using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetworkGuardian.Windows.Tray;

public enum TrayClickKind
{
    LeftClick,
    LeftDoubleClick,
    RightClick,
}

public sealed record TrayMenuItem(string Id, string Text, bool Enabled = true, bool Checked = false, bool IsSeparator = false)
{
    public static TrayMenuItem Separator => new("-", string.Empty, IsSeparator: true);
}

/// <summary>
/// System tray icon implemented directly on <c>Shell_NotifyIcon</c>.
/// </summary>
/// <remarks>
/// The class creates a hidden Win32 window on the calling thread, so that thread must pump
/// messages (the WinUI dispatcher thread does). No third-party tray library is used.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAY_CALLBACK = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_DESTROY = 0x0002;
    private const int WM_COMMAND = 0x0111;
    private const int WM_QUERYENDSESSION = 0x0011;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_SETVERSION = 0x00000004;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int NIF_SHOWTIP = 0x00000080;

    private const int NOTIFYICON_VERSION_4 = 4;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_GRAYED = 0x00000001;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;
    private const uint LR_SHARED = 0x00008000;

    private const int IDI_APPLICATION = 32512;

    private readonly ILogger<TrayIcon> _logger;
    private readonly WndProcDelegate _wndProc;
    private readonly uint _taskbarCreatedMessage;
    private readonly int _iconId = 1;
    private readonly object _gate = new();
    private readonly Dictionary<int, string> _menuCommands = new();

    private IntPtr _window;
    private IntPtr _icon;
    private bool _added;
    private bool _disposed;
    private string _tooltip;

    public TrayIcon(string tooltip, ILogger<TrayIcon>? logger = null)
    {
        _logger = logger ?? NullLogger<TrayIcon>.Instance;
        _tooltip = Truncate(tooltip, 127);
        _wndProc = WindowProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    public event EventHandler<TrayClickKind>? Clicked;

    /// <summary>Supplies the popup menu contents when the user right-clicks the icon.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    /// <summary>Invoked with the identifier of the selected menu item.</summary>
    public Action<string>? MenuCommandSelected { get; set; }

    public bool IsVisible
    {
        get
        {
            lock (_gate)
            {
                return _added;
            }
        }
    }

    public bool Show(string? iconPath = null)
    {
        if (_disposed)
        {
            return false;
        }

        lock (_gate)
        {
            if (_window == IntPtr.Zero && !CreateHiddenWindow())
            {
                return false;
            }

            _icon = LoadTrayIcon(iconPath);
            var data = BuildIconData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;

            var result = Shell_NotifyIcon(_added ? NIM_MODIFY : NIM_ADD, ref data);
            if (result)
            {
                if (!_added)
                {
                    // uTimeout and uVersion are a union in NOTIFYICONDATA: writing 4 here selects
                    // NOTIFYICON_VERSION_4. With version 4 the timeout value is ignored by Windows.
                    var version = data;
                    version.uTimeout = NOTIFYICON_VERSION_4;
                    Shell_NotifyIcon(NIM_SETVERSION, ref version);
                    _added = true;
                    _logger.LogInformation("Tray icon created");
                }

                return true;
            }

            _logger.LogWarning("Shell_NotifyIcon failed while creating the tray icon");
            return false;
        }
    }

    public void Hide()
    {
        lock (_gate)
        {
            if (!_added || _window == IntPtr.Zero)
            {
                return;
            }

            var data = BuildIconData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
            _logger.LogInformation("Tray icon removed");
        }
    }

    public void SetTooltip(string tooltip)
    {
        lock (_gate)
        {
            _tooltip = Truncate(tooltip, 127);
            if (!_added || _window == IntPtr.Zero)
            {
                return;
            }

            var data = BuildIconData();
            data.uFlags = NIF_TIP | NIF_SHOWTIP;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    public void ShowBalloon(string title, string message, int timeoutMilliseconds = 5000)
    {
        lock (_gate)
        {
            if (!_added || _window == IntPtr.Zero)
            {
                return;
            }

            var data = BuildIconData();
            data.uFlags = NIF_INFO;
            data.uTimeout = (uint)Math.Max(1000, timeoutMilliseconds);
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(message, 255);
            data.dwInfoFlags = 0x00000001; // NIIF_INFO
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    private bool CreateHiddenWindow()
    {
        var className = $"NetworkGuardianTrayWindow_{Environment.ProcessId}";
        var instance = GetModuleHandle(null);

        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = className,
        };

        var atom = RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            var error = Marshal.GetLastWin32Error();
            // 1410 = ERROR_CLASS_ALREADY_EXISTS, which is fine.
            if (error != 1410)
            {
                _logger.LogWarning("RegisterClassEx failed with error {Error}", error);
                return false;
            }
        }

        _window = CreateWindowEx(
            0,
            className,
            "NetworkGuardian",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            _logger.LogWarning("CreateWindowEx for the tray window failed with error {Error}",
                Marshal.GetLastWin32Error());
            return false;
        }

        return true;
    }

    private NOTIFYICONDATA BuildIconData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window,
            uID = (uint)_iconId,
            uCallbackMessage = WM_TRAY_CALLBACK,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    private static IntPtr LoadTrayIcon(string? iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            var fromFile = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (fromFile != IntPtr.Zero)
            {
                return fromFile;
            }
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            var extracted = ExtractIconEx(executable, 0, out var large, out var small, 1);
            if (extracted > 0)
            {
                // Prefer the small icon for the notification area and release the other one.
                if (large != IntPtr.Zero)
                {
                    DestroyIcon(large);
                }

                if (small != IntPtr.Zero)
                {
                    return small;
                }
            }
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
            {
                // Explorer restarted: the notification area lost our icon, so add it again.
                _logger.LogInformation("Explorer restarted; re-adding the tray icon");
                lock (_gate)
                {
                    _added = false;
                }

                Show();
                return IntPtr.Zero;
            }

            switch ((int)message)
            {
                case WM_TRAY_CALLBACK:
                {
                    // LOWORD(lParam) carries the notification; the conversion is intentional and cannot
                    // overflow because the message id is always a small value.
                    var notification = unchecked((int)(long)lParam);
                    switch (notification)
                    {
                        case WM_LBUTTONUP:
                            Raise(TrayClickKind.LeftClick);
                            break;
                        case WM_LBUTTONDBLCLK:
                            Raise(TrayClickKind.LeftDoubleClick);
                            break;
                        case WM_RBUTTONUP:
                        case WM_CONTEXTMENU:
                            Raise(TrayClickKind.RightClick);
                            ShowContextMenu();
                            break;
                    }

                    return IntPtr.Zero;
                }

                case WM_COMMAND:
                {
                    var id = unchecked((int)(wParam.ToInt64() & 0xFFFF));
                    if (_menuCommands.TryGetValue(id, out var commandId))
                    {
                        try
                        {
                            MenuCommandSelected?.Invoke(commandId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Tray menu command {Command} failed", commandId);
                        }
                    }

                    return IntPtr.Zero;
                }

                case WM_DESTROY:
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray window procedure threw for message 0x{Message:X}", message);
        }

        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void Raise(TrayClickKind kind)
    {
        try
        {
            Clicked?.Invoke(this, kind);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray click handler failed");
        }
    }

    private void ShowContextMenu()
    {
        var items = MenuProvider?.Invoke() ?? Array.Empty<TrayMenuItem>();
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        _menuCommands.Clear();
        var id = 1;

        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                    continue;
                }

                _menuCommands[id] = item.Id;
                var flags = MF_STRING;
                if (!item.Enabled)
                {
                    flags |= MF_GRAYED;
                }

                if (item.Checked)
                {
                    flags |= MF_CHECKED;
                }

                AppendMenu(menu, flags, (IntPtr)id, item.Text);
                id++;
            }

            GetCursorPos(out var point);

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_window);

            var selected = TrackPopupMenuEx(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                point.X,
                point.Y,
                _window,
                IntPtr.Zero);

            if (selected != 0 && _menuCommands.TryGetValue(selected, out var commandId))
            {
                try
                {
                    MenuCommandSelected?.Invoke(commandId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Tray menu command {Command} failed", commandId);
                }
            }

            PostMessage(_window, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Hide();

        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        GC.KeepAlive(_wndProc);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
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
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        /// <summary>Union of uTimeout / uVersion as declared in shellapi.h.</summary>
        public uint uTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr largeIcon, out IntPtr smallIcon, uint count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
