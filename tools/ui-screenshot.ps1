# Launches the app, finds its window, shows it and captures only that window.
#
# Only the NetworkGuardian window rectangle is captured - never the rest of the desktop.
#
# The window is located with EnumWindows (class WinUIDesktopWin32WindowClass, title prefix
# "NetworkGuardian") instead of Process.MainWindowHandle, because minimize-to-tray can hide the
# window during startup. It is then restored explicitly, raised with HWND_TOPMOST and captured.
#
# Usage: pwsh -NoProfile -File tools/ui-screenshot.ps1 [-Page wireless] [-Out path.png] [-Seconds 25]

param(
    [int]$Seconds = 25,
    [string]$Out = '',
    [string]$Page = '',
    [string]$ExePath = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class GuardianWindow
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_SHOWNORMAL = 1;

    /// <summary>Finds the main WinUI window of a process, restoring it if it was hidden to the tray.</summary>
    public static IntPtr Find(uint pid, bool restore)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner != pid) { return true; }

            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);
            if (!cls.ToString().Equals("WinUIDesktopWin32WindowClass", StringComparison.Ordinal)) { return true; }

            var title = new StringBuilder(512);
            GetWindowText(hWnd, title, title.Capacity);
            if (!title.ToString().StartsWith("NetworkGuardian", StringComparison.Ordinal)) { return true; }

            found = hWnd;
            if (restore && !IsWindowVisible(hWnd))
            {
                ShowWindow(hWnd, SW_SHOWNORMAL);
            }
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static string TitleOf(IntPtr hWnd)
    {
        var title = new StringBuilder(512);
        GetWindowText(hWnd, title, title.Capacity);
        return title.ToString();
    }
}
'@

[void][GuardianWindow]::SetProcessDpiAwarenessContext([IntPtr](-4))

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = $ExePath
if ([string]::IsNullOrWhiteSpace($exe)) {
    $exe = Get-ChildItem -Path (Join-Path $repoRoot 'src\NetworkGuardian.App\bin') -Filter 'NetworkGuardian.exe' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $exe) { throw 'NetworkGuardian.exe was not found - build the solution first.' }

if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $repoRoot ('docs\ui-' + ($(if ($Page) { $Page } else { 'dashboard' })) + '.png')
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

        $handle = [GuardianWindow]::Find([uint32]$process.Id, $true)
        if ($handle -ne [IntPtr]::Zero) {
            $title = [GuardianWindow]::TitleOf($handle)
            if ($title -like '*could not be started*') { throw "the app failed to start: $title" }
            break
        }
    }

    if ($handle -eq [IntPtr]::Zero) { throw 'no NetworkGuardian window appeared' }

    # Let the first snapshot and layout pass complete.
    Start-Sleep -Seconds 6

    $rect = New-Object GuardianWindow+RECT
    if (-not [GuardianWindow]::GetWindowRect($handle, [ref]$rect)) { throw 'GetWindowRect failed' }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    Write-Host "window      : ${width}x${height} at $($rect.Left),$($rect.Top)"
    Write-Host "title       : $([GuardianWindow]::TitleOf($handle))"

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    # WinUI renders through DirectComposition, so PrintWindow returns black. Raise our own window
    # (without stealing focus) and copy only its rectangle from the screen.
    [void][GuardianWindow]::SetWindowPos($handle, [GuardianWindow]::HWND_TOPMOST, 0, 0, 0, 0,
        ([GuardianWindow]::SWP_NOMOVE -bor [GuardianWindow]::SWP_NOSIZE -bor [GuardianWindow]::SWP_SHOWWINDOW))
    Start-Sleep -Milliseconds 1200

    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    }
    finally {
        [void][GuardianWindow]::SetWindowPos($handle, [GuardianWindow]::HWND_NOTOPMOST, 0, 0, 0, 0,
            ([GuardianWindow]::SWP_NOMOVE -bor [GuardianWindow]::SWP_NOSIZE))
    }

    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)

    # A uniform image means the window was occluded or not rendered: report it instead of passing the
    # capture off as verified UI.
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
