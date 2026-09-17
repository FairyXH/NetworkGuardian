using System.Text;
using System.Runtime.InteropServices;
using static NetworkGuardian.Portable.Interop.NativeMethods;

namespace NetworkGuardian.Portable.Ui;

/// <summary>Win32 common file dialog (works from a native AOT process, unlike the WinRT pickers).</summary>
internal static class FileDialog
{
    public static string? PickExecutable(IntPtr owner, string title)
    {
        const string filter =
            "可执行文件 (*.exe;*.bat;*.cmd;*.ps1)\0*.exe;*.bat;*.cmd;*.ps1\0所有文件 (*.*)\0*.*\0\0";

        var buffer = new StringBuilder(1024);
        var dialog = new OPENFILENAMEW
        {
            lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
            hwndOwner = owner,
            lpstrFilter = filter,
            nFilterIndex = 1,
            lpstrFile = buffer,
            nMaxFile = buffer.Capacity,
            lpstrTitle = title,
            Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER | OFN_HIDEREADONLY,
        };

        return GetOpenFileNameW(ref dialog) ? buffer.ToString() : null;
    }
}
