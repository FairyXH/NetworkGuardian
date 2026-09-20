using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Portable.Host;
using NetworkGuardian.Portable.Interop;
using NetworkGuardian.Portable.Services;
using static NetworkGuardian.Portable.Interop.NativeMethods;

namespace NetworkGuardian.Portable.Ui;

/// <summary>
/// The self-drawn main window: header, navigation rail, page host, scrollbar and toast.
/// </summary>
/// <remarks>
/// There is no UI framework here on purpose. A window class plus <c>WM_PAINT</c> into a memory bitmap
/// is what keeps the executable at a few megabytes: WinUI would add ~150 MB, WinForms ~68 MB and even
/// a trimmed self-contained app ~10 MB before any of this code exists.
/// </remarks>
internal sealed class MainWindow
{
    public const string ClassName = "NetworkGuardianPortableWindow";
    public const string TitleText = "NetworkGuardian - 网络保活";

    private const int HeaderHeight = 64;
    private const int SidebarWidth = 196;
    private const int ToastHeight = 34;
    private const int EditorControlId = 100;
    private const int ScrollLineHeight = 44;
    private const int ToastTimerId = 1;

    private static MainWindow? _current;

    // Held for the lifetime of the window: the native window class keeps the raw function pointer.
    private static readonly WndProcDelegate WindowProcDelegate = WindowProc;

    private readonly PortableApp _app;
    private readonly GuardianHostService _host;
    private readonly List<IPage> _pages;
    private readonly Dictionary<string, int> _scrollByPage = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HitTarget> _hits = new();

    private IntPtr _hwnd;
    private IntPtr _foregroundBrush;
    private IntPtr _fieldBrush;
    private IntPtr _editor;
    private FieldEdit? _edit;
    private IPage _page;
    private int _scalePercent = 100;
    private int _scrollY;
    private int _contentHeight;
    private int _hoverIndex = -1;
    private int _pressedIndex = -1;
    private int _toastTicks;
    private string? _toast;
    private int _activityFrame;
    private readonly ConcurrentDictionary<string, string> _operations = new(StringComparer.Ordinal);

