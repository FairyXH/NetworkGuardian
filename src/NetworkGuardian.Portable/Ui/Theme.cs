using NetworkGuardian.Portable.Interop;
using static NetworkGuardian.Portable.Interop.NativeMethods;

namespace NetworkGuardian.Portable.Ui;

/// <summary>COLORREF (0x00BBGGRR) used by GDI.</summary>
internal readonly struct Rgb
{
    public Rgb(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }

    public int ColorRef => R | (G << 8) | (B << 16);

    public static Rgb FromHex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Blends towards <paramref name="other"/>; <paramref name="amount"/> is 0..1.</summary>
    public Rgb Mix(Rgb other, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return new Rgb(
            (byte)(R + ((other.R - R) * t)),
            (byte)(G + ((other.G - G) * t)),
            (byte)(B + ((other.B - B) * t)));
    }
}

/// <summary>Dark palette of the portable UI (the WinUI version followed the system theme).</summary>
internal static class Palette
{
    public static readonly Rgb WindowBackground = Rgb.FromHex(0x16161B);
    public static readonly Rgb SidebarBackground = Rgb.FromHex(0x1B1B21);
    public static readonly Rgb CardBackground = Rgb.FromHex(0x22222A);
    public static readonly Rgb CardBorder = Rgb.FromHex(0x3A3A44);
    public static readonly Rgb SubtleBackground = Rgb.FromHex(0x2C2C36);
    public static readonly Rgb TextPrimary = Rgb.FromHex(0xF3F3F3);
    public static readonly Rgb TextSecondary = Rgb.FromHex(0xB4B4BB);
    public static readonly Rgb TextMuted = Rgb.FromHex(0x8A8A93);
    public static readonly Rgb Good = Rgb.FromHex(0x5CD37A);
    public static readonly Rgb Bad = Rgb.FromHex(0xFF8A80);
    public static readonly Rgb Warn = Rgb.FromHex(0xF2C46B);
    public static readonly Rgb Accent = Rgb.FromHex(0x7CC4FF);
    public static readonly Rgb AccentBackground = Rgb.FromHex(0x1D3A52);
    public static readonly Rgb ButtonBackground = Rgb.FromHex(0x2E2E38);
    public static readonly Rgb ButtonHover = Rgb.FromHex(0x3A3A46);
    public static readonly Rgb ButtonPrimary = Rgb.FromHex(0x2D6AA8);
    public static readonly Rgb ButtonPrimaryHover = Rgb.FromHex(0x3779BC);
    public static readonly Rgb ToggleOn = Rgb.FromHex(0x3E9A5B);
    public static readonly Rgb ToggleOff = Rgb.FromHex(0x4A4A55);
    public static readonly Rgb FieldBackground = Rgb.FromHex(0x1E1E25);
    public static readonly Rgb HeaderBackground = Rgb.FromHex(0x1C1C22);
}

internal enum TextStyle
{
    PageTitle,
    Section,
    Body,
    Caption,
    Value,
    Mono,
    Button,
    Nav,
}

internal enum TextAlign
{
    Left,
    Center,
    Right,
}

internal enum TextWrap
{
    None,
    Wrap,
}

/// <summary>Font handles created on demand. Cleared once at shutdown.</summary>
internal static class Fonts
{
    private static readonly Dictionary<(TextStyle Style, int Height), IntPtr> Cache = new();

    public static IntPtr Get(TextStyle style, int scalePercent)
    {
        var (points, weight, mono) = Describe(style);
        var height = -Math.Max(8, (int)Math.Round(points * scalePercent / 100.0));
        var key = (style, height);
        var handle = IntPtr.Zero;

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out handle) && handle != IntPtr.Zero)
            {
                return handle;
            }
        }

        handle = CreateFontW(
            height,
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            DEFAULT_CHARSET,
            0,
            0,
            CLEARTYPE_QUALITY,
            (uint)(mono ? FIXED_PITCH : VARIABLE_PITCH) | FF_DONTCARE,
            mono ? "Consolas" : "Microsoft YaHei UI");

        lock (Cache)
        {
            Cache[key] = handle;
        }

        return handle;
    }

    public static void Clear()
    {
        lock (Cache)
        {
            foreach (var handle in Cache.Values)
            {
                if (handle != IntPtr.Zero)
                {
                    DeleteObject(handle);
                }
            }

            Cache.Clear();
        }
    }

    private static (double Points, int Weight, bool Mono) Describe(TextStyle style) => style switch
    {
        TextStyle.PageTitle => (21, FW_SEMIBOLD, false),
        TextStyle.Section => (14.5, FW_SEMIBOLD, false),
        TextStyle.Value => (16, FW_SEMIBOLD, false),
        TextStyle.Button => (12.5, FW_NORMAL, false),
        TextStyle.Nav => (13.5, FW_NORMAL, false),
        TextStyle.Caption => (11.5, FW_NORMAL, false),
        TextStyle.Mono => (11.5, FW_NORMAL, true),
        _ => (12.5, FW_NORMAL, false),
    };
}
