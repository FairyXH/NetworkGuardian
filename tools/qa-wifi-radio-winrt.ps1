# Reads or flips a single Wi-Fi adapter's software radio through the Windows Radio Manager
# (Windows.Devices.Radios), which is what the Windows 11 Settings switch does.
#
# Why a separate script: the wlanapi route used by tools/qa-wifi-radio.ps1 is rejected by several USB
# drivers (WlanSetInterface(radio_state) -> ERROR_INVALID_PARAMETER 87), while the Radio Manager write
# works. It must run under Windows PowerShell 5.1: pwsh 7 cannot load the WinRT projection.
#
# Usage:
#   powershell -NoProfile -File tools/qa-wifi-radio-winrt.ps1 -Action query
#   powershell -NoProfile -File tools/qa-wifi-radio-winrt.ps1 -Action off -Index 1
#   powershell -NoProfile -File tools/qa-wifi-radio-winrt.ps1 -Action on  -Match 'MediaTek'

param(
    [ValidateSet('query', 'on', 'off')]
    [string]$Action = 'query',
    [int]$Index = -1,
    [string]$Match = ''
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -gt 5) {
    Write-Host 'This script must run under Windows PowerShell 5.1 (WinRT projection). Re-launching...'
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
              '-Action', $Action, '-Index', $Index)
    if ($Match) { $args += @('-Match', $Match) }
    & powershell.exe @args
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Await-Operation {
    param($Operation, [Type]$ResultType)
    $task = $asTaskGeneric.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait(-1) | Out-Null
    return $task.Result
}

[void][Windows.Devices.Radios.Radio, Windows.Devices.Radios, ContentType = WindowsRuntime]
[void][Windows.Devices.Radios.RadioState, Windows.Devices.Radios, ContentType = WindowsRuntime]
[void][Windows.Devices.Radios.RadioKind, Windows.Devices.Radios, ContentType = WindowsRuntime]
[void][Windows.Devices.Radios.RadioAccessStatus, Windows.Devices.Radios, ContentType = WindowsRuntime]

$readOnlyListType = [System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]]
$radios = Await-Operation ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) $readOnlyListType

$wifi = @($radios | Where-Object { $_.Kind -eq [Windows.Devices.Radios.RadioKind]::WiFi })
if ($wifi.Count -eq 0) { throw 'no Wi-Fi radio is present' }

for ($i = 0; $i -lt $wifi.Count; $i++) {
    Write-Host ("[{0}] {1,-10} state={2}" -f $i, $wifi[$i].Name, $wifi[$i].State)
}

if ($Action -eq 'query') { exit 0 }

$target = $null
if ($Match) {
    $target = $wifi | Where-Object { $_.Name -like "*$Match*" } | Select-Object -First 1
    if (-not $target) { throw "no Wi-Fi radio matches '$Match'" }
}
elseif ($Index -ge 0) {
    if ($Index -ge $wifi.Count) { throw "index $Index is out of range (0..$($wifi.Count - 1))" }
    $target = $wifi[$Index]
}
else {
    # Default: the first radio that is NOT off, so a spare adapter is switched rather than one in use.
    $target = $wifi | Where-Object { $_.State -ne [Windows.Devices.Radios.RadioState]::Off } | Select-Object -First 1
    if (-not $target) { $target = $wifi[0] }
}

$desired = if ($Action -eq 'on') { [Windows.Devices.Radios.RadioState]::On } else { [Windows.Devices.Radios.RadioState]::Off }
Write-Host "setting '$($target.Name)' -> $desired"

$status = Await-Operation ($target.SetStateAsync($desired)) ([Windows.Devices.Radios.RadioAccessStatus])
Write-Host "RadioAccessStatus: $status"

Start-Sleep -Milliseconds 500
Write-Host "state now: $($target.State)"
