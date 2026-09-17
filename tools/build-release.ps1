# Builds the deployable NetworkGuardian package into <repo>\release.
#
# The package is fully self-contained: the .NET runtime and the Windows App SDK runtime travel with
# the app, so the target machine needs no .NET SDK, no .NET runtime, no Windows App Runtime package
# and no Visual Studio. The privileged helper is published self-contained as well.
#
# Usage: pwsh -NoProfile -File tools/build-release.ps1

param(
    [string]$OutputDirectory = '',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'release'
}

$appProject = Join-Path $repoRoot 'src\NetworkGuardian.App\NetworkGuardian.App.csproj'
$helperProject = Join-Path $repoRoot 'src\NetworkGuardian.Helper\NetworkGuardian.Helper.csproj'
$solution = Join-Path $repoRoot 'NetworkGuardian.sln'

# Publish output that never lands in the deliverable (the helper is copied into release\helper).
$stagingRoot = Join-Path $env:TEMP ('ng-release-staging-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$helperPublish = Join-Path $stagingRoot 'helper\'

function Invoke-Dotnet {
    param([string[]]$Arguments, [string]$Step)

    Write-Host "== $Step" -ForegroundColor Cyan
    $log = Join-Path $stagingRoot (($Step -replace '[^A-Za-z0-9]', '-') + '.log')
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath $log | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE (log: $log)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

New-Item -ItemType Directory -Force -Path $stagingRoot, $helperPublish | Out-Null

try {
    if (Test-Path $OutputDirectory) {
        # Clear the contents instead of removing the directory: a shell or Explorer window sitting in
        # the folder would otherwise block the deletion with "being used by another process".
        Get-ChildItem -Path $OutputDirectory -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 200
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

    # Debug symbols and PDBs are development artifacts; the release package ships without them.
    $releaseOptions = @(
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        '-p:PublishTrimmed=false',
        '--nologo',
        '-v:minimal'
    )

    # The helper is a tiny console app that only performs PnP operations, so it is published as a
    # single compressed file instead of shipping a second copy of the whole .NET runtime next to it.
    $helperOptions = $releaseOptions + @(
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true'
    )

    # A solution build rejects -r / --self-contained (NETSDK1134): those belong to the publish steps.
    Invoke-Dotnet -Step 'build solution' -Arguments @(
        'build', $solution, '-c', 'Release', '--nologo', '-v:minimal')

    if (-not $SkipTests) {
        Invoke-Dotnet -Step 'run tests' -Arguments @(
            'test', (Join-Path $repoRoot 'tests\NetworkGuardian.Tests\NetworkGuardian.Tests.csproj'),
            '-c', 'Release', '--nologo', '-v:minimal')
    }

    Invoke-Dotnet -Step 'publish privileged helper' -Arguments (@(
        'publish', $helperProject, '-o', $helperPublish) + $helperOptions)

    Invoke-Dotnet -Step 'publish app' -Arguments (@(
        'publish', $appProject, '-o', $OutputDirectory, "-p:HelperPublishDir=$helperPublish") + $releaseOptions)

    # ---- verification of the package itself -------------------------------------------------
    $required = @(
        'NetworkGuardian.exe',
        'NetworkGuardian.dll',
        'NetworkGuardian.deps.json',
        'NetworkGuardian.runtimeconfig.json',
        # .NET runtime carried locally
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'clrjit.dll',
        # Windows App SDK runtime carried locally
        'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.UI.Xaml.dll',
        'Microsoft.WindowsAppRuntime.Bootstrap.dll',
        # privileged helper, also self-contained
        'helper\NetworkGuardian.Helper.exe'
    )

    $missing = @()
    foreach ($relative in $required) {
        if (-not (Test-Path (Join-Path $OutputDirectory $relative))) { $missing += $relative }
    }

    if (Test-Path (Join-Path $OutputDirectory 'Microsoft.ui.xaml.dll')) { $missing = $missing | Where-Object { $_ -ne 'Microsoft.UI.Xaml.dll' } }

    if ($missing.Count -gt 0) {
        Write-Host "release package is incomplete, missing:" -ForegroundColor Red
        $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }

    # A self-contained deployment must not depend on a machine-wide shared framework.
    $runtimeConfig = Get-Content (Join-Path $OutputDirectory 'NetworkGuardian.runtimeconfig.json') -Raw
    if ($runtimeConfig -notmatch 'includedFrameworks') {
        Write-Host "NetworkGuardian.runtimeconfig.json does not declare an included framework - the app would need a machine-wide .NET runtime." -ForegroundColor Red
        exit 1
    }

    # Either the helper carries the runtime as loose files (coreclr.dll next to it) or it is bundled
    # into the single-file executable; in both cases it must not need a machine-wide runtime.
    $helperExe = Get-Item (Join-Path $OutputDirectory 'helper\NetworkGuardian.Helper.exe')
    $helperCoreClr = Join-Path $OutputDirectory 'helper\coreclr.dll'
    if (Test-Path $helperCoreClr) {
        $helperRuntimeConfig = Get-Content (Join-Path $OutputDirectory 'helper\NetworkGuardian.Helper.runtimeconfig.json') -Raw
        if ($helperRuntimeConfig -notmatch 'includedFrameworks') {
            Write-Host "helper is not self-contained - it would need a machine-wide .NET runtime." -ForegroundColor Red
            exit 1
        }
    }
    elseif ($helperExe.Length -lt 20MB) {
        Write-Host "helper is neither self-contained nor bundled ($([math]::Round($helperExe.Length / 1MB, 1)) MB) - it would need a machine-wide .NET runtime." -ForegroundColor Red
        exit 1
    }

    $leftoverPdbs = Get-ChildItem -Path $OutputDirectory -Filter '*.pdb' -Recurse
    if ($leftoverPdbs.Count -gt 0) {
        Write-Host "removing $($leftoverPdbs.Count) leftover PDB file(s)" -ForegroundColor Yellow
        $leftoverPdbs | Remove-Item -Force
    }

    # ---- release notes ----------------------------------------------------------------------
    $version = (Get-Item (Join-Path $OutputDirectory 'NetworkGuardian.exe')).VersionInfo.FileVersion
    $notes = @"
NetworkGuardian $version - 发布包（自包含）

运行要求
  - Windows 10 2004 (10.0.19041) 或 Windows 11，x64
  - 无需安装 .NET 运行时、Windows App Runtime、Visual Studio 或任何开发工具
    （.NET 与 Windows App SDK 运行时随本目录一起发布）

运行方式
  - 双击 NetworkGuardian.exe
  - 静默启动到托盘：NetworkGuardian.exe --minimized
  - 直接打开指定页面：NetworkGuardian.exe --page wireless
    （dashboard | wireless | ethernet | settings | logs）

首次运行
  - 会在 %LOCALAPPDATA%\NetworkGuardian\ 生成 config.json 与 Logs\ 日志目录
  - 默认不开启自动恢复：先在「设置」页确认阈值并打开「启用自动网络恢复」
  - 校园网认证 / 断网自定义命令行在「设置」页填写（可执行文件路径或整条命令行）

权限说明
  - 主程序不需要管理员权限
  - 只有两件事需要提权，会弹一次 UAC，由同目录 helper\NetworkGuardian.Helper.exe 完成：
      1) 启用被禁用的物理无线网卡
      2) 配置中显式要求提权的认证程序 / 离线命令

位置权限
  - Windows 11 将 SSID / BSSID / RSSI 归入位置隐私权限。若「总览」页提示位置权限受限：
      设置 > 隐私和安全性 > 位置 > 打开「定位服务」和「让桌面应用访问你的位置」> 重启本程序
  - 未开启时程序仍可扫描并连接已保存的配置，但读不到 BSSID / RSSI

目录内容
  NetworkGuardian.exe           主程序（自包含，含 .NET 与 Windows App SDK 运行时）
  helper\                       提权助手（同样自包含）
  发布说明.txt                   本文件
  其余 dll / pri / 语言资源       运行时依赖，请勿删除或单独移动

卸载
  - 删除本目录，并删除 %LOCALAPPDATA%\NetworkGuardian\（配置与日志），
    以及「设置」页里已勾选的开机启动项（若开启过）。
"@
    $notes | Set-Content -Path (Join-Path $OutputDirectory '发布说明.txt') -Encoding utf8

    # ---- summary ----------------------------------------------------------------------------
    $files = Get-ChildItem -Path $OutputDirectory -Recurse -File
    $totalMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    $helperMb = [math]::Round(((Get-ChildItem (Join-Path $OutputDirectory 'helper') -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
    $hash = (Get-FileHash (Join-Path $OutputDirectory 'NetworkGuardian.exe') -Algorithm SHA256).Hash

    Write-Host ''
    Write-Host 'release package ready' -ForegroundColor Green
    Write-Host "  path        : $OutputDirectory"
    Write-Host "  version     : $version"
    Write-Host "  files       : $($files.Count) (helper: $helperMb MB)"
    Write-Host "  total size  : $totalMb MB"
    Write-Host "  exe sha256  : $hash"
    Write-Host '  next        : pwsh -NoProfile -File tools/verify-release.ps1'
}
finally {
    Remove-Item -Recurse -Force $stagingRoot -ErrorAction SilentlyContinue
}
