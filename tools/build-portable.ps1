# Builds the portable single-file NetworkGuardian.
#
# Usage:
#   pwsh -NoProfile -File tools/build-portable.ps1                 # Release build + tests + AOT publish
#   pwsh -NoProfile -File tools/build-portable.ps1 -Zip            # ... plus a transfer archive
#   pwsh -NoProfile -File tools/build-portable.ps1 -SkipTests      # quick iteration
#
# Produces (default, release\ is the project's release folder and is git-ignored):
#   release\NetworkGuardian.exe    native AOT single file, no runtime required
#   release\发布说明.txt
#
# The privileged helper is inside that executable (NetworkGuardian.exe --helper), so the package is
# one binary: nothing has to sit next to it except the read-me.
#
# The size gate fails the build when the exe exceeds -MaxExeMb (default 8 MB).

param(
    [string]$Configuration = 'Release',
    [string]$OutputDir = 'release',
    [double]$MaxExeMb = 8.0,
    [switch]$SkipTests,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'NetworkGuardian.sln'
$appProject = Join-Path $repoRoot 'src\NetworkGuardian.Portable\NetworkGuardian.Portable.csproj'
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repoRoot $OutputDir }
$logDir = Join-Path $repoRoot 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Invoke-Step {
    param([string]$Name, [string]$Command)

    Write-Host "== $Name"
    $log = Join-Path $logDir (($Name -replace '[^A-Za-z0-9]+', '-').ToLowerInvariant() + '.log')
    $output = & pwsh -NoProfile -Command $Command 2>&1
    $exit = $LASTEXITCODE

    if ($exit -ne 0) {
        $output | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
        throw "$Name failed with exit code $exit"
    }

    $output | ForEach-Object { $_ } | Out-File -FilePath $log -Encoding utf8
    return $output
}

# ---------- build + tests ----------

$buildOutput = Invoke-Step 'build' "dotnet build '$solution' -c $Configuration --nologo -v:m"

# The MSBuild summary line is "N Warning(s)"; matching on the word "warning" alone would also hit
# that summary line itself.
$warningCount = 0
foreach ($line in $buildOutput) {
    if ($line -match '^\s*(\d+)\s+Warning\(s\)') { $warningCount = [int]$Matches[1] }
}

$buildTail = ($buildOutput | Select-Object -Last 4) -join '; '
Write-Host "   $buildTail"
if ($warningCount -gt 0) {
    $buildOutput | Select-String -Pattern ': warning ' | Select-Object -First 10 | ForEach-Object { Write-Host "   $($_.Line)" }
    throw "the build reported $warningCount warning(s); a portable release must be warning free"
}

if (-not $SkipTests) {
    $testOutput = Invoke-Step 'test' "dotnet test '$(Join-Path $repoRoot 'tests\NetworkGuardian.Tests\NetworkGuardian.Tests.csproj')' -c $Configuration --nologo"
    $summary = $testOutput | Select-String -Pattern 'Passed!|Failed!' | Select-Object -First 1
    if (-not $summary -or $summary.Line -notmatch 'Passed!') {
        throw 'the unit tests did not pass'
    }

    Write-Host "   $($summary.Line.Trim())"
}

# ---------- publish ----------

# Clear the contents instead of the directory: a shell sitting inside it would lock the delete.
if (Test-Path $outputPath) {
    Get-ChildItem -Path $outputPath -Force | Remove-Item -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
Invoke-Step 'publish-app' "dotnet publish '$appProject' -c $Configuration -r win-x64 -o '$outputPath' --nologo" | Out-Null

# ---------- package contents ----------

$exePath = Join-Path $outputPath 'NetworkGuardian.exe'
if (-not (Test-Path $exePath)) { throw 'NetworkGuardian.exe was not produced' }

# A single-file package means exactly one executable; a second one would also be a second copy of the
# privileged code path that nobody tests.
$extraExe = Get-ChildItem -Path $outputPath -Recurse -Filter '*.exe' | Where-Object { $_.FullName -ne $exePath }
if ($extraExe) { throw "the package contains a second executable: $($extraExe[0].FullName)" }

Copy-Item -Path (Join-Path $PSScriptRoot 'portable-notes.txt') -Destination (Join-Path $outputPath '发布说明.txt') -Force

$strayPdb = Get-ChildItem -Path $outputPath -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue
if ($strayPdb) { throw "the package contains debug symbols: $($strayPdb[0].FullName)" }

$exeMb = [Math]::Round((Get-Item $exePath).Length / 1MB, 2)
$totalMb = [Math]::Round(((Get-ChildItem -Path $outputPath -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 2)
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version

Write-Host ''
Write-Host '== package'
Write-Host "   NetworkGuardian.exe           $exeMb MB   (native AOT, helper included, no runtime needed)"
Write-Host "   total                         $totalMb MB"

if ($exeMb -gt $MaxExeMb) {
    throw "NetworkGuardian.exe is $exeMb MB which exceeds the $MaxExeMb MB gate"
}

# ---------- optional transfer archive ----------

if ($Zip) {
    $stageName = "NetworkGuardian-$version-win-x64"
    $stageRoot = Join-Path $env:TEMP ('ng-zip-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $stageDir = Join-Path $stageRoot $stageName

    try {
        New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
        Copy-Item -Path (Join-Path $outputPath '*') -Destination $stageDir -Recurse -Force

        $zipPath = Join-Path $repoRoot "$stageName.zip"
        Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
        Add-Type -AssemblyName System.IO.Compression.FileSystem

        # includeBaseDirectory=true prefixes every entry with the *source directory name*, so the
        # versioned folder itself is the source (passing its parent would name the archive after the
        # temp directory).
        [System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

        $zipMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 2)
        Write-Host "   $stageName.zip$(' ' * [Math]::Max(1, 24 - $stageName.Length))$zipMb MB"

        # Prove the archive round trips: extract and compare hashes.
        $extractRoot = Join-Path $env:TEMP ('ng-unzip-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractRoot)

        $extractedExe = Join-Path $extractRoot "$stageName\NetworkGuardian.exe"
        $sourceHash = (Get-FileHash $exePath -Algorithm SHA256).Hash
        $extractedHash = (Get-FileHash $extractedExe -Algorithm SHA256).Hash
        if ($sourceHash -ne $extractedHash) { throw 'the extracted exe does not match the published one' }

        Write-Host "   archive verified (extracted exe hash matches)"
        Remove-Item -Recurse -Force $extractRoot -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item -Recurse -Force $stageRoot -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host "done: $outputPath"
