param([string]$OutputPath = 'artifacts/NetworkGuardian-Skill.zip')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$target = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
$skill = Join-Path $repoRoot 'skills/networkguardian'
if (-not (Test-Path -LiteralPath (Join-Path $skill 'SKILL.md'))) { throw 'Skill entrypoint missing' }
Add-Type -AssemblyName System.IO.Compression
$temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $skill -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]|\.pyc$' } | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath((Split-Path -Parent $skill), $_.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $relative) | Out-Null
    }
} finally { $archive.Dispose() }
Move-Item -LiteralPath $temporary -Destination $target -Force
Write-Output $target
