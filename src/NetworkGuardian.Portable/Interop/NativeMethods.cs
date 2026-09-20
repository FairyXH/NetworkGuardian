using System.Runtime.InteropServices;
using System.Text;

namespace NetworkGuardian.Portable.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PAINTSTRUCT
{
    public IntPtr hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    public unsafe fixed byte rgbReserved[32];
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEXW
{
    public int cbSize;
    public uint style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public string? lpszMenuName;
    public string lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SCROLLINFO
{
    public uint cbSize;
    public uint fMask;
    public int nMin;
    public int nMax;
    public uint nPage;
    public int nPos;
    public int nTrackPos;
}

/// <summary>
/// Win32 surface used by the self-drawn UI: window management from <c>user32</c>, drawing from
/// <c>gdi32</c> and the common file dialog from <c>comdlg32</c>.
/// </summary>
/// <remarks>
/// Everything here is plain P/Invoke with blittable structures, which is what makes the UI work from
/// a Native AOT process. No GDI+/System.Drawing, no XAML and no WinRT is involved.
/// </remarks>
internal static class NativeMethods
{
    internal const string User32 = "user32.dll";
    internal const string Gdi32 = "gdi32.dll";
    internal const string ComDlg32 = "comdlg32.dll";
    internal const string Kernel32 = "kernel32.dll";

    // ---------- message boxes ----------
    internal const uint MB_YESNO = 0x00000004;
    internal const uint MB_ICONWARNING = 0x00000030;
    internal const uint MB_DEFBUTTON2 = 0x00000100;
    internal const int IDYES = 6;

    // ---------- window messages ----------
    internal const uint WM_NULL = 0x0000;
    internal const uint WM_CREATE = 0x0001;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_QUERYENDSESSION = 0x0011;
    internal const uint WM_PAINT = 0x000F;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint WM_DPICHANGED = 0x02E0;
    internal const uint WM_POWERBROADCAST = 0x0218;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_CTLCOLOREDIT = 0x0133;
    internal const uint WM_CTLCOLORSTATIC = 0x0138;
    internal const uint WM_VSCROLL = 0x0115;
    internal const uint WM_SIZE = 0x0005;
    internal const uint WM_GETMINMAXINFO = 0x0024;
    internal const uint WM_SETCURSOR = 0x0020;
    internal const uint WM_PRINTCLIENT = 0x0318;
    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_ACTIVATEAPP = 0x001C;

    internal const uint PBT_APMSUSPEND = 0x0004;
    internal const uint PBT_APMRESUMESUSPEND = 0x0007;
    internal const uint PBT_APMRESUMEAUTOMATIC = 0x0012;

    internal const int WM_APP = 0x8000;
    internal const int WM_APP_SHOW_WINDOW = WM_APP + 10;
    internal const int WM_APP_REFRESH = WM_APP + 11;
    internal const int WM_APP_QUIT = WM_APP + 12;

    internal const int CW_USEDEFAULT = unchecked((int)0x80000000);

    internal const uint WM_SETFONT = 0x0030;
    internal const uint EM_SETSEL = 0x00B1;

    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_CHECKED = 0x00000008;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint TPM_LEFTALIGN = 0x0000;
    internal const uint TPM_TOPALIGN = 0x0000;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_NONOTIFY = 0x0080;
    internal const uint TPM_RETURNCMD = 0x0100;

    // ---------- window styles ----------
    internal const uint WS_OVERLAPPED = 0x00000000;
    internal const uint WS_CAPTION = 0x00C00000;
    internal const uint WS_SYSMENU = 0x00080000;
    internal const uint WS_THICKFRAME = 0x00040000;
    internal const uint WS_MINIMIZEBOX = 0x00020000;
    internal const uint WS_MAXIMIZEBOX = 0x00010000;
    internal const uint WS_VSCROLL = 0x00200000;
    internal const uint WS_CLIPCHILDREN = 0x02000000;
    internal const uint WS_CLIPSIBLINGS = 0x04000000;
    internal const uint WS_CHILD = 0x40000000;
    internal const uint WS_VISIBLE = 0x10000000;
    internal const uint WS_TABSTOP = 0x00010000;
    internal const uint WS_BORDER = 0x00800000;

    internal const uint WS_OVERLAPPEDWINDOW =
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

    internal const uint ES_LEFT = 0x0000;
    internal const uint ES_MULTILINE = 0x0004;
    internal const uint ES_AUTOHSCROLL = 0x0080;
    internal const uint ES_AUTOVSCROLL = 0x0040;
    internal const uint ES_WANTRETURN = 0x1000;

    internal const int EN_KILLFOCUS = 0x0200;
    internal const int EN_CHANGE = 0x0300;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNORMAL = 1;
    internal const int SW_SHOWMINIMIZED = 2;
    internal const int SW_RESTORE = 9;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const int SB_VERT = 1;
    internal const uint SIF_RANGE = 0x0001;
    internal const uint SIF_PAGE = 0x0002;
    internal const uint SIF_POS = 0x0004;
    internal const uint SIF_DISABLENOSCROLL = 0x0008;

    internal const int SB_LINEUP = 0;
    internal const int SB_LINEDOWN = 1;
    internal const int SB_PAGEUP = 2;
    internal const int SB_PAGEDOWN = 3;
    internal const int SB_THUMBTRACK = 5;
    internal const int SB_THUMBPOSITION = 4;
    internal const int SB_TOP = 6;
    internal const int SB_BOTTOM = 7;

    internal const int SPI_GETWHEELSCROLLLINES = 0x0068;
    internal const int WHEEL_DELTA = 120;

    internal const int IDC_ARROW = 32512;
    internal const int IDC_HAND = 32649;

    // ---------- GDI ----------
    internal const int SRCCOPY = 0x00CC0020;
    internal const int TRANSPARENT = 1;
    internal const int NULL_BRUSH = 5;
    internal const int NULL_PEN = 8;
    internal const int PS_SOLID = 0;
    internal const int DT_LEFT = 0x00000000;
    internal const int DT_CENTER = 0x00000001;
    internal const int DT_RIGHT = 0x00000002;
    internal const int DT_VCENTER = 0x00000004;
    internal const int DT_WORDBREAK = 0x00000010;
    internal const int DT_SINGLELINE = 0x00000020;
    internal const int DT_NOCLIP = 0x00000100;
    internal const int DT_END_ELLIPSIS = 0x00008000;
    internal const int DT_NOPREFIX = 0x00000800;
    internal const int DT_CALCRECT = 0x00000400;
    internal const int DT_EDITCONTROL = 0x00002000;
    internal const int FW_NORMAL = 400;
    internal const int FW_SEMIBOLD = 600;
    internal const int CLEARTYPE_QUALITY = 5;
    internal const int DEFAULT_CHARSET = 1;
    internal const int FF_DONTCARE = 0;
    internal const int VARIABLE_PITCH = 2;
    internal const int FIXED_PITCH = 1;

    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    internal delegate int EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport(User32)]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport(User32)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport(User32)]
    internal static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport(User32)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport(User32)]
    internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport(User32)]
    internal static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport(User32)]
    internal static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport(User32)]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport(User32)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool ReleaseCapture();

    [DllImport(User32)]
    internal static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport(User32)]
    internal static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport(User32)]
    internal static extern int SetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO lpsi, bool redraw);

    [DllImport(User32)]
    internal static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO lpsi);

    [DllImport(User32)]
    internal static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

    [DllImport(User32)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport(User32)]
    internal static extern bool AdjustWindowRectEx(ref RECT lpRect, uint dwStyle, bool bMenu, uint dwExStyle);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [DllImport(User32)]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport(User32)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport(User32)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport(User32)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport(User32)]
    internal static extern int TrackPopupMenuEx(
        IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport(User32)]
    internal static extern bool SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport(User32)]
    internal static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport(User32)]
    internal static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

    [DllImport(User32)]
    internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    /// <summary>Fills a rectangle with a brush (user32, not gdi32).</summary>
    [DllImport(User32)]
    internal static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport(User32)]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    internal static extern bool GetCursorInfo(ref CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    // ---------- GDI ----------
    [DllImport(Gdi32)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport(Gdi32)]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport(Gdi32)]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport(Gdi32)]
    internal static extern bool DeleteObject(IntPtr ho);

    [DllImport(Gdi32)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport(Gdi32)]
    internal static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [DllImport(Gdi32)]
    internal static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

    [DllImport(Gdi32)]
    internal static extern IntPtr CreateSolidBrush(int color);

    [DllImport(Gdi32)]
    internal static extern IntPtr CreatePen(int style, int width, int color);

    [DllImport(Gdi32)]
    internal static extern IntPtr GetStockObject(int index);

    [DllImport(Gdi32)]
    internal static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport(Gdi32)]
    internal static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport(Gdi32)]
    internal static extern int SetBkColor(IntPtr hdc, int color);

    /// <summary>DrawText lives in user32, not gdi32 (an easy way to get a silent, unpainted window).</summary>
    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport(Gdi32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFontW(
        int cHeight,
        int cWidth,
        int cEscapement,
        int cOrientation,
        int cWeight,
        uint bItalic,
        uint bUnderline,
        uint bStrikeOut,
        uint iCharSet,
        uint iOutPrecision,
        uint iClipPrecision,
        uint iQuality,
        uint iPitchAndFamily,
        string pszFaceName);

    [DllImport(Gdi32, CharSet = CharSet.Unicode)]
    internal static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out SIZE psizl);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
    }

    [DllImport(Gdi32)]
    internal static extern bool SetViewportOrgEx(IntPtr hdc, int x, int y, out POINT lppt);

    [DllImport(Gdi32)]
    internal static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport(Gdi32)]
    internal static extern int SaveDC(IntPtr hdc);

    [DllImport(Gdi32)]
    internal static extern bool RestoreDC(IntPtr hdc, int nSavedDC);

    [DllImport(Gdi32)]
    internal static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [DllImport(Gdi32)]
    internal static extern bool LineTo(IntPtr hdc, int x, int y);

    // ---------- common dialog ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        /// <summary>Mutable on purpose: the dialog writes the chosen path into this buffer.</summary>
        public StringBuilder? lpstrFile;
        public int nMaxFile;
        public StringBuilder? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    internal const int OFN_READONLY = 0x00000001;
    internal const int OFN_OVERWRITEPROMPT = 0x00000002;
    internal const int OFN_FILEMUSTEXIST = 0x00001000;
    internal const int OFN_PATHMUSTEXIST = 0x00000800;
    internal const int OFN_NOCHANGEDIR = 0x00000008;
    internal const int OFN_EXPLORER = 0x00080000;
    internal const int OFN_HIDEREADONLY = 0x00000004;

    [DllImport(ComDlg32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetOpenFileNameW(ref OPENFILENAMEW lpofn);

    [DllImport(Kernel32, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
