# Launches the built portable NetworkGuardian against a throwaway configuration root and reports what
# happened (device enumeration, radio state, probing, logging).
#
# Usage: pwsh -NoProfile -File tools/smoke-run.ps1 [-Recovery] [-Seconds 25]
#
# Without -Recovery the background recovery loop is switched off (automaticRecovery=false) so the run
# only observes: nothing on the machine is touched.

param(
    [switch]$Recovery,
    [int]$Seconds = 25
)

$ErrorActionPreference = 'Stop'

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

Write-Host "exe         : $exe"

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('ng-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$logs = Join-Path $root 'Logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

$config = @{
    version    = 3
    general    = @{
        automaticRecovery = [bool]$Recovery
        healthSweepSeconds = 10
        enumerationRefreshSeconds = 20
    }
    startup    = @{
        startMinimized = $true
        closeToTray = $false
    }
    logging    = @{
        minimumLevel = 'debug'
        writeToFile = $true
    }
}

$config | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $root 'config.json') -Encoding utf8

Write-Host "config root : $root"
Write-Host "recovery    : $([bool]$Recovery)"

$env:NETWORKGUARDIAN_CONFIG_ROOT = $root
$env:NETWORKGUARDIAN_INSTANCE_SUFFIX = '.smoke' + [Guid]::NewGuid().ToString('N').Substring(0, 5)
$process = Start-Process -FilePath $exe -ArgumentList '--minimized' -PassThru
Write-Host "pid         : $($process.Id)"

Start-Sleep -Seconds $Seconds

$alive = -not $process.HasExited
Write-Host "alive after ${Seconds}s : $alive"

if ($alive) {
    $process.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 3
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        Write-Host 'stopped     : force killed (the window is hidden in the tray, WM_CLOSE was not delivered)'
    }
    else {
        Write-Host 'stopped     : graceful close'
    }
}
else {
    Write-Host "exit code   : $($process.ExitCode)"
}

$logFile = Get-ChildItem $logs -Filter '*.log' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($logFile) {
    $lines = Get-Content $logFile.FullName
    Write-Host ''
    Write-Host "--- $($logFile.Name) ($($lines.Count) lines) ---"
    $lines | Select-Object -First 400
}
else {
    Write-Host 'no log file was produced'
    exit 1
}

Write-Host ''
Write-Host "artifacts   : $root"
