param()
$ErrorActionPreference = 'Stop'
$guideRepo = Split-Path -Parent $PSScriptRoot
$guideSource = Join-Path $guideRepo 'docs\user-guide\NetworkGuardian-User-Guide.tex'
$guideBuild = Join-Path $guideRepo 'artifacts\manual-0.14.0'
$guideTarget = Join-Path $guideRepo 'docs\user-guide\NetworkGuardian-User-Guide.pdf'
New-Item -ItemType Directory -Path $guideBuild -Force | Out-Null
$guideConsole = Join-Path $guideBuild 'compile-console.log'
$guideLog = Join-Path $guideBuild 'NetworkGuardian-User-Guide.log'
Get-Command xelatex -ErrorAction Stop | Out-Null
for ($guideRound = 0; $guideRound -lt 3; $guideRound++) {
    & xelatex '-interaction=nonstopmode' '-halt-on-error' '-no-shell-escape' "-output-directory=$guideBuild" $guideSource > $guideConsole 2>&1
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $guideConsole -Tail 40
        throw 'XeLaTeX compilation failed; published PDF was not replaced.'
    }
}
$guideProblems = Select-String -LiteralPath $guideLog -Pattern 'Overfull|Underfull|Missing character|LaTeX Warning|Package .* Warning'
if ($guideProblems) {
    $guideProblems | ForEach-Object { Write-Output $_.Line }
    throw 'Fix LaTeX warnings before publishing the guide.'
}
Copy-Item -LiteralPath (Join-Path $guideBuild 'NetworkGuardian-User-Guide.pdf') -Destination $guideTarget -Force
Get-Item -LiteralPath $guideTarget | Select-Object FullName, Length
Get-FileHash -LiteralPath $guideTarget -Algorithm SHA256
