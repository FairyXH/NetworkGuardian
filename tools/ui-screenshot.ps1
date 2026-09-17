# Captures one page of the portable NetworkGuardian window.
#
# Usage:
#   pwsh -NoProfile -File tools/ui-screenshot.ps1 -Page dashboard -Out docs/ui-dashboard.png
#
# The window class is located with EnumWindows instead of Process.MainWindowHandle, because the app
# can start minimised and can hide itself to the tray. The capture itself uses PrintWindow against the
# window's own WM_PRINTCLIENT handler, so it works while the window is behind other windows and never
# has to steal the foreground. The script launches its own instance with an isolated configuration
# root (and its own single instance suffix), so a running user instance is never touched.

param(
    [string]$Page = 'dashboard',
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$ExePath = '',
    [int]$WaitSeconds = 12,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class NgCapture
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder buffer, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    public const uint PW_CLIENTONLY = 0x00000001;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr FindByClass(uint pid, string className)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            uint windowPid;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid != pid) { return true; }

            var buffer = new StringBuilder(256);
            GetClassNameW(hWnd, buffer, buffer.Capacity);
            if (buffer.ToString() != className) { return true; }

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }
}
'@

# A DPI unaware caller receives virtualised rectangles, which would clip or shrink the capture.
[void][NgCapture]::SetProcessDpiAwarenessContext([IntPtr](-4))

$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $candidates = @(
        (Join-Path $repoRoot 'portable\NetworkGuardian.exe'),
        (Join-Path $repoRoot 'src\NetworkGuardian.Portable\bin\Release\net8.0-windows\win-x64\NetworkGuardian.exe'),
        (Join-Path $repoRoot 'src\NetworkGuardian.Portable\bin\Debug\net8.0-windows\win-x64\NetworkGuardian.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { $ExePath = $candidate; break }
    }
}

if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw 'NetworkGuardian.exe was not found - build or publish the portable project first.'
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('ng-shot-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path (Join-Path $root 'Logs') | Out-Null
'{"version":3,"general":{"automaticRecovery":false,"healthSweepSeconds":10},"startup":{"startMinimized":false,"closeToTray":false},"logging":{"minimumLevel":"information","writeToFile":true}}' |
    Set-Content -Path (Join-Path $root 'config.json') -Encoding utf8

$env:NETWORKGUARDIAN_CONFIG_ROOT = $root
$env:NETWORKGUARDIAN_INSTANCE_SUFFIX = '.shot' + [Guid]::NewGuid().ToString('N').Substring(0, 5)

Write-Host "exe    : $ExePath"
Write-Host "page   : $Page"
Write-Host "config : $root"

$process = Start-Process -FilePath $ExePath -ArgumentList '--visible', '--page', $Page -PassThru

try {
    $handle = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt ($WaitSeconds * 2); $attempt++) {
        Start-Sleep -Milliseconds 500
        $handle = [NgCapture]::FindByClass([uint32]$process.Id, 'NetworkGuardianPortableWindow')
        if ($handle -ne [IntPtr]::Zero) { break }
    }

    if ($handle -eq [IntPtr]::Zero) {
        throw "The NetworkGuardian window was not found after $WaitSeconds seconds."
    }

    # Let the monitor loop publish at least one snapshot so the page shows real data.
    Start-Sleep -Seconds 4

    $client = New-Object NgCapture+RECT
    [void][NgCapture]::GetClientRect($handle, [ref]$client)
    $width = $client.Right - $client.Left
    $height = $client.Bottom - $client.Top
    if ($width -le 100 -or $height -le 100) { throw "The client rectangle is unusable ($width x $height)." }

    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $printed = $false
    try {
        $printed = [NgCapture]::PrintWindow($handle, $hdc, ([NgCapture]::PW_CLIENTONLY -bor [NgCapture]::PW_RENDERFULLCONTENT))
    }
    finally {
        $graphics.ReleaseHdc($hdc)
    }

    $outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $repoRoot $Out }
    $outDir = Split-Path -Parent $outPath
    if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

    $bitmap.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)

    # A uniform capture means the window never painted; report it instead of claiming the UI was seen.
    $distinct = New-Object 'System.Collections.Generic.HashSet[int]'
    for ($y = 0; $y -lt $height; $y += 7) {
        for ($x = 0; $x -lt $width; $x += 7) {
            [void]$distinct.Add($bitmap.GetPixel($x, $y).ToArgb())
        }
    }

    $bitmap.Dispose()

    Write-Host "saved  : $outPath ($width x $height, printWindow=$printed, $($distinct.Count) distinct sampled colours)"
    if ($distinct.Count -lt 5) {
        Write-Warning 'The capture looks uniform - the window may not have painted.'
    }
}
finally {
    if (-not $KeepRunning -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
        Write-Host 'instance: stopped'
    }
}
