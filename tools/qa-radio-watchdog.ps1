# Verifies the radio watchdog on the real machine.
#
# The check: start NetworkGuardian, switch one adapter's software radio off the way Windows Settings
# does (Radio Manager via tools/qa-wifi-radio-winrt.ps1), and require it to be back on within a few
# seconds - the watchdog polls every general.radioWatchdogSeconds (3 by default) and switches a
# software-off radio back on immediately.
#
# The run uses its own configuration root, so the user's config.json and network library are untouched.
# The temporary profile raises the disconnect grace, which keeps the recovery engine from connecting
# anything while the check runs.
#
# Usage:
#   pwsh -NoProfile -File tools/qa-radio-watchdog.ps1
#   pwsh -NoProfile -File tools/qa-radio-watchdog.ps1 -TimeoutSeconds 20 -RadioIndex 2

param(
    [int]$TimeoutSeconds = 25,
    [int]$RadioIndex = -1,
    [string]$Exe = '',
    [int]$Rounds = 3
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = if ($Exe) { $Exe } else {
    Get-ChildItem -Recurse -Filter 'NetworkGuardian.exe' -Path (Join-Path $repoRoot 'src\NetworkGuardian.Portable\bin') |
        Where-Object { $_.FullName -notmatch '\\native\\' } |
        Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
}

if (-not $exe -or -not (Test-Path $exe)) { throw 'no NetworkGuardian.exe build found; run dotnet build first' }

$winrtRadio = Join-Path $PSScriptRoot 'qa-wifi-radio-winrt.ps1'
$root = Join-Path $env:TEMP ('ng-radio-qa-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Get-RadioStates {
    $states = @()
    $current = $null
    foreach ($line in (netsh wlan show interfaces)) {
        if ($line -match '^\s{4}Name\s+:\s(.+?)\s*$') {
            $current = [pscustomobject]@{ Name = $Matches[1]; Description = ''; Software = '' }
            $states += $current
        }
        elseif ($line -match '^\s{4}Description\s+:\s(.+?)\s*$' -and $current) { $current.Description = $Matches[1] }
        elseif ($line -match '^\s+Software\s+(On|Off)\s*$' -and $current) { $current.Software = $Matches[1] }
    }
    return $states
}

function Show-Radios {
    param([string]$Title)
    Write-Host "== $Title"
    Get-RadioStates | ForEach-Object { Write-Host ("   {0,-10} {1,-45} software={2}" -f $_.Name, $_.Description, $_.Software) }
}

# The configuration: defaults everywhere except the radio watchdog cadence and the disconnect grace.
$config = @'
{
  "version": 5,
  "general": { "radioWatchdogEnabled": true, "radioWatchdogSeconds": 3, "healthSweepSeconds": 10 },
  "wifi": { "disconnectGraceSeconds": 900, "minimumSignalQuality": 100 },
  "startup": { "closeToTray": false, "startMinimized": true },
  "logging": { "minimumLevel": "debug", "verboseNetwork": true }
}
'@
[System.IO.File]::WriteAllText((Join-Path $root 'config.json'), $config, [System.Text.UTF8Encoding]::new($false))

Show-Radios 'radio state before'

$previousRoot = $env:NETWORKGUARDIAN_CONFIG_ROOT
$previousSuffix = $env:NETWORKGUARDIAN_INSTANCE_SUFFIX
$env:NETWORKGUARDIAN_CONFIG_ROOT = $root
$env:NETWORKGUARDIAN_INSTANCE_SUFFIX = '-radio-qa'

$process = $null
$restored = $null

try {
    Write-Host "== starting $exe"
    Write-Host "   (config root $root)"
    $process = Start-Process -FilePath $exe -ArgumentList '--minimized' -PassThru

    # Wait until the host is up, so the flip happens while the watchdog is already running.
    $logDirectory = Join-Path $root 'Logs'
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $log = Get-ChildItem -Path $logDirectory -Filter '*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if ($log -and (Select-String -Path $log.FullName -Pattern 'Guardian host started' -Quiet)) { break }
    }

    # Several rounds on purpose: the decision engine may also switch the radio back on when it sees the
    # radio-state notification, but it is rate limited (one attempt per 30 s). From the second round on,
    # the watchdog is the only thing that can restore the switch - which is exactly what is being
    # verified here.
    # ($Rounds is the parameter; PowerShell variables are case insensitive, so this list needs another name.)
    $measurements = @()
    for ($round = 1; $round -le [Math]::Max(1, $Rounds); $round++) {
        Write-Host "== round $round : switching one adapter software radio off (Radio Manager, like Windows Settings)"
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $winrtRadio -Action off -Index $RadioIndex | Out-Host
        Start-Sleep -Milliseconds 300

        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $backOn = $null
        while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            Start-Sleep -Milliseconds 200
            if (@(Get-RadioStates | Where-Object { $_.Software -ne 'On' }).Count -eq 0) {
                $backOn = $watch.Elapsed.TotalSeconds
                break
            }
        }

        $measurements += [pscustomobject]@{ Round = $round; RestoredAfterSeconds = $backOn }
        Write-Host ("   restored after: " + $(if ($backOn) { "$([Math]::Round($backOn, 2))s" } else { "never (${TimeoutSeconds}s timeout)" }))

        if (-not $backOn) { break }
        Start-Sleep -Seconds 4
    }

    Show-Radios 'radio state at the end'

    $unrestored = @($measurements | Where-Object { -not $_.RestoredAfterSeconds })
    if ($unrestored.Count -eq 0) { $restored = ($measurements | Measure-Object -Property RestoredAfterSeconds -Maximum).Maximum }
}
finally {
    if ($process -and -not $process.HasExited) {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'shutdown-test.ps1') -Seconds 10 | Out-Null
        Start-Sleep -Seconds 2
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }

    $env:NETWORKGUARDIAN_CONFIG_ROOT = $previousRoot
    $env:NETWORKGUARDIAN_INSTANCE_SUFFIX = $previousSuffix
}

$log = Get-ChildItem -Path (Join-Path $root 'Logs') -Filter '*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    $watchdogLines = Select-String -Path $log.FullName -Pattern '无线电看门狗'
    Write-Host "== radio log lines (watchdog lines: $($watchdogLines.Count))"
    Select-String -Path $log.FullName -Pattern '看门狗|software radio|radio state|radio enabled' |
        Select-Object -First 14 | ForEach-Object { Write-Host "   $($_.Line)" }

    if ($watchdogLines.Count -gt 0) {
        Write-Host '== watchdog switched a radio back on:'
        $watchdogLines | Select-Object -First 4 | ForEach-Object { Write-Host "   $($_.Line)" }
    }
}

Write-Host ''
if ($restored) {
    Write-Host "PASS: the switched-off software radio was on again after $([Math]::Round($restored, 1))s"
    Write-Host "      log: $($log.FullName)"
    Write-Host "      config root kept for inspection: $root"
}
else {
    Write-Host "FAIL: the software radio was still off after $TimeoutSeconds s"
    Write-Host "      log: $($log.FullName)"
    exit 1
}
