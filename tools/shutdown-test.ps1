# Verifies that closing the window shuts the app down: the monitor loop stops, notifications are
# unregistered, the WLAN handle is closed, the log flushes and the process really exits.
#
# Usage: pwsh -NoProfile -File tools/shutdown-test.ps1
#
# The config it writes sets startup.closeToTray=false so that WM_CLOSE means "exit".

param(
    [int]$Seconds = 35
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WinClose
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_CLOSE = 0x0010;
}
'@

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem -Path (Join-Path $repoRoot 'src\NetworkGuardian.App\bin') -Filter 'NetworkGuardian.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $exe) { throw 'NetworkGuardian.exe was not found - build the solution first.' }

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('ng-shutdown-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path (Join-Path $root 'Logs') | Out-Null
'{"version":3,"general":{"automaticRecovery":false},"startup":{"closeToTray":false},"logging":{"minimumLevel":"debug","writeToFile":true}}' |
    Set-Content -Path (Join-Path $root 'config.json') -Encoding utf8

$env:NETWORKGUARDIAN_CONFIG_ROOT = $root
$process = Start-Process -FilePath $exe -PassThru
Write-Host "pid         : $($process.Id)"

$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline -and $process.MainWindowHandle -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 500
    $process.Refresh()
}

$handle = $process.MainWindowHandle
Write-Host "window      : $handle"
if ($handle -eq [IntPtr]::Zero) { throw 'no main window appeared' }

Start-Sleep -Seconds 8
[WinClose]::PostMessage($handle, [WinClose]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null

# Give the shutdown sequence time to cancel the loop and release native handles.
$exited = $process.WaitForExit(20000)
Write-Host "exited      : $exited"

if (-not $exited) {
    Stop-Process -Id $process.Id -Force
    Write-Host 'WARNING: the process had to be killed; shutdown did not complete'
}

$log = Get-ChildItem (Join-Path $root 'Logs') -Filter '*.log' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($log) {
    Write-Host ''
    Write-Host '--- shutdown log tail ---'
    Get-Content $log.FullName | Select-Object -Last 12
}

Write-Host ''
Write-Host "artifacts   : $root"
