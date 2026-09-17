# Verifies that the release package runs on a machine without any development environment:
#   - the package is copied to a neutral directory outside the build tree
#   - the app is launched with a sanitized environment (no dotnet on PATH, no DOTNET_ROOT)
#   - the privileged helper is executed for real and its response file is checked
#
# Usage: pwsh -NoProfile -File tools/verify-release.ps1 [-InstanceId '<PnP instance id>']

param(
    [string]$PackageDirectory = '',
    [string]$InstanceId = '',
    [int]$Seconds = 20
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repoRoot 'release'
}

if (-not (Test-Path (Join-Path $PackageDirectory 'NetworkGuardian.exe'))) {
    throw "no NetworkGuardian.exe in $PackageDirectory - run tools/build-release.ps1 first."
}

$failures = New-Object System.Collections.Generic.List[string]
$checks = New-Object System.Collections.Generic.List[string]

function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $line = if ($Detail) { "$Name - $Detail" } else { $Name }
    if ($Ok) { $checks.Add("PASS  $line"); Write-Host "PASS  $line" -ForegroundColor Green }
    else { $failures.Add("FAIL  $line"); Write-Host "FAIL  $line" -ForegroundColor Red }
}

# ---------------------------------------------------------------- package contents
Write-Host '== package contents' -ForegroundColor Cyan

$required = @(
    'NetworkGuardian.exe', 'NetworkGuardian.dll', 'NetworkGuardian.runtimeconfig.json',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
    'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll',
    'helper\NetworkGuardian.Helper.exe'
)
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $PackageDirectory $_)) })
Check 'required runtime and helper files exist' ($missing.Count -eq 0) ($missing -join ', ')

$runtimeConfig = Get-Content (Join-Path $PackageDirectory 'NetworkGuardian.runtimeconfig.json') -Raw
Check 'app is self-contained (includedFrameworks)' ($runtimeConfig -match 'includedFrameworks')

# The helper either carries the runtime as loose files or bundled inside a single file.
$helperCoreClr = Join-Path $PackageDirectory 'helper\coreclr.dll'
if (Test-Path $helperCoreClr) {
    $helperRuntimeConfig = Get-Content (Join-Path $PackageDirectory 'helper\NetworkGuardian.Helper.runtimeconfig.json') -Raw
    Check 'helper is self-contained (includedFrameworks)' ($helperRuntimeConfig -match 'includedFrameworks')
}
else {
    $helperSizeMb = [math]::Round((Get-Item (Join-Path $PackageDirectory 'helper\NetworkGuardian.Helper.exe')).Length / 1MB, 1)
    Check 'helper carries its own runtime (single-file bundle)' ($helperSizeMb -ge 20) "$helperSizeMb MB"
}

