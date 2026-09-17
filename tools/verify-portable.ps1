# Verifies the portable package (release\ by default) the way it will be used: copied to a neutral
# directory and started
# with a sanitised environment (no dotnet on PATH, no DOTNET_ROOT, isolated configuration root).
#
# Usage:
#   pwsh -NoProfile -File tools/verify-portable.ps1
#   pwsh -NoProfile -File tools/verify-portable.ps1 -InstanceId 'USB\VID_0BDA&PID_8153\001000001'
#
# Checks: package contents, single-file/native AOT proof, size gate, start-up with a clean environment,
# the window class, window responsiveness, log + configuration side effects, the privileged helper run
# for real, graceful shutdown through WM_CLOSE and leftover-free cleanup.

param(
    [string]$PackageDir = 'release',
    [double]$MaxExeMb = 8.0,
    [int]$Seconds = 20,
    [string]$InstanceId = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$packagePath = if ([System.IO.Path]::IsPathRooted($PackageDir)) { $PackageDir } else { Join-Path $repoRoot $PackageDir }
$windowClass = 'NetworkGuardianPortableWindow'

$script:passed = 0
$script:failed = 0

function Check($name, $condition, $detail = '') {
    if ($condition) {
        $script:passed++
        Write-Host "  [PASS] $name"
    }
    else {
        $script:failed++
        Write-Host "  [FAIL] $name $detail" -ForegroundColor Red
    }
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class NgVerify
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder buffer, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    public const uint WM_CLOSE = 0x0010;
    public const uint SMTO_BLOCK_ABORTIFHUNG = 0x0003;

    public static IntPtr FindWindow(uint pid, string className)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            uint windowPid;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid != pid) { return true; }

            var buffer = new StringBuilder(256);
            GetClassNameW(hWnd, buffer, buffer.Capacity);
            if (buffer.ToString() != className) { return true; }

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }
}
'@

Write-Host "package : $packagePath"
Write-Host ''

# ---------- 1. contents ----------

Write-Host '== package contents'
$exePath = Join-Path $packagePath 'NetworkGuardian.exe'
$helperPath = Join-Path $packagePath 'helper\NetworkGuardian.Helper.exe'
$notesPath = Join-Path $packagePath '发布说明.txt'

Check 'NetworkGuardian.exe exists' (Test-Path $exePath)
Check 'helper\NetworkGuardian.Helper.exe exists' (Test-Path $helperPath)
Check '发布说明.txt exists' (Test-Path $notesPath)

if (-not (Test-Path $exePath)) { throw 'nothing to verify' }

$exeMb = [Math]::Round((Get-Item $exePath).Length / 1MB, 2)
Check "NetworkGuardian.exe is within the $MaxExeMb MB gate" ($exeMb -le $MaxExeMb) "(actual $exeMb MB; measured 248.8 MB / 515 files for the WinUI build)"
Write-Host "         exe $exeMb MB, helper $([Math]::Round((Get-Item $helperPath).Length / 1MB, 2)) MB"

# ---------- 2. single file / no runtime payload ----------

Write-Host '== self-containment'
$loose = Get-ChildItem -Path $packagePath -File | Where-Object { $_.Extension -in '.dll', '.json', '.pdb', '.config' }
Check 'no loose framework files next to the exe (single native file)' ($loose.Count -eq 0) "($($loose.Name -join ', '))"
Check 'no debug symbols anywhere in the package' ((Get-ChildItem -Path $packagePath -Recurse -Filter '*.pdb').Count -eq 0)

# ---------- 3. neutral directory + clean environment ----------

