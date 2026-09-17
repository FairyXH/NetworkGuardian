# Launches the app, waits for its main window and captures only that window.
#
# Only the NetworkGuardian window rectangle is captured - never the rest of the desktop.
#
# Usage: pwsh -NoProfile -File tools/ui-screenshot.ps1 [-Seconds 20] [-Out path.png]

param(
    [int]$Seconds = 20,
    [string]$Out = '',
    [string]$Page = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# Without this the hosting shell is DPI-virtualised and GetWindowRect returns scaled coordinates,
# which makes the captured region smaller than the real window.
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WinDpi
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
'@
[void][WinDpi]::SetProcessDpiAwarenessContext([IntPtr](-4))

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WinRect
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);
}
'@

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem -Path (Join-Path $repoRoot 'src\NetworkGuardian.App\bin') -Filter 'NetworkGuardian.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $exe) { throw 'NetworkGuardian.exe was not found - build the solution first.' }

if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $repoRoot 'docs\ui-dashboard.png'
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('ng-ui-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path (Join-Path $root 'Logs') | Out-Null
'{"version":3,"general":{"automaticRecovery":false},"logging":{"minimumLevel":"information","writeToFile":true}}' |
    Set-Content -Path (Join-Path $root 'config.json') -Encoding utf8

$env:NETWORKGUARDIAN_CONFIG_ROOT = $root
$exeArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Page)) {
    $exeArgs = @('--page', $Page)
    Write-Host "page        : $Page"
}

$process = Start-Process -FilePath $exe -ArgumentList $exeArgs -PassThru

try {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $handle = [IntPtr]::Zero

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { throw "process exited with code $($process.ExitCode)" }

        $handle = $process.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            $title = $process.MainWindowTitle
            if ($title -like '*could not be started*') { throw "the app failed to start: $title" }
            if ([WinRect]::IsWindowVisible($handle)) { break }
        }
    }

    if ($handle -eq [IntPtr]::Zero) { throw 'no main window appeared' }

    # Give the first snapshot and layout pass time to complete.
    Start-Sleep -Seconds 6

    $rect = New-Object WinRect+RECT
    if (-not [WinRect]::GetWindowRect($handle, [ref]$rect)) { throw 'GetWindowRect failed' }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    Write-Host "window      : ${width}x${height} at $($rect.Left),$($rect.Top)"
    Write-Host "title       : $($process.MainWindowTitle)"

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    # WinUI content is composed by DirectComposition, so PrintWindow returns a black frame. The window
    # is raised with HWND_TOPMOST instead (no focus steal) and its own rectangle is copied from the
    # screen; nothing outside the window is captured.
    [WinRect]::SetWindowPos($handle, [WinRect]::HWND_TOPMOST, 0, 0, 0, 0,
        ([WinRect]::SWP_NOMOVE -bor [WinRect]::SWP_NOSIZE -bor [WinRect]::SWP_SHOWWINDOW)) | Out-Null
    Start-Sleep -Milliseconds 1200

    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    }
    finally {
        [WinRect]::SetWindowPos($handle, [WinRect]::HWND_NOTOPMOST, 0, 0, 0, 0,
            ([WinRect]::SWP_NOMOVE -bor [WinRect]::SWP_NOSIZE)) | Out-Null
    }

    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)

    # A uniform image means the body was not rendered (or the window was still occluded), which has to
    # be reported instead of being passed off as a verified UI.
    $samples = @{}
    for ($y = 10; $y -lt $height - 10; $y += 40) {
        for ($x = 10; $x -lt $width - 10; $x += 40) {
            $samples[$bitmap.GetPixel($x, $y).ToArgb()] = $true
        }
    }

    $graphics.Dispose()
    $bitmap.Dispose()

    if ($samples.Count -lt 4) {
        Write-Host "WARNING: the captured window looks uniform ($($samples.Count) distinct colour(s))"
    }
    else {
        Write-Host "content     : $($samples.Count) distinct sampled colours"
    }

    Write-Host "screenshot  : $Out"
}
finally {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    Write-Host "config root : $root"
}
