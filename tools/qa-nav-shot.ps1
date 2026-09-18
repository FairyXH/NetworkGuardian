param(
    [string]$Exe = 'D:\Files\Develop\Windows\NetworkGuardian\release\NetworkGuardian.exe',
    [int]$HoldMs = 500
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NG2 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
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

$exe = $Exe
$root = Join-Path $env:TEMP ('ng-qa-nav-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $root | Out-Null
$env:NETWORKGUARDIAN_CONFIG_ROOT = $root
$env:NETWORKGUARDIAN_INSTANCE_SUFFIX = '.qa'

function Shot([IntPtr]$h, [string]$path) {
    $r = New-Object NG2+RECT
    [void][NG2]::GetClientRect($h, [ref]$r)
    $dc = [NG2]::GetDC($h)
    $mem = [NG2]::CreateCompatibleDC($dc)
    $bmp = [NG2]::CreateCompatibleBitmap($dc, [Math]::Max(1,$r.Right), [Math]::Max(1,$r.Bottom))
    [void][NG2]::SelectObject($mem, $bmp)
    [void][NG2]::ReleaseDC($h, $dc)
    [void][NG2]::PrintWindow($h, $mem, 0x1)
    $img = [System.Drawing.Image]::FromHbitmap($bmp)
    $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $img.Dispose()
    [void][NG2]::DeleteObject($bmp); [void][NG2]::DeleteDC($mem)
}

function Click([IntPtr]$h, [int]$x, [int]$y) {
    $lp = [IntPtr]((($y -band 0xFFFF) -shl 16) -bor ($x -band 0xFFFF))
    [void][NG2]::SendMessageW($h, 0x0201, [IntPtr]1, $lp)
    Start-Sleep -Milliseconds 80
    [void][NG2]::SendMessageW($h, 0x0202, [IntPtr]0, $lp)
    Start-Sleep -Milliseconds 500
}

$proc = Start-Process -FilePath $exe -ArgumentList '--visible','--page','dashboard' -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { $h = $proc.MainWindowHandle; break }
    if ($proc.HasExited) { throw "process exited with $($proc.ExitCode)" }
}
if ($h -eq [IntPtr]::Zero) { throw 'main window not found' }

$dpi = [NG2]::GetDpiForWindow($h); $scale = $dpi / 96.0
function S([int]$v) { return [int][Math]::Round($v * $scale) }
$y0 = (S 64) + (S 14); $item = S 38; $gap = S 2
$x = S 100

Shot $h (Join-Path $root '0-dashboard.png')
for ($i = 1; $i -lt 5; $i++) {
    $y = $y0 + $i * ($item + $gap) + [int]($item / 2)
    Click $h $x $y
    Shot $h (Join-Path $root ("$i-nav.png"))
    Write-Host "clicked nav[$i] at ($x,$y)"
}

Write-Host "root=$root"
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