    public MainWindow(PortableApp app, GuardianHostService host, IReadOnlyList<IPage> pages, string startPage)
    {
        _app = app;
        _host = host;
        _pages = pages.ToList();
        _page = _pages.FirstOrDefault(p => string.Equals(p.Tag, startPage, StringComparison.OrdinalIgnoreCase)) ?? _pages[0];
        _current = this;

        var instance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcDelegate),
            hInstance = instance,
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(IDC_ARROW)),
            lpszClassName = ClassName,
        };

        if (RegisterClassExW(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            // 1410 = ERROR_CLASS_ALREADY_EXISTS, which is fine for a second window in the same process.
            if (error != 1410)
            {
                throw new InvalidOperationException($"RegisterClassExW failed with error {error}");
            }
        }

        var style = WS_OVERLAPPEDWINDOW | WS_VSCROLL | WS_CLIPCHILDREN;
        var rect = new RECT { Left = 0, Top = 0, Right = 1180, Bottom = 800 };
        AdjustWindowRectEx(ref rect, style, false, 0);

        _hwnd = CreateWindowExW(
            0,
            ClassName,
            TitleText,
            style,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            rect.Width,
            rect.Height,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowExW failed with error {Marshal.GetLastWin32Error()}");
        }

        _foregroundBrush = CreateSolidBrush(Palette.WindowBackground.ColorRef);
        _fieldBrush = CreateSolidBrush(Palette.FieldBackground.ColorRef);

        _scalePercent = ReadScalePercent();
        SetTimer(_hwnd, new IntPtr(ToastTimerId), 500, IntPtr.Zero);
    }

    public IntPtr Handle => _hwnd;

    public PortableApp App => _app;

    public IPage CurrentPage => _page;

    /// <summary>True while the view is at (or within one line of) the end of the page.</summary>
    public bool IsScrolledToEnd
    {
        get
        {
            var maximum = Math.Max(0, _contentHeight - ContentRect.Height);
            return _scrollY >= maximum - Scale(ScrollLineHeight);
        }
    }

    public void ScrollToEnd()
    {
        var maximum = Math.Max(0, _contentHeight - ContentRect.Height);
        SetScroll(maximum);
    }

    public bool IsVisible => IsWindowVisible(_hwnd) && !IsIconic(_hwnd);

    public void Show()
    {
        if (IsIconic(_hwnd))
        {
            ShowWindow(_hwnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(_hwnd, SW_SHOWNORMAL);
        }

        SetForegroundWindow(_hwnd);
        Invalidate();
    }

    public void Hide() => ShowWindow(_hwnd, SW_HIDE);

    public void Invalidate()
    {
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    /// <summary>Repaints from a background thread (the monitor loop publishes snapshots there).</summary>
    public void RequestRefresh() => PostMessageW(_hwnd, (uint)WM_APP_REFRESH, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Ends the message loop from any thread (WM_QUIT is thread local).</summary>
    public void PostQuit() => PostMessageW(_hwnd, (uint)WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);

    public void ShowToast(string message)
    {
        _toast = message;
        _toastTicks = 8;
        Invalidate();
    }

    public bool Confirm(string message, string caption = "请确认") =>
        MessageBoxW(_hwnd, message, caption, MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2) == IDYES;

    /// <summary>True when the hovered region contains <paramref name="rect"/> (client coordinates).</summary>
    public bool IsHovered(Rectangle rect)
    {
        if (_hoverIndex < 0 || _hoverIndex >= _hits.Count)
        {
            return false;
        }

        return _hits[_hoverIndex].Rect.Contains(rect);
    }

    public void NavigateTo(string tag)
    {
        var page = _pages.FirstOrDefault(p => string.Equals(p.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (page is null || ReferenceEquals(page, _page))
        {
            return;
        }

        CloseEditor(commit: true);
        _page = page;
        _scrollY = 0;
        Invalidate();
    }

    public bool IsOperationRunning(string operationId) => _operations.ContainsKey(operationId);

    /// <summary>Runs one named background action with visible progress and duplicate-click protection.</summary>
    public void RunBackground(
        string operationId,
        string busyMessage,
        Func<Task> work,
        string? successToast = null,
        string failurePrefix = "操作失败")
    {
        if (!_operations.TryAdd(operationId, busyMessage))
        {
            return;
        }

        CloseEditor(commit: true);
        RequestRefresh();

        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
                if (successToast is not null)
                {
                    ShowToast(successToast);
                }
            }
            catch (Exception ex)
            {
                _host.LogUiFailure(ex.Message);
                ShowToast($"{failurePrefix}：{ex.Message}");
            }
            finally
            {
                _operations.TryRemove(operationId, out _);
            }

            RequestRefresh();
        });
    }

    // ---------- inline text editor ----------

    public void BeginEdit(FieldEdit edit)
    {
        CloseEditor(commit: true);

        _edit = edit;
        var style = WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_LEFT;
        style |= edit.Multiline ? ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN : ES_AUTOHSCROLL;

        _editor = CreateWindowExW(
            0,
            "EDIT",
            edit.Initial,
            style,
            edit.Rect.Left,
            edit.Rect.Top,
            edit.Rect.Width,
            edit.Rect.Height,
            _hwnd,
            new IntPtr(EditorControlId),
            GetModuleHandleW(null),
            IntPtr.Zero);

        if (_editor == IntPtr.Zero)
        {
            _edit = null;
            ShowToast("无法创建输入框");
            return;
        }

        SendMessageW(_editor, WM_SETFONT, Fonts.Get(TextStyle.Body, _scalePercent), new IntPtr(1));
        SendMessageW(_editor, EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
        SetFocus(_editor);
        Invalidate();
    }

    public void CloseEditor(bool commit)
    {
        if (_editor == IntPtr.Zero)
        {
            return;
        }

        var edit = _edit;
        var text = ReadEditorText();

        if (commit && edit is not null && !string.Equals(text, edit.Initial, StringComparison.Ordinal))
        {
            try
            {
                edit.Commit(text);
            }
            catch (Exception ex)
            {
                _host.LogUiFailure(ex.Message);
                ShowToast($"保存失败：{ex.Message}");
            }
        }

        DestroyWindow(_editor);
        _editor = IntPtr.Zero;
        _edit = null;
        Invalidate();
    }

    private string ReadEditorText()
    {
        var length = GetWindowTextLengthW(_editor);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 2);
        GetWindowTextW(_editor, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    // ---------- dropdown ----------

    public void ShowPopupMenu(Rectangle rect, IReadOnlyList<string> items, int selected, Action<int> onSelect)
    {
        CloseEditor(commit: true);

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var flags = MF_STRING | (i == selected ? MF_CHECKED : 0u);
                AppendMenuW(menu, flags, new IntPtr(i + 1), items[i]);
            }

            var point = new POINT { X = rect.Left, Y = rect.Bottom };
            ClientToScreen(_hwnd, ref point);

            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(
                menu,
                TPM_LEFTALIGN | TPM_TOPALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                point.X,
                point.Y,
                _hwnd,
                IntPtr.Zero);

            if (chosen > 0)
            {
                onSelect(chosen - 1);
            }
        }
        finally
        {
            DestroyMenu(menu);
            Invalidate();
        }
    }

    public void RestoreOriginalTitle() => SetWindowTextW(_hwnd, TitleText);

    // ---------- window procedure ----------

    private static IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var window = _current;
        if (window is null || window._hwnd != hWnd)
        {
            return DefWindowProcW(hWnd, message, wParam, lParam);
        }

        try
        {
            return window.HandleMessage(message, wParam, lParam);
        }
        catch (Exception ex)
        {
            // A UI exception must never take the process (and the monitor loop) down.
            window._host.LogUiFailure($"{message:X}: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private IntPtr HandleMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1);

            case WM_PAINT:
                Paint();
                return IntPtr.Zero;

            case WM_PRINTCLIENT:
                // PrintWindow support: the whole UI is drawn into the caller's DC, which lets the
                // screenshot tool capture the window even while it is behind another window or
                // hidden in the tray.
                PaintInto(wParam);
                return IntPtr.Zero;

            case WM_SIZE:
                UpdateScalePercent();
                ClampScroll();
                Invalidate();
                return IntPtr.Zero;

            case WM_DPICHANGED:
                UpdateScalePercent();
                Invalidate();
                return IntPtr.Zero;

            case WM_APP_REFRESH:
                Invalidate();
                return IntPtr.Zero;

            case WM_APP_SHOW_WINDOW:
                Show();
                return IntPtr.Zero;

            case (uint)WM_APP_QUIT:
                // Destroy on the owning thread so WM_DESTROY can clean up (editor, timer), then let
                // the message loop return.
                DestroyWindow(_hwnd);
                PostQuitMessage(0);
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                UpdateHover(lParam);
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                _pressedIndex = HitTest(lParam);
                _host.LogUiDebug($"mouse down at {Point(lParam)} -> hit {_pressedIndex}");
                SetCapture(_hwnd);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
            {
                ReleaseCapture();
                var released = HitTest(lParam);
                var pressed = _pressedIndex;
                _pressedIndex = -1;

                _host.LogUiDebug($"mouse up at {Point(lParam)} -> hit {released} (pressed {pressed}, " +
                    $"targets {_hits.Count}, scroll {_scrollY})");

                if (pressed >= 0 && pressed == released && _hits[pressed].Enabled)
                {
                    var target = _hits[pressed].OnClick;
                    target();
                    // Most controls mutate in-memory state synchronously. Paint that state now instead
                    // of waiting for the next monitor snapshot, which can be several seconds away.
                    Invalidate();
                }

                return IntPtr.Zero;
            }

            case WM_MOUSEWHEEL:
            {
                var delta = unchecked((short)((long)wParam >> 16));
                var lines = ReadWheelLines();
                ScrollBy(-delta / WHEEL_DELTA * lines * Scale(ScrollLineHeight));
                return IntPtr.Zero;
            }

            case WM_VSCROLL:
            {
                var request = (int)(wParam.ToInt64() & 0xFFFF);
                var viewport = ContentRect.Height;
                switch (request)
                {
                    case SB_LINEUP:
                        ScrollBy(-Scale(ScrollLineHeight));
                        break;
                    case SB_LINEDOWN:
                        ScrollBy(Scale(ScrollLineHeight));
                        break;
                    case SB_PAGEUP:
                        ScrollBy(-viewport);
                        break;
                    case SB_PAGEDOWN:
                        ScrollBy(viewport);
                        break;
                    case SB_TOP:
                        SetScroll(0);
                        break;
                    case SB_BOTTOM:
                        SetScroll(int.MaxValue);
                        break;
                    case SB_THUMBTRACK:
                    case SB_THUMBPOSITION:
                        SetScroll((int)((long)wParam >> 16));
                        break;
                }

                return IntPtr.Zero;
            }

            case WM_KEYDOWN:
                switch ((int)wParam)
                {
                    case 0x1B: // VK_ESCAPE
                        CloseEditor(commit: false);
                        return IntPtr.Zero;
                    case 0x21: // VK_PRIOR
                        ScrollBy(-ContentRect.Height);
                        return IntPtr.Zero;
                    case 0x22: // VK_NEXT
                        ScrollBy(ContentRect.Height);
                        return IntPtr.Zero;
                    default:
                        return IntPtr.Zero;
                }

            case WM_COMMAND:
            {
                var controlId = (int)(wParam.ToInt64() & 0xFFFF);
                var notification = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (controlId == EditorControlId && (notification == EN_KILLFOCUS || notification == 0))
                {
                    CloseEditor(commit: true);
                }

                return IntPtr.Zero;
            }

            case WM_CTLCOLOREDIT:
            case WM_CTLCOLORSTATIC:
            {
                var dc = wParam;
                SetTextColor(dc, Palette.TextPrimary.ColorRef);
                SetBkColor(dc, Palette.FieldBackground.ColorRef);
                return _fieldBrush;
            }

            case WM_SETCURSOR:
            {
                if (_hoverIndex >= 0 && _hoverIndex < _hits.Count)
                {
                    SetCursor(LoadCursorW(IntPtr.Zero, new IntPtr(IDC_HAND)));
                    return new IntPtr(1);
                }

                SetCursor(LoadCursorW(IntPtr.Zero, new IntPtr(IDC_ARROW)));
                return DefWindowProcW(_hwnd, message, wParam, lParam);
            }

            case WM_TIMER:
                if (!_operations.IsEmpty)
                {
                    _activityFrame = (_activityFrame + 1) % 4;
                    Invalidate();
                }

                if ((int)wParam == ToastTimerId && _toastTicks > 0)
                {
                    _toastTicks--;
                    if (_toastTicks == 0)
                    {
                        _toast = null;
                        Invalidate();
                    }
                }

                return IntPtr.Zero;

            case WM_POWERBROADCAST:
            {
                var powerEvent = (int)wParam;
                if (powerEvent is (int)PBT_APMRESUMEAUTOMATIC or (int)PBT_APMRESUMESUSPEND)
                {
                    _host.NotifySystemResumed();
                    ShowToast("检测到系统唤醒，正在稳定网络…");
                }

                return IntPtr.Zero;
            }

            case WM_CLOSE:
                _app.RequestClose();
                return IntPtr.Zero;

            case WM_DESTROY:
                CloseEditor(commit: false);
                KillTimer(_hwnd, new IntPtr(ToastTimerId));
                return IntPtr.Zero;

            default:
                return DefWindowProcW(_hwnd, message, wParam, lParam);
        }
    }

    private void UpdateHover(IntPtr lParam)
    {
        var index = HitTest(lParam);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }
    }

    private int HitTest(IntPtr lParam)
    {
        var x = unchecked((short)(long)lParam);
        var y = unchecked((short)((long)lParam >> 16));

        // Later regions win: pages register after the shell, so they sit on top.
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Rect.Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Formats the mouse coordinates carried in a mouse message.</summary>
    private static string Point(IntPtr lParam)
    {
        var x = unchecked((short)(long)lParam);
        var y = unchecked((short)((long)lParam >> 16));
        return $"{x},{y}";
    }

    // ---------- scrolling ----------

    private Rectangle ContentRect
    {
        get
        {
            GetClientRect(_hwnd, out var client);
            var left = Scale(SidebarWidth);
            var top = Scale(HeaderHeight);
            return new Rectangle(left, top, Math.Max(0, client.Width - left), Math.Max(0, client.Height - top));
        }
    }

    private void ScrollBy(int delta) => SetScroll(_scrollY + delta);

    private void SetScroll(int value)
    {
        var viewport = ContentRect.Height;
        var maximum = Math.Max(0, _contentHeight - viewport);
        var clamped = Math.Clamp(value, 0, maximum);

        if (clamped == _scrollY)
        {
            return;
        }

        _scrollY = clamped;
        Invalidate();
    }

    private void ClampScroll() => SetScroll(_scrollY);

    private void UpdateScrollBar()
    {
        var viewport = ContentRect.Height;
        var info = new SCROLLINFO
        {
            cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
            fMask = SIF_RANGE | SIF_PAGE | SIF_POS | SIF_DISABLENOSCROLL,
            nMin = 0,
            nMax = Math.Max(0, _contentHeight - 1),
            nPage = (uint)Math.Max(1, viewport),
            nPos = _scrollY,
        };

        SetScrollInfo(_hwnd, SB_VERT, ref info, true);
    }

    private static int ReadWheelLines()
    {
        if (SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, out var lines, 0) && lines > 0)
        {
            return Math.Min(lines, 10);
        }

        return 3;
    }

    // ---------- painting ----------

    private int ReadScalePercent()
    {
        var dpi = _hwnd == IntPtr.Zero ? 96u : GetDpiForWindow(_hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        return (int)Math.Round(dpi * 100.0 / 96.0);
    }

    private void UpdateScalePercent()
    {
        var percent = ReadScalePercent();
        if (percent != _scalePercent)
        {
            _scalePercent = percent;
        }
    }

    private int Scale(int value) => (int)Math.Round(value * _scalePercent / 100.0);

    private void Paint()
    {
        var hdc = BeginPaint(_hwnd, out var paint);
        try
        {
            PaintInto(hdc);
        }
        finally
        {
            EndPaint(_hwnd, ref paint);
        }
    }

    /// <summary>Draws the complete window into <paramref name="hdc"/> on a memory bitmap.</summary>
    private void PaintInto(IntPtr hdc)
    {
        GetClientRect(_hwnd, out var client);
        if (client.Width <= 0 || client.Height <= 0 || hdc == IntPtr.Zero)
        {
            return;
        }

        using var canvas = new Canvas(hdc, client.Width, client.Height, _scalePercent)
        {
            HoverTest = rect => IsHovered(rect),
        };

        canvas.Fill(new Rectangle(0, 0, client.Width, client.Height), Palette.WindowBackground);

        _hits.Clear();
        DrawHeader(canvas, client);
        DrawSidebar(canvas, client);
        DrawContent(canvas, client);
        DrawToast(canvas, client);

        // The canvas records one hit region per interactive control while it draws. Without this copy
        // the window keeps an empty list and every click, hover and hand cursor silently does nothing
        // (the layout itself still looks correct, which is what makes it easy to miss).
        _hits.AddRange(canvas.Hits);

        canvas.Blit();
    }

    private void DrawHeader(Canvas canvas, RECT client)
    {
        var height = Scale(HeaderHeight);
        canvas.Fill(new Rectangle(0, 0, client.Width, height), Palette.HeaderBackground);
        canvas.Line(0, height - 1, client.Width, height, Palette.CardBorder);

        var snapshot = _host.Snapshot;
        var padding = Scale(20);
        var pageContext = new PageContext { Canvas = canvas, Window = this, Host = _host };

        canvas.Text("NetworkGuardian", new Rectangle(padding, Scale(10), Scale(420), Scale(28)), Palette.TextPrimary, TextStyle.PageTitle);
        canvas.Text(
            $"配置 {_host.ConfigPath}",
            new Rectangle(padding, Scale(36), Scale(520), Scale(20)),
            Palette.TextMuted,
            TextStyle.Caption);

        var right = client.Width - padding;
        var buttonHeight = Scale(32);
        var buttonTop = Scale(16);

        const string testOperation = "connectivity-test";
        var testing = IsOperationRunning(testOperation);
        var testLabel = testing ? "测试中…" : "连通性测试";
        var testWidth = Widgets.MeasureButtonWidth(pageContext, "连通性测试");
        var testRect = new Rectangle(right - testWidth, buttonTop, testWidth, buttonHeight);
        Widgets.ButtonAt(pageContext, testRect, testLabel,
            () => RunBackground(testOperation, "正在执行连通性测试", () => _host.RunConnectivityTestAsync(CancellationToken.None), "连通性测试完成"),
            enabled: !testing);

        var pauseLabel = _host.IsPaused ? "恢复自动恢复" : "暂停自动恢复";
        var pauseWidth = Widgets.MeasureButtonWidth(pageContext, pauseLabel);
        var pauseRect = new Rectangle(testRect.Left - Scale(10) - pauseWidth, buttonTop, pauseWidth, buttonHeight);
        Widgets.ButtonAt(pageContext, pauseRect, pauseLabel, () =>
        {
            _host.SetPaused(!_host.IsPaused);
            Invalidate();
        });

        var (statusText, statusColor) = (_host.IsPaused
            ? "已暂停"
            : snapshot.GlobalProbe.IsOnline ? "外网正常" : "外网中断",
            _host.IsPaused ? Palette.Warn : snapshot.GlobalProbe.IsOnline ? Palette.Good : Palette.Bad);

        canvas.Text(
            statusText,
            new Rectangle(Math.Max(padding, pauseRect.Left - Scale(190)), buttonTop, Scale(180), buttonHeight),
            statusColor,
            TextStyle.Value,
            TextAlign.Right);
    }

    private void DrawSidebar(Canvas canvas, RECT client)
    {
        var width = Scale(SidebarWidth);
        var top = Scale(HeaderHeight);
        canvas.Fill(new Rectangle(0, top, width, client.Height - top), Palette.SidebarBackground);
        canvas.Line(width - 1, top, width, client.Height, Palette.CardBorder);

        var y = top + Scale(14);
        var itemHeight = Scale(38);

        foreach (var page in _pages)
        {
            var rect = new Rectangle(Scale(8), y, width - Scale(16), itemHeight);
            var active = ReferenceEquals(page, _page);
            var hovered = canvas.IsHovered(rect);

            if (active)
            {
                canvas.FillRounded(rect, 6, Palette.AccentBackground);
            }
            else if (hovered)
            {
                canvas.FillRounded(rect, 6, Palette.SubtleBackground);
            }

            canvas.Text(
                page.Label,
                new Rectangle(rect.Left + Scale(14), rect.Top, rect.Width - Scale(20), rect.Height),
                active ? Palette.Accent : Palette.TextPrimary,
                TextStyle.Nav);

            var target = page;
            canvas.Hit(rect, () => NavigateTo(target.Tag), kind: "nav");
            y += itemHeight + Scale(2);
        }

        var snapshot = _host.Snapshot;
        var infoY = client.Height - Scale(96);
        var infoWidth = width - Scale(24);

        canvas.Text("运行状态", new Rectangle(Scale(12), infoY, infoWidth, Scale(20)), Palette.TextMuted, TextStyle.Caption);
        canvas.Text(
            $"物理无线网卡 {snapshot.WifiAdapters.Count} 个",
            new Rectangle(Scale(12), infoY + Scale(20), infoWidth, Scale(20)),
            Palette.TextSecondary,
            TextStyle.Caption);
        canvas.Text(
            $"上次快照 {snapshot.TimestampUtc.ToLocalTime():HH:mm:ss}",
            new Rectangle(Scale(12), infoY + Scale(40), infoWidth, Scale(20)),
            Palette.TextSecondary,
            TextStyle.Caption);
        canvas.Text(
            "自动恢复 " + (snapshot.IsAutomaticRecoveryEnabled ? "开启" : "关闭"),
            new Rectangle(Scale(12), infoY + Scale(60), infoWidth, Scale(20)),
            Palette.TextSecondary,
            TextStyle.Caption);
    }

    private void DrawContent(Canvas canvas, RECT client)
    {
        var content = ContentRect;
        if (content.Width <= 0 || content.Height <= 0)
        {
            return;
        }

        canvas.PushClip(content);
        canvas.PushOffset(0, -_scrollY);

        var area = new Rectangle(content.Left + Scale(20), content.Top + Scale(16), content.Width - Scale(44), 0);

        var pageContext = new PageContext { Canvas = canvas, Window = this, Host = _host };
        _contentHeight = _page.Render(pageContext, area) + Scale(40);

        canvas.PopOffset();
        canvas.PopClip();

        UpdateScrollBar();
    }

    private void DrawToast(Canvas canvas, RECT client)
    {
        var operation = _operations.FirstOrDefault();
        var hasOperation = !string.IsNullOrEmpty(operation.Key);
        if (!hasOperation && string.IsNullOrEmpty(_toast))
        {
            return;
        }

        var height = Scale(ToastHeight);
        var rect = new Rectangle(ContentRect.Left, client.Height - height, ContentRect.Width, height);
        canvas.Fill(rect, hasOperation ? Palette.AccentBackground : Palette.HeaderBackground);
        canvas.Line(rect.Left, rect.Top, rect.Right, rect.Top + 1, hasOperation ? Palette.Accent : Palette.CardBorder);

        var message = hasOperation
            ? $"{operation.Value}{new string('.', _activityFrame + 1)}  请稍候"
            : _toast!;
        canvas.Text(message, new Rectangle(rect.Left + Scale(20), rect.Top, rect.Width - Scale(40), rect.Height),
            hasOperation ? Palette.Accent : Palette.TextSecondary, TextStyle.Body);
    }

    public void Dispose()
    {
        CloseEditor(commit: false);

        if (_foregroundBrush != IntPtr.Zero)
        {
            DeleteObject(_foregroundBrush);
            _foregroundBrush = IntPtr.Zero;
        }

        if (_fieldBrush != IntPtr.Zero)
        {
            DeleteObject(_fieldBrush);
            _fieldBrush = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _current = null;
    }
}
