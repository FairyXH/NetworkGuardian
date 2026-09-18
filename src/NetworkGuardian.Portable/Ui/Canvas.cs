using System.Drawing;
using NetworkGuardian.Portable.Interop;
using static NetworkGuardian.Portable.Interop.NativeMethods;

namespace NetworkGuardian.Portable.Ui;

/// <summary>One clickable region produced while a page is drawn.</summary>
internal sealed class HitTarget
{
    public required Rectangle Rect { get; init; }

    /// <summary>What happens when the region is clicked.</summary>
    public required Action OnClick { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Region flavour used for hover and cursor feedback.</summary>
    public string Kind { get; init; } = "button";

    /// <summary>Set by the window when the mouse is inside the region.</summary>
    public bool Hovered { get; set; }
}

/// <summary>
/// Double buffered GDI drawing surface with text helpers and hit testing.
/// </summary>
/// <remarks>
/// The whole UI is painted from scratch on every frame into a memory bitmap which is then blitted to
/// the window. Nothing is retained: the layout code runs on each paint and registers the clickable
/// regions as a side effect, which removes an entire class of stale-hitbox bugs.
/// </remarks>
internal sealed class Canvas : IDisposable
{
    private readonly IntPtr _hdc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _originalBitmap;
    private readonly int _savedDc;
    private readonly int _scalePercent;

    private readonly Dictionary<(int Color, int Style, int Width), IntPtr> _pens = new();
    private readonly Dictionary<int, IntPtr> _brushes = new();

    // Current viewport translation (page scrolling) and the stack of previous ones.
    private readonly Stack<(int X, int Y)> _offsetStack = new();
    private int _offsetX;
    private int _offsetY;

    public Canvas(IntPtr targetDc, int width, int height, int scalePercent)
    {
        Target = targetDc;
        Width = width;
        Height = height;
        _scalePercent = scalePercent;

        _hdc = CreateCompatibleDC(targetDc);
        _bitmap = CreateCompatibleBitmap(targetDc, width, height);
        _originalBitmap = SelectObject(_hdc, _bitmap);
        _savedDc = SaveDC(_hdc);

        SetBkMode(_hdc, TRANSPARENT);
    }

    public IntPtr Target { get; }

    public int Width { get; }

    public int Height { get; }

    public int ScalePercent => _scalePercent;

    public int Scale(int value) => (int)Math.Round(value * _scalePercent / 100.0);

    public List<HitTarget> Hits { get; } = new();

    /// <summary>Hover lookup provided by the window (updated on every mouse move).</summary>
    public Func<Rectangle, bool>? HoverTest { get; set; }

    /// <summary>Asks whether the point over <paramref name="rect"/> (layout coordinates) is hovered.</summary>
    public bool IsHovered(Rectangle rect) => HoverTest?.Invoke(ToClient(rect)) ?? false;

    // ---------- shapes ----------

    public void Fill(Rectangle rect, Rgb color)
    {
        var native = ToRect(rect);
        FillRect(_hdc, ref native, Brush(color));
    }

    public void FillRounded(Rectangle rect, int radius, Rgb color)
    {
        var r = Scale(radius);
        var oldPen = SelectObject(_hdc, GetStockObject(NULL_PEN));
        var oldBrush = SelectObject(_hdc, Brush(color));
        RoundRect(_hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, r, r);
        SelectObject(_hdc, oldBrush);
        SelectObject(_hdc, oldPen);
    }

    public void StrokeRounded(Rectangle rect, int radius, Rgb color, int thickness = 1)
    {
        var r = Scale(radius);
        var oldBrush = SelectObject(_hdc, GetStockObject(NULL_BRUSH));
        var oldPen = SelectObject(_hdc, Pen(color, Scale(thickness)));
        RoundRect(_hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, r, r);
        SelectObject(_hdc, oldPen);
        SelectObject(_hdc, oldBrush);
    }

    /// <summary>Card with an optional accent bar: the "glass" look of the WinUI version.</summary>
    public void Card(Rectangle rect, Rgb? border = null)
    {
        FillRounded(rect, 10, Palette.CardBackground);
        StrokeRounded(rect, 10, border ?? Palette.CardBorder);
    }

    public void Line(int x1, int y1, int x2, int y2, Rgb color, int thickness = 1)
    {
        var oldPen = SelectObject(_hdc, Pen(color, Scale(thickness)));
        MoveToEx(_hdc, x1, y1, IntPtr.Zero);
        LineTo(_hdc, x2, y2);
        SelectObject(_hdc, oldPen);
    }

    // ---------- text ----------

