# Verifies that closing the window shuts the portable app down cleanly: the monitor loop stops, WLAN
# notifications are unregistered, the WLAN handle is closed, the log is flushed and the process exits
# on its own (no forced kill).
#
# Usage: pwsh -NoProfile -File tools/shutdown-test.ps1 [-Seconds 35]
#
# The configuration it writes sets startup.closeToTray=false so that WM_CLOSE means "exit".

param(
    [int]$Seconds = 35
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class NgClose
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder buffer, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_CLOSE = 0x0010;

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

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem -Path (Join-Path $repoRoot 'src\NetworkGuardian.Portable\bin') -Filter 'NetworkGuardian.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $exe) {
    $published = Join-Path $repoRoot 'portable\NetworkGuardian.exe'
    if (Test-Path $published) { $exe = $published }
}

if (-not $exe) {
    throw 'NetworkGuardian.exe was not found - build the portable project first.'
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('ng-shutdown-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path (Join-Path $root 'Logs') | Out-Null
'{"version":3,"general":{"automaticRecovery":false,"healthSweepSeconds":10},"startup":{"startMinimized":false,"closeToTray":false},"logging":{"minimumLevel":"debug","writeToFile":true}}' |
    Set-Content -Path (Join-Path $root 'config.json') -Encoding utf8

$env:NETWORKGUARDIAN_CONFIG_ROOT = $root
$env:NETWORKGUARDIAN_INSTANCE_SUFFIX = '.shutdown' + [Guid]::NewGuid().ToString('N').Substring(0, 5)

Write-Host "exe    : $exe"
Write-Host "config : $root"

$process = Start-Process -FilePath $exe -ArgumentList '--visible', '--page', 'dashboard' -PassThru
Write-Host "pid    : $($process.Id)"

# Let the app finish its first cycle so the log contains the start-up sequence.
Start-Sleep -Seconds 12

$handle = [NgClose]::FindWindow([uint32]$process.Id, 'NetworkGuardianPortableWindow')
if ($handle -eq [IntPtr]::Zero) {
    if (-not $process.HasExited) { $process.Kill() }
    throw 'the NetworkGuardian window was not found'
}

Write-Host "window : $handle"
[void][NgClose]::PostMessageW($handle, [NgClose]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)

$exited = $process.WaitForExit($Seconds * 1000)
Write-Host "exited : $exited (after at most $Seconds s, exit code $($process.ExitCode))"

if (-not $exited) {
    $process.Kill()
    Write-Warning 'the process had to be killed - the shutdown path is broken'
    exit 1
}

$log = Get-ChildItem (Join-Path $root 'Logs') -Filter '*.log' | Select-Object -First 1
$text = Get-Content $log.FullName -Raw

$expected = @(
    'Main window closed; shutting down',
    'Guardian monitor loop stopped',
    'Unregistered WLAN notifications',
    'WLAN client handle closed',
    'Guardian host disposed',
    'NetworkGuardian stopped'
)

$missing = $expected | Where-Object { $text -notmatch [Regex]::Escape($_) }
if ($missing) {
    Write-Warning "missing log lines: $($missing -join '; ')"
    Write-Host ''
    Get-Content $log.FullName | Select-Object -Last 25
    exit 1
}

Write-Host ''
Write-Host 'shutdown sequence:'
Get-Content $log.FullName | Select-String -Pattern 'Main window closed|monitor loop stopped|Unregistered WLAN|WLAN client handle closed|Guardian host disposed|NetworkGuardian stopped' |
    ForEach-Object { Write-Host "  $($_.Line)" }

Write-Host ''
Write-Host 'PASS'
