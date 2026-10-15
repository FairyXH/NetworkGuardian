param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$ReportDirectory,
    [int]$Seconds = 20,
    [switch]$Visible,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$qaExe = (Resolve-Path -LiteralPath $Exe).Path
$qaRoot = [System.IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Force -Path $qaRoot | Out-Null
$qaConfig = @{
    version = 15
    general = @{
        automaticRecovery = $false
        manageInterfaceMetrics = $false
        ensureRadioOnAtStartup = $false
        autoEnableWifiRadio = $false
        autoEnableWifiDevices = $false
        radioWatchdogEnabled = $false
        healthSweepSeconds = 5
    }
    startup = @{ startMinimized = -not [bool]$Visible; closeToTray = $false; runAtLogon = $false }
    logging = @{ minimumLevel = 'debug'; writeToFile = $true }
    quality = @{ enabled = $true; sampleIntervalSeconds = 15 }
}
$configPath = Join-Path $qaRoot 'config.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    $qaConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding utf8
}
$oldRoot = $env:NETWORKGUARDIAN_CONFIG_ROOT
$oldSuffix = $env:NETWORKGUARDIAN_INSTANCE_SUFFIX
try {
    $env:NETWORKGUARDIAN_CONFIG_ROOT = $qaRoot
    $env:NETWORKGUARDIAN_INSTANCE_SUFFIX = '.qa-' + [Guid]::NewGuid().ToString('N')
    $qaArguments = if ($Visible) { @('--visible', '--page', 'dashboard') } else { @('--minimized') }
    $qaStyle = if ($Visible) { 'Normal' } else { 'Hidden' }
    $qaProcess = Start-Process -FilePath $qaExe -ArgumentList $qaArguments -WindowStyle $qaStyle -PassThru
    Write-Output "QA process $($qaProcess.Id), configuration $qaRoot"
    Start-Sleep -Seconds 5
    $qaProcess.Refresh()
    if ($qaProcess.HasExited) { throw "Application exited during startup: $($qaProcess.ExitCode)" }
    $cpuBefore = $qaProcess.TotalProcessorTime.TotalMilliseconds
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $memorySamples = @()
    for ($sample = 0; $sample -lt $Seconds; $sample++) {
        Start-Sleep -Seconds 1
        $qaProcess.Refresh()
        if ($qaProcess.HasExited) { throw "Application exited during measurement: $($qaProcess.ExitCode)" }
        $memorySamples += $qaProcess.WorkingSet64
    }
    $result = [ordered]@{
        exe = $qaExe
        processId = $qaProcess.Id
        seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2)
        cpuPercentOfOneCore = [Math]::Round(($qaProcess.TotalProcessorTime.TotalMilliseconds - $cpuBefore) / $clock.Elapsed.TotalMilliseconds * 100, 2)
        averageWorkingSetMb = [Math]::Round(($memorySamples | Measure-Object -Average).Average / 1MB, 2)
        privateMemoryMb = [Math]::Round($qaProcess.PrivateMemorySize64 / 1MB, 2)
        handles = $qaProcess.HandleCount
        exeBytes = (Get-Item -LiteralPath $qaExe).Length
    }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $qaRoot 'runtime-metrics.json') -Encoding utf8
    $result | ConvertTo-Json
}
finally {
    $env:NETWORKGUARDIAN_CONFIG_ROOT = $oldRoot
    $env:NETWORKGUARDIAN_INSTANCE_SUFFIX = $oldSuffix
    if ($qaProcess -and -not $KeepRunning -and -not $qaProcess.HasExited) { Stop-Process -Id $qaProcess.Id }
}