$devPathHits = @()
foreach ($file in @('NetworkGuardian.deps.json', 'NetworkGuardian.runtimeconfig.json', 'helper\NetworkGuardian.Helper.deps.json')) {
    $path = Join-Path $PackageDirectory $file
    if (Test-Path $path) {
        if ((Get-Content $path -Raw) -match [Regex]::Escape($repoRoot.Replace('/', '\'))) { $devPathHits += $file }
    }
}
Check 'no build-tree paths baked into the deployment manifests' ($devPathHits.Count -eq 0) ($devPathHits -join ', ')

Check 'no PDB files shipped' (@(Get-ChildItem $PackageDirectory -Filter '*.pdb' -Recurse).Count -eq 0)
Check 'release notes present' (Test-Path (Join-Path $PackageDirectory '发布说明.txt'))

# ---------------------------------------------------------------- neutral copy + clean environment
Write-Host ''
Write-Host '== run from a neutral directory with a sanitized environment' -ForegroundColor Cyan

$verifyRoot = Join-Path $env:TEMP ('ng-release-verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$packageCopy = Join-Path $verifyRoot 'NetworkGuardian'
$configRoot = Join-Path $verifyRoot 'config'
New-Item -ItemType Directory -Force -Path $packageCopy, $configRoot | Out-Null

Copy-Item -Path (Join-Path $PackageDirectory '*') -Destination $packageCopy -Recurse -Force
Write-Host "copied package : $packageCopy"

# A machine without a development environment has no dotnet on PATH and no DOTNET_ROOT.
$savedPath = $env:PATH
$savedDotnetRoot = $env:DOTNET_ROOT
$savedConfigRoot = $env:NETWORKGUARDIAN_CONFIG_ROOT
$dotnetProcesses = @(Get-Process -Name 'NetworkGuardian*' -ErrorAction SilentlyContinue)
if ($dotnetProcesses.Count -gt 0) {
    $dotnetProcesses | Stop-Process -Force
    Start-Sleep -Seconds 1
}

$appProcess = $null
$helperResult = $null
try {
    $env:PATH = 'C:\Windows\system32;C:\Windows'
    Remove-Item Env:\DOTNET_ROOT -ErrorAction SilentlyContinue
    $env:NETWORKGUARDIAN_CONFIG_ROOT = $configRoot

    Write-Host "PATH           : $env:PATH"
    Write-Host "DOTNET_ROOT    : $([string]::IsNullOrEmpty($env:DOTNET_ROOT) ? '<unset>' : $env:DOTNET_ROOT)"

    $appProcess = Start-Process -FilePath (Join-Path $packageCopy 'NetworkGuardian.exe') -ArgumentList '--minimized' -PassThru
    Start-Sleep -Seconds $Seconds
    $appProcess.Refresh()

    Check 'app stays alive without a development environment' (-not $appProcess.HasExited)

    $window = $null
    if (-not $appProcess.HasExited) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class VerifyWindows
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    public static string Describe(uint pid)
    {
        string found = null;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner != pid) { return true; }
            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);
            if (cls.ToString().StartsWith("WinUIDesktopWin32WindowClass", StringComparison.Ordinal)) { found = cls.ToString(); return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
        $window = [VerifyWindows]::Describe([uint32]$appProcess.Id)
    }

    Check 'WinUI window class created' (-not [string]::IsNullOrEmpty($window)) ([string]$window)

    $logFile = Get-ChildItem (Join-Path $configRoot 'Logs') -Filter '*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $logText = if ($logFile) { Get-Content $logFile.FullName -Raw } else { '' }
    Check 'log file created and written' ($logText -match 'NetworkGuardian starting')
    Check 'WLAN client handle opened' ($logText -match 'WLAN client handle opened')
    Check 'config created on first run' (Test-Path (Join-Path $configRoot 'config.json'))

    # ------------------------------------------------------------ privileged helper
    Write-Host ''
    Write-Host '== privileged helper' -ForegroundColor Cyan

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $elevated) {
        Write-Host 'SKIP  helper execution requires an elevated session (the helper manifest is requireAdministrator)' -ForegroundColor Yellow
    }
    else {
        $targetId = if ([string]::IsNullOrWhiteSpace($InstanceId)) { 'USB\VID_0000&PID_0000\NOT-A-REAL-DEVICE' } else { $InstanceId }
        $requestPath = Join-Path $configRoot 'helper-request.json'
        $responsePath = Join-Path $configRoot 'helper-response.json'
        @{
            protocolVersion = 1
            operation = 'query-status'
            deviceInstanceId = $targetId
            requirePhysicalDevice = $true
            requestedBy = 'verify-release'
            requestedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json | Set-Content -Path $requestPath -Encoding utf8

        $helperExe = Join-Path $packageCopy 'helper\NetworkGuardian.Helper.exe'
        $helperArgs = @{ FilePath = $helperExe; ArgumentList = @('--request', $requestPath, '--response', $responsePath); PassThru = $true; Wait = $true; WindowStyle = 'Hidden' }
        $helper = Start-Process @helperArgs

        Check 'helper runs from the package (exit code)' ($null -ne $helper) ("exit code $($helper.ExitCode)")

        if (Test-Path $responsePath) {
            $helperResult = Get-Content $responsePath -Raw | ConvertFrom-Json
            Write-Host ("      response: success={0} outcome={1} helperElevated={2} version={3}" -f
                $helperResult.success, $helperResult.outcome, $helperResult.helperElevated, $helperResult.helperVersion)
            Check 'helper reports its protocol response' ($null -ne $helperResult.operation)
            Check 'helper saw an elevated token' ([bool]$helperResult.helperElevated)

            if ([string]::IsNullOrWhiteSpace($InstanceId)) {
                # The bogus id must be rejected gracefully instead of crashing or acting on something else.
                Check 'unknown device is rejected gracefully' (-not $helperResult.success)
            }
            else {
                Check 'real device can be queried' ([bool]$helperResult.success -or $helperResult.outcome -eq 'DriverProblem')
            }
        }
        else {
            Check 'helper wrote a response file' $false $responsePath
        }
    }
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue }
    $env:PATH = $savedPath
    if ($savedDotnetRoot) { $env:DOTNET_ROOT = $savedDotnetRoot }
    if ($savedConfigRoot) { $env:NETWORKGUARDIAN_CONFIG_ROOT = $savedConfigRoot } else { Remove-Item Env:\NETWORKGUARDIAN_CONFIG_ROOT -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "reports: $($checks.Count) passed, $($failures.Count) failed" -ForegroundColor ($(if ($failures.Count -eq 0) { 'Green' } else { 'Red' }))
Write-Host "artifacts left in: $verifyRoot"
if ($failures.Count -gt 0) { exit 1 }
exit 0
