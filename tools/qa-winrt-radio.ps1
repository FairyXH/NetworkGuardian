# Lists the Windows Radio objects (one per Wi-Fi adapter, plus Bluetooth) - these are what the
# individual Wi-Fi switches in Windows Settings -> Network & internet -> Wi-Fi control.
#
# Usage:
#   pwsh -NoProfile -File tools/qa-winrt-radio.ps1                          # list
#   pwsh -NoProfile -File tools/qa-winrt-radio.ps1 -Name 'WLAN' -State Off  # set one radio
#
# Note: this uses the WinRT projection, which a Native AOT application cannot consume; it is a probe
# for the QA tooling, not part of the shipped app.

param(
    [string]$Name = '',
    [ValidateSet('On', 'Off')]
    [string]$State = 'On'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]

function Await($operation, $type) {
    $task = $asTaskGeneric.MakeGenericMethod($type).Invoke($null, @($operation))
    $task.Wait(-1) | Out-Null
    $task.Result
}

[Windows.Devices.Radios.Radio, Windows.System.Devices, ContentType = WindowsRuntime] | Out-Null
[Windows.Devices.Radios.RadioState, Windows.System.Devices, ContentType = WindowsRuntime] | Out-Null

$radios = Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]])

$radios | Select-Object Name, Kind, State | Format-Table -AutoSize

if ($Name) {
    $target = $radios | Where-Object { $_.Name -eq $Name -and $_.Kind -eq 'WiFi' } | Select-Object -First 1
    if (-not $target) { throw "no Wi-Fi radio named '$Name'" }

    $desired = [Windows.Devices.Radios.RadioState]::$State
    Write-Host "setting radio '$Name' ($($target.State)) -> $State"
    $result = Await ($target.SetStateAsync($desired)) ([Windows.Devices.Radios.RadioAccessStatus])
    Write-Host "result: $result"
}
