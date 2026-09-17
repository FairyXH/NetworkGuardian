# Lists the top-level windows of a process: handle, visibility, title, class.
# Usage: pwsh -NoProfile -File tools/window-dump.ps1 -ProcessId 1234

param(
    [Parameter(Mandatory = $true)]
    [int]$ProcessId
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowDump
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    public static List<string> Find(uint pid)
    {
        var result = new List<string>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner == pid)
            {
                var title = new StringBuilder(512);
                GetWindowText(hWnd, title, title.Capacity);
                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, cls.Capacity);
                result.Add($"0x{hWnd.ToInt64():X} visible={IsWindowVisible(hWnd)} class={cls} title='{title}'");
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

$windows = [WindowDump]::Find([uint32]$ProcessId)
if ($windows.Count -eq 0) {
    Write-Host "no top-level windows for pid $ProcessId"
}
else {
    $windows | ForEach-Object { Write-Host $_ }
}