Write-Host '== start-up in a neutral directory'
$neutral = Join-Path $env:TEMP ('ng-verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$packageCopy = Join-Path $neutral 'package'
$root = Join-Path $neutral 'config'
New-Item -ItemType Directory -Force -Path $packageCopy, $root, (Join-Path $root 'Logs') | Out-Null
Copy-Item -Path (Join-Path $packagePath '*') -Destination $packageCopy -Recurse -Force

# closeToTray=false so WM_CLOSE really exits; the rest is left at the defaults on purpose.
'{"version":3,"general":{"automaticRecovery":false,"healthSweepSeconds":10},"startup":{"startMinimized":false,"closeToTray":false},"logging":{"minimumLevel":"debug","writeToFile":true}}' |
    Set-Content -Path (Join-Path $root 'config.json') -Encoding utf8

$env:NETWORKGUARDIAN_CONFIG_ROOT = $root
$env:NETWORKGUARDIAN_INSTANCE_SUFFIX = '.verify' + [Guid]::NewGuid().ToString('N').Substring(0, 5)
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
$savedPath = $env:PATH
$env:PATH = 'C:\Windows\system32;C:\Windows'

$exe = Join-Path $packageCopy 'NetworkGuardian.exe'
$process = Start-Process -FilePath $exe -ArgumentList '--visible', '--page', 'dashboard' -PassThru
Write-Host "         pid $($process.Id), PATH=$env:PATH, DOTNET_ROOT unset"

try {
    $handle = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt ($Seconds * 2); $attempt++) {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited) { break }
        $handle = [NgVerify]::FindWindow([uint32]$process.Id, $windowClass)
        if ($handle -ne [IntPtr]::Zero) { break }
    }

    Check 'process is still running' (-not $process.HasExited)
    Check "window class $windowClass exists" ($handle -ne [IntPtr]::Zero)

    # A blocked (not pumping) window thread is exactly the failure mode that a self-drawn UI can hide.
    $result = [IntPtr]::Zero
    $answered = [NgVerify]::SendMessageTimeout($handle, 0, [IntPtr]::Zero, [IntPtr]::Zero, [NgVerify]::SMTO_BLOCK_ABORTIFHUNG, 3000, [ref]$result)
    Check 'window thread answers messages' ($answered -ne [IntPtr]::Zero)

    Start-Sleep -Seconds 6

    $log = Get-ChildItem (Join-Path $root 'Logs') -Filter '*.log' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check 'a log file was written' ($null -ne $log)

    if ($log) {
        $logText = Get-Content $log.FullName -Raw
        Check 'log records the start-up' ($logText -match 'NetworkGuardian starting')
        Check 'log records the guardian host' ($logText -match 'Guardian host started')
        Check 'log records device enumeration' ($logText -match 'Enumeration complete')
        Check 'log records a connectivity probe' ($logText -match 'Connectivity probe')
        Check 'log records the tray icon' ($logText -match 'Tray icon created')
    }

    $configFile = Join-Path $root 'config.json'
    Check 'configuration file exists after the first run' (Test-Path $configFile)
    if (Test-Path $configFile) {
        $configText = Get-Content $configFile -Raw
        Check 'configuration keeps the documented camelCase keys' ($configText -match '"probeEndpoints"' -and $configText -match '"healthSweepSeconds"')
        Check 'configuration contains no Wi-Fi key material' ($configText -notmatch '(?i)psk|password|keymaterial')
    }

    # ---------- 4. the privileged helper runs for real ----------

    Write-Host '== privileged helper'
    $helperDir = Join-Path $root 'helper'
    $requestPath = Join-Path $helperDir 'request-verify.json'
    $responsePath = Join-Path $helperDir 'response-verify.json'
    '{"protocolVersion":1,"operation":"query-status","deviceInstanceId":"BOGUS\\DEVICE\\0000","requirePhysicalDevice":true}' |
        Set-Content -Path $requestPath -Encoding utf8

    $helper = Start-Process -FilePath (Join-Path $packageCopy 'helper\NetworkGuardian.Helper.exe') `
        -ArgumentList '--request', $requestPath, '--response', $responsePath -Wait -PassThru
    Start-Sleep -Milliseconds 500

    Check 'helper produced a response file' (Test-Path $responsePath)
    if (Test-Path $responsePath) {
        $response = Get-Content $responsePath -Raw | ConvertFrom-Json
        Check 'helper refuses an unknown device without touching hardware' ($response.outcome -eq 'DeviceNotFound') "(outcome $($response.outcome))"
        Check 'helper reports its elevation state' ($null -ne $response.helperElevated)
        Write-Host "         helperElevated=$($response.helperElevated), helperVersion=$($response.helperVersion)"
    }

    if ($InstanceId) {
        $requestPath2 = Join-Path $helperDir 'request-verify2.json'
        $responsePath2 = Join-Path $helperDir 'response-verify2.json'
        (@{ protocolVersion = 1; operation = 'query-status'; deviceInstanceId = $InstanceId; requirePhysicalDevice = $true } | ConvertTo-Json -Compress) |
            Set-Content -Path $requestPath2 -Encoding utf8

        Start-Process -FilePath (Join-Path $packageCopy 'helper\NetworkGuardian.Helper.exe') `
            -ArgumentList '--request', $requestPath2, '--response', $responsePath2 -Wait | Out-Null
        Start-Sleep -Milliseconds 500

        $response2 = Get-Content $responsePath2 -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
        Check 'helper answers for a real device' ($null -ne $response2) "(device $InstanceId)"
        if ($response2) {
            Write-Host "         outcome=$($response2.outcome) elevated=$($response2.helperElevated) problemCodeAfter=$($response2.problemCodeAfter)"

            if ($response2.helperElevated) {
                Check 'helper reports the real device as enabled' ($response2.outcome -in @('Succeeded', 'AlreadyInDesiredState', 'RebootRequired')) "(outcome $($response2.outcome))"
            }
            else {
                Check 'a non elevated helper refuses to act' ($response2.outcome -eq 'AccessDenied') "(outcome $($response2.outcome))"
            }
        }
    }

    # ---------- 5. graceful shutdown ----------

    Write-Host '== shutdown'
    [void][NgVerify]::PostMessageW($handle, [NgVerify]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)

    $exited = $process.WaitForExit($Seconds * 1000)
    Check 'WM_CLOSE makes the process exit on its own' $exited

    if ($log) {
        $logText = Get-Content $log.FullName -Raw
        Check 'shutdown logs the close request' ($logText -match 'Main window closed; shutting down')
        Check 'shutdown logs the monitor loop stop' ($logText -match 'Guardian monitor loop stopped')
        Check 'shutdown logs the WLAN handle release' ($logText -match 'WLAN client handle closed')
        Check 'shutdown logs the host disposal' ($logText -match 'Guardian host disposed')
        Check 'shutdown logs the final stop' ($logText -match 'NetworkGuardian stopped')

        # A second log file means a line was written after the writer had been disposed.
        $logFiles = Get-ChildItem (Join-Path $root 'Logs') -Filter '*.log' -ErrorAction SilentlyContinue
        Check 'the shutdown sequence stays in a single log file' ($logFiles.Count -eq 1) "($($logFiles.Name -join ', '))"
    }

    # ---------- 6. no leftovers ----------

    Write-Host '== leftovers'
    # Remove the exchange files this script created; anything left is an app leftover.
    # (-Include needs -Recurse or a wildcard path, so filter by name explicitly.)
    Get-ChildItem $helperDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'request-verify*.json' -or $_.Name -like 'response-verify*.json' } |
        Remove-Item -Force -ErrorAction SilentlyContinue
    $helperLeftovers = Get-ChildItem $helperDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'helper.log' }
    Check 'no helper exchange files are left behind' ($helperLeftovers.Count -eq 0) "($($helperLeftovers.Name -join ', '))"
    Check 'the helper wrote its audit log' (Test-Path (Join-Path $helperDir 'helper.log'))
}
finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $env:PATH = $savedPath
    Remove-Item Env:NETWORKGUARDIAN_CONFIG_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:NETWORKGUARDIAN_INSTANCE_SUFFIX -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "result: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) {
    Write-Host "artifacts kept for inspection: $neutral"
    exit 1
}

Remove-Item -Recurse -Force $neutral -ErrorAction SilentlyContinue
exit 0
