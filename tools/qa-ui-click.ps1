param(
    [string]$Exe = 'D:\Files\Develop\Windows\NetworkGuardian\release\NetworkGuardian.exe',
    [string]$Page = 'settings',
    [int]$ScrollTo = 7,                            # 7 = SB_BOTTOM, 6 = SB_TOP
    [string]$Clicks = '674,141',                   # semicolon separated client coordinates
    [int]$WaitMs = 700
)

# Self-drawn UI regression probe: clicks client coordinates and captures the window through
# PrintWindow (WM_PRINTCLIENT), which works while the window is occluded.
#
# Coordinates are calibrated first: this script's thread has a different DPI context than the target
# window, so Windows scales the coordinates of the messages it delivers. A throw-away click at (4,4)
# measures that factor so the requested coordinates arrive unchanged.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NGQa {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
}
'@

$root = Join-Path $env:TEMP ('ng-qa-click-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $root, (Join-Path $root 'Logs') | Out-Null
# debug level so the UI hit-test trace lands in the log file.
'{"version":3,"general":{"automaticRecovery":false,"healthSweepSeconds":10},"startup":{"startMinimized":false,"closeToTray":false},"logging":{"minimumLevel":"debug","writeToFile":true}}' |
    Set-Content -Path (Join-Path $root 'config.json') -Encoding utf8
$env:NETWORKGUARDIAN_CONFIG_ROOT = $root
$env:NETWORKGUARDIAN_INSTANCE_SUFFIX = '.qa'

function Shot([IntPtr]$h, [string]$path) {
    $r = New-Object NGQa+RECT
    [void][NGQa]::GetClientRect($h, [ref]$r)
    $dc = [NGQa]::GetDC($h)
    $mem = [NGQa]::CreateCompatibleDC($dc)
    $bmp = [NGQa]::CreateCompatibleBitmap($dc, [Math]::Max(1,$r.Right), [Math]::Max(1,$r.Bottom))
    [void][NGQa]::SelectObject($mem, $bmp)
    [void][NGQa]::ReleaseDC($h, $dc)
    [void][NGQa]::PrintWindow($h, $mem, 0x1)
    $img = [System.Drawing.Image]::FromHbitmap($bmp)
    $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $img.Dispose()
    [void][NGQa]::DeleteObject($bmp); [void][NGQa]::DeleteDC($mem)
}

function RawClick([IntPtr]$h, [int]$x, [int]$y) {
    $lp = [IntPtr]((($y -band 0xFFFF) -shl 16) -bor ($x -band 0xFFFF))
    [void][NGQa]::SendMessageW($h, 0x0201, [IntPtr]1, $lp)
    Start-Sleep -Milliseconds 80
    [void][NGQa]::SendMessageW($h, 0x0202, [IntPtr]0, $lp)
}

function LogLines([string]$root, [int]$count = 60) {
    $log = Get-ChildItem (Join-Path $root 'Logs') -Filter '*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { return @() }
    return @(Get-Content $log.FullName -Tail $count)
}

$proc = Start-Process -FilePath $Exe -ArgumentList '--visible','--page',$Page -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { $h = $proc.MainWindowHandle; break }
    if ($proc.HasExited) { throw "process exited with $($proc.ExitCode)" }
}
if ($h -eq [IntPtr]::Zero) { throw 'main window not found' }
Start-Sleep -Milliseconds 900

# --- calibrate the delivered coordinate scale -------------------------------------------------
# Align this thread with the target window first: a DPI-unaware caller has its message coordinates
# rescaled by Windows, and the factor differs between runs (observed 1.25 and 1.5) depending on how the
# host process was started. Asking for PER_MONITOR_AWARE_V2 makes the delivered coordinates equal the
# requested ones, so the calibration below should come out at 1.0 - it is kept as a check, not a guess.
[void][NGQa]::SetThreadDpiAwarenessContext([IntPtr](-4))
RawClick $h 4 4
Start-Sleep -Milliseconds 400
$probe = LogLines $root 80 | Select-String -Pattern 'mouse down at (\d+),(\d+)' | Select-Object -Last 1
$k = 1.0
if ($probe) {
    $delivered = [int]$probe.Matches[0].Groups[1].Value
    if ($delivered -gt 0) { $k = $delivered / 4.0 }
}
Write-Host ("coordinate scale: requested 4 -> delivered {0} (factor {1})" -f ($k * 4), $k)

function Click([IntPtr]$h, [int]$x, [int]$y) {
    RawClick $h ([int][Math]::Round($x / $k)) ([int][Math]::Round($y / $k))
    Start-Sleep -Milliseconds $WaitMs
}

Shot $h (Join-Path $root '0-before.png')

if ($ScrollTo -ge 0) {
    [void][NGQa]::SendMessageW($h, 0x0115, [IntPtr]$ScrollTo, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
    Shot $h (Join-Path $root '1-scrolled.png')
}

$index = 0
foreach ($pair in $Clicks.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
    $parts = $pair.Split(',')
    $cx = [int]$parts[0].Trim(); $cy = [int]$parts[1].Trim()
    Click $h $cx $cy
    $index++
    Shot $h (Join-Path $root ("2-click$index.png"))
    Write-Host "clicked client ($cx,$cy)"
}

Write-Host '--- UI hit trace ---'
LogLines $root 80 | Select-String -Pattern 'mouse (down|up)' | Select-Object -Last 12 | ForEach-Object { Write-Host "  $($_.Line)" }

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Host "artifacts: $root"