    public void Text(
        string text,
        Rectangle rect,
        Rgb color,
        TextStyle style = TextStyle.Body,
        TextAlign align = TextAlign.Left,
        TextWrap wrap = TextWrap.None)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var oldFont = SelectObject(_hdc, Fonts.Get(style, _scalePercent));
        SetTextColor(_hdc, color.ColorRef);

        var flags = DT_NOPREFIX;
        if (wrap == TextWrap.None)
        {
            flags |= DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS;
        }
        else
        {
            flags |= DT_WORDBREAK | DT_EDITCONTROL;
        }

        flags |= align switch
        {
            TextAlign.Center => DT_CENTER,
            TextAlign.Right => DT_RIGHT,
            _ => DT_LEFT,
        };

        var native = ToRect(rect);
        DrawTextW(_hdc, text, -1, ref native, (uint)flags);
        SelectObject(_hdc, oldFont);
    }

    public Size Measure(string text, TextStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Size.Empty;
        }

        var oldFont = SelectObject(_hdc, Fonts.Get(style, _scalePercent));
        GetTextExtentPoint32W(_hdc, text, text.Length, out var size);
        SelectObject(_hdc, oldFont);

        return new Size(size.cx, size.cy);
    }

    /// <summary>Height a wrapped text needs inside the given width.</summary>
    public int MeasureWrappedHeight(string text, int width, TextStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var oldFont = SelectObject(_hdc, Fonts.Get(style, _scalePercent));
        var rect = new RECT { Left = 0, Top = 0, Right = Math.Max(1, width), Bottom = 0 };
        DrawTextW(_hdc, text, -1, ref rect, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX | DT_EDITCONTROL);
        SelectObject(_hdc, oldFont);

        return rect.Height;
    }

    // ---------- clipping / offset ----------

    public void PushClip(Rectangle rect)
    {
        SaveDC(_hdc);
        IntersectClipRect(_hdc, rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public void PopClip()
    {
        RestoreDC(_hdc, -1);
    }

    /// <summary>Offsets everything drawn afterwards, used for page scrolling.</summary>
    public void PushOffset(int dx, int dy)
    {
        SaveDC(_hdc);
        SetViewportOrgEx(_hdc, dx, dy, out _);
        _offsetStack.Push((_offsetX, _offsetY));
        _offsetX += dx;
        _offsetY += dy;
    }

    public void PopOffset()
    {
        RestoreDC(_hdc, -1);
        if (_offsetStack.Count > 0)
        {
            var previous = _offsetStack.Pop();
            _offsetX = previous.X;
            _offsetY = previous.Y;
        }
    }

    /// <summary>
    /// Maps a layout rectangle to client coordinates. A scrolled page draws its layout shifted, so
    /// anything addressed in client coordinates (hit regions, native child controls, popup menus)
    /// has to be translated by the current viewport offset.
    /// </summary>
    public Rectangle ToClient(Rectangle rect) =>
        new(rect.Left + _offsetX, rect.Top + _offsetY, rect.Width, rect.Height);

    // ---------- hit testing ----------

    /// <summary>
    /// Records a clickable region. <paramref name="rect"/> is in layout coordinates; the stored
    /// region is translated to client coordinates so mouse messages can be matched directly.
    /// </summary>
    public void Hit(Rectangle rect, Action onClick, bool enabled = true, string kind = "button") =>
        Hits.Add(new HitTarget { Rect = ToClient(rect), OnClick = onClick, Enabled = enabled, Kind = kind });

    // ---------- internals ----------

    private IntPtr Brush(Rgb color)
    {
        var key = color.ColorRef;
        if (_brushes.TryGetValue(key, out var handle))
        {
            return handle;
        }

        handle = CreateSolidBrush(key);
        _brushes[key] = handle;
        return handle;
    }

    private IntPtr Pen(Rgb color, int width)
    {
        var key = (color.ColorRef, PS_SOLID, width);
        if (_pens.TryGetValue(key, out var handle))
        {
            return handle;
        }

        handle = CreatePen(PS_SOLID, width, color.ColorRef);
        _pens[key] = handle;
        return handle;
    }

    private static RECT ToRect(Rectangle rect) =>
        new() { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };

    public void Blit()
    {
        BitBlt(Target, 0, 0, Width, Height, _hdc, 0, 0, SRCCOPY);
    }

    public void Dispose()
    {
        SetBkMode(_hdc, TRANSPARENT);
        RestoreDC(_hdc, _savedDc);
        SelectObject(_hdc, _originalBitmap);

        foreach (var brush in _brushes.Values)
        {
            DeleteObject(brush);
        }

        foreach (var pen in _pens.Values)
        {
            DeleteObject(pen);
        }

        if (_bitmap != IntPtr.Zero)
        {
            DeleteObject(_bitmap);
        }

        if (_hdc != IntPtr.Zero)
        {
            DeleteDC(_hdc);
        }
    }
}
