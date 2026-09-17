param(
    [string]$OutputJson = "D:\Files\Develop\Windows\NetworkGuardian\src\NetworkGuardian.App\obj\Debug\net8.0-windows10.0.19041.0\win-x64\output.json"
)

# The WinUI XAML compiler masks x:Bind name-resolution failures behind a misleading
# "WMC9999 Could not find any resources ..." error, so the real culprit is recovered by reading
# the compiler's own output.json and locating the last page that entered code generation.
if (-not (Test-Path $OutputJson)) {
    Write-Host "output.json not found at $OutputJson"
    exit 1
}

$json = Get-Content $OutputJson -Raw | ConvertFrom-Json
$entries = $json.MSBuildLogEntries
$failureIndex = -1

for ($i = 0; $i -lt $entries.Count; $i++) {
    if ($entries[$i].Message -match "Could not find any resources appropriate") {
        $failureIndex = $i
        break
    }
}

if ($failureIndex -lt 0) {
    Write-Host "No masked resource failure found in the compiler output."
    exit 0
}

Write-Host "Masked failure at log entry index $failureIndex"
for ($i = [Math]::Max(0, $failureIndex - 8); $i -lt $failureIndex; $i++) {
    Write-Host ("  " + $entries[$i].Message)
}

Write-Host ""
Write-Host "Stack:"
Write-Host $entries[$failureIndex].Message
