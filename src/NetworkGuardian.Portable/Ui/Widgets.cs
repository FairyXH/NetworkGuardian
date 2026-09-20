using System.Drawing;
using NetworkGuardian.Core.Configuration;

namespace NetworkGuardian.Portable.Ui;

/// <summary>What a page receives while it renders.</summary>
internal sealed class PageContext
{
    public required Canvas Canvas { get; init; }

    public required MainWindow Window { get; init; }

    public required Services.GuardianHostService Host { get; init; }

    public GuardianConfig Config => Host.Config;

    public Core.Models.GuardianSnapshot Snapshot => Host.Snapshot;

    public int Scale(int value) => Canvas.Scale(value);
}

/// <summary>A navigable page. <see cref="Render"/> returns the content height for the scrollbar.</summary>
internal interface IPage
{
    string Tag { get; }

    string Label { get; }

    string Title { get; }

    string Description { get; }

    int Render(PageContext ctx, Rectangle area);
}

/// <summary>Editable text field handed to the window's inline editor.</summary>
internal sealed record FieldEdit(
    Rectangle Rect,
    string Initial,
    bool Multiline,
    Action<string> Commit);

/// <summary>Reusable, self-drawn controls. Every control registers its own click region.</summary>
internal static class Widgets
{
    public const int RowHeight = 26;
    public const int CardPadding = 16;
    public const int Gap = 10;

    /// <summary>Page heading: title plus the explanatory line under it.</summary>
    public static int Heading(PageContext ctx, Rectangle area, string title, string description)
    {
        var canvas = ctx.Canvas;
        canvas.Text(title, new Rectangle(area.Left, area.Top, area.Width, ctx.Scale(30)), Palette.TextPrimary, TextStyle.PageTitle);
        var y = area.Top + ctx.Scale(34);

        if (!string.IsNullOrEmpty(description))
        {
            var height = canvas.MeasureWrappedHeight(description, area.Width, TextStyle.Body);
            canvas.Text(description, new Rectangle(area.Left, y, area.Width, height), Palette.TextSecondary, TextStyle.Body, wrap: TextWrap.Wrap);
            y += height;
        }

        return y - area.Top + ctx.Scale(8);
    }

    public static void Card(PageContext ctx, Rectangle rect) => ctx.Canvas.Card(rect);

    public static int SectionTitle(PageContext ctx, int x, int y, int width, string title)
    {
        var height = ctx.Scale(22);
        ctx.Canvas.Text(title, new Rectangle(x, y, width, height), Palette.TextPrimary, TextStyle.Section);
        return height;
    }

    public static int Caption(PageContext ctx, int x, int y, int width, string text, Rgb? color = null)
    {
        var height = ctx.Scale(18);
        ctx.Canvas.Text(text, new Rectangle(x, y, width, height), color ?? Palette.TextMuted, TextStyle.Caption);
        return height;
    }

    /// <summary>Label/value pair on two lines; returns the consumed height.</summary>
    public static int KeyValue(PageContext ctx, int x, int y, int width, string label, string value, Rgb? valueColor = null)
    {
        var consumed = Caption(ctx, x, y, width, label);
        var height = ctx.Canvas.MeasureWrappedHeight(value, width, TextStyle.Body);
        height = Math.Max(height, ctx.Scale(20));
        ctx.Canvas.Text(value, new Rectangle(x, y + consumed, width, height), valueColor ?? Palette.TextPrimary, TextStyle.Body, wrap: TextWrap.Wrap);
        return consumed + height + ctx.Scale(6);
    }

    /// <summary>Mono-spaced line (identifiers, addresses, log lines).</summary>
    public static int Mono(PageContext ctx, int x, int y, int width, string text, Rgb? color = null)
    {
        var height = Math.Max(ctx.Scale(18), ctx.Canvas.MeasureWrappedHeight(text, width, TextStyle.Mono));
        ctx.Canvas.Text(text, new Rectangle(x, y, width, height), color ?? Palette.TextSecondary, TextStyle.Mono, wrap: TextWrap.Wrap);
        return height;
    }

    public static Rectangle Button(PageContext ctx, int x, int y, string label, Action onClick, bool primary = false, bool enabled = true)
    {
        var rect = new Rectangle(x, y, MeasureButtonWidth(ctx, label), ctx.Scale(32));
        ButtonAt(ctx, rect, label, onClick, primary, enabled);
        return rect;
    }

    public static int MeasureButtonWidth(PageContext ctx, string label)
    {
        var textWidth = ctx.Canvas.Measure(label, TextStyle.Button).Width;
        return Math.Max(ctx.Scale(84), textWidth + ctx.Scale(28));
    }

    /// <summary>Button drawn at an exact rectangle (used when buttons are laid out from the right edge).</summary>
    public static void ButtonAt(PageContext ctx, Rectangle rect, string label, Action onClick, bool primary = false, bool enabled = true)
    {
        var canvas = ctx.Canvas;
        var hovered = canvas.IsHovered(rect) && enabled;

        var background = primary
            ? (hovered ? Palette.ButtonPrimaryHover : Palette.ButtonPrimary)
            : (hovered ? Palette.ButtonHover : Palette.ButtonBackground);

        canvas.FillRounded(rect, 6, enabled ? background : Palette.SubtleBackground);
        canvas.StrokeRounded(rect, 6, Palette.CardBorder);
        canvas.Text(label, rect, enabled ? Palette.TextPrimary : Palette.TextMuted, TextStyle.Button, TextAlign.Center);

        if (enabled)
        {
            canvas.Hit(rect, onClick, kind: "button");
        }
    }

    /// <summary>Toggle switch with a label on the left and the switch on the right.</summary>
    public static int Toggle(PageContext ctx, int x, int y, int width, string label, bool value, Action onClick)
    {
        var canvas = ctx.Canvas;
        var height = ctx.Scale(26);
        var labelWidth = width - ctx.Scale(64);
        canvas.Text(label, new Rectangle(x, y, labelWidth, height), Palette.TextPrimary, TextStyle.Body, wrap: TextWrap.Wrap);

        var switchRect = new Rectangle(x + width - ctx.Scale(52), y + ctx.Scale(4), ctx.Scale(44), ctx.Scale(20));
        var hovered = canvas.IsHovered(switchRect);

        canvas.FillRounded(switchRect, 10, value ? Palette.ToggleOn : Palette.ToggleOff);
        var knob = ctx.Scale(16);
        var knobX = value ? switchRect.Right - knob - ctx.Scale(2) : switchRect.Left + ctx.Scale(2);
        canvas.FillRounded(new Rectangle(knobX, switchRect.Top + ctx.Scale(2), knob, knob), 8, hovered ? Palette.TextPrimary : Palette.TextSecondary);

        canvas.Hit(switchRect, onClick, kind: "toggle");
        return Math.Max(height, canvas.MeasureWrappedHeight(label, labelWidth, TextStyle.Body)) + ctx.Scale(6);
    }

    /// <summary>Read-only toggle (used for values the program does not own).</summary>
    public static int ToggleReadOnly(PageContext ctx, int x, int y, int width, string label, bool value)
    {
        var height = ctx.Scale(26);
        var canvas = ctx.Canvas;
        var labelWidth = width - ctx.Scale(64);
        canvas.Text(label, new Rectangle(x, y, labelWidth, height), Palette.TextSecondary, TextStyle.Body, wrap: TextWrap.Wrap);

        var switchRect = new Rectangle(x + width - ctx.Scale(52), y + ctx.Scale(4), ctx.Scale(44), ctx.Scale(20));
        canvas.FillRounded(switchRect, 10, value ? Palette.ToggleOn : Palette.ToggleOff);
        var knob = ctx.Scale(16);
        var knobX = value ? switchRect.Right - knob - ctx.Scale(2) : switchRect.Left + ctx.Scale(2);
        canvas.FillRounded(new Rectangle(knobX, switchRect.Top + ctx.Scale(2), knob, knob), 8, Palette.TextSecondary);

        return Math.Max(height, canvas.MeasureWrappedHeight(label, labelWidth, TextStyle.Body)) + ctx.Scale(6);
    }

    /// <summary>Text box; clicking it opens the inline editor provided by the window.</summary>
    public static int Field(PageContext ctx, int x, int y, int width, string label, string value, Action<string> commit, bool multiline = false)
    {
        var canvas = ctx.Canvas;
        var consumed = string.IsNullOrEmpty(label) ? 0 : Caption(ctx, x, y, width, label);
        var height = multiline ? ctx.Scale(64) : ctx.Scale(30);
        var rect = new Rectangle(x, y + consumed, width, height);
        var hovered = canvas.IsHovered(rect);

        canvas.FillRounded(rect, 6, Palette.FieldBackground);
        canvas.StrokeRounded(rect, 6, hovered ? Palette.Accent : Palette.CardBorder);

        var text = string.IsNullOrEmpty(value) ? "（未设置，点击输入）" : value;
        var color = string.IsNullOrEmpty(value) ? Palette.TextMuted : Palette.TextPrimary;
        var inner = Rectangle.Inflate(rect, -ctx.Scale(8), -ctx.Scale(6));

        canvas.Text(text, inner, color, TextStyle.Body, wrap: multiline ? TextWrap.Wrap : TextWrap.None);

        // Recorded so the window can place the real EDIT control exactly here (client coordinates:
        // a scrolled page draws its layout shifted).
        var fieldRect = canvas.ToClient(rect);
        canvas.Hit(rect, () => ctx.Window.BeginEdit(new FieldEdit(fieldRect, value, multiline, commit)), kind: "field");
        return consumed + height + ctx.Scale(8);
    }

    /// <summary>Numeric field with -/+ buttons and an editable centre value.</summary>
    public static int StepField(
        PageContext ctx,
        int x,
        int y,
        int width,
        string label,
        string display,
        Action<int> onDelta,
        Action<string>? onCommit = null,
        bool readOnly = false)
    {
        var canvas = ctx.Canvas;
        var consumed = string.IsNullOrEmpty(label) ? 0 : Caption(ctx, x, y, width, label);
        var height = ctx.Scale(30);
        var rect = new Rectangle(x, y + consumed, width, height);

        canvas.FillRounded(rect, 6, Palette.FieldBackground);
        canvas.StrokeRounded(rect, 6, Palette.CardBorder);

        var buttonSize = ctx.Scale(26);
        var minus = new Rectangle(rect.Left + ctx.Scale(2), rect.Top + ctx.Scale(2), buttonSize, buttonSize);
        var plus = new Rectangle(rect.Right - buttonSize - ctx.Scale(2), rect.Top + ctx.Scale(2), buttonSize, buttonSize);

        canvas.Text(display, new Rectangle(minus.Right, rect.Top, plus.Left - minus.Right, rect.Height),
            readOnly ? Palette.TextSecondary : Palette.TextPrimary, TextStyle.Body, TextAlign.Center);

        if (!readOnly)
        {
            DrawStepButton(ctx, minus, "-", () => onDelta(-1));
            DrawStepButton(ctx, plus, "+", () => onDelta(1));
            if (onCommit is not null)
            {
                var valueRect = new Rectangle(minus.Right, rect.Top, plus.Left - minus.Right, rect.Height);
                var editorRect = canvas.ToClient(valueRect);
                canvas.Hit(valueRect, () => ctx.Window.BeginEdit(new FieldEdit(editorRect, display, false, onCommit)), kind: "field");
            }
        }

        return consumed + height + ctx.Scale(8);
    }

    private static void DrawStepButton(PageContext ctx, Rectangle rect, string glyph, Action onClick)
    {
        var canvas = ctx.Canvas;
        var hovered = canvas.IsHovered(rect);
        canvas.FillRounded(rect, 5, hovered ? Palette.ButtonHover : Palette.SubtleBackground);
        canvas.Text(glyph, rect, Palette.TextPrimary, TextStyle.Button, TextAlign.Center);
        canvas.Hit(rect, onClick, kind: "button");
    }

    /// <summary>Standalone numeric row (used inside hand laid out blocks).</summary>
    public static void Number(PageContext ctx, int x, int y, int width, string label, int value, int min, int max, Action<int> set)
    {
        StepField(ctx, x, y, width, label, value.ToString(), delta =>
        {
            var next = Math.Clamp(value + delta, min, max);
            if (next != value)
            {
                set(next);
            }
        }, text =>
        {
            if (!int.TryParse(text.Trim(), out var parsed))
            {
                throw new FormatException($"请输入 {min} 到 {max} 之间的整数");
            }

            set(Math.Clamp(parsed, min, max));
        });
    }

    /// <summary>Dropdown implemented as a native popup menu (no self-drawn popup window).</summary>
    public static int Dropdown(PageContext ctx, int x, int y, int width, string label, IReadOnlyList<string> items, int selected, Action<int> onSelect)
    {
        var canvas = ctx.Canvas;
        var consumed = string.IsNullOrEmpty(label) ? 0 : Caption(ctx, x, y, width, label);
        var height = ctx.Scale(30);
        var rect = new Rectangle(x, y + consumed, width, height);
        var hovered = canvas.IsHovered(rect);

        canvas.FillRounded(rect, 6, Palette.FieldBackground);
        canvas.StrokeRounded(rect, 6, hovered ? Palette.Accent : Palette.CardBorder);

        var text = selected >= 0 && selected < items.Count ? items[selected] : "（未选择）";
        canvas.Text(text, new Rectangle(rect.Left + ctx.Scale(10), rect.Top, rect.Width - ctx.Scale(30), rect.Height), Palette.TextPrimary, TextStyle.Body);
        canvas.Text("v", new Rectangle(rect.Right - ctx.Scale(18), rect.Top, ctx.Scale(12), rect.Height), Palette.TextSecondary, TextStyle.Caption, TextAlign.Center);

        // The popup is positioned with ClientToScreen, so it needs the client rectangle.
        var popupRect = canvas.ToClient(rect);
        canvas.Hit(rect, () => ctx.Window.ShowPopupMenu(popupRect, items, selected, onSelect), kind: "field");
        return consumed + height + ctx.Scale(8);
    }

    /// <summary>Small grey chip used for saved profile names.</summary>
    public static int Chip(PageContext ctx, int x, int y, string text)
    {
        var canvas = ctx.Canvas;
        var size = canvas.Measure(text, TextStyle.Caption);
        var rect = new Rectangle(x, y, size.Width + ctx.Scale(16), ctx.Scale(20));
        canvas.FillRounded(rect, 5, Palette.SubtleBackground);
        canvas.Text(text, rect, Palette.TextSecondary, TextStyle.Caption, TextAlign.Center);
        return rect.Right;
    }

    /// <summary>Status dot plus text, used for "online / offline" summaries.</summary>
    public static void StatusChip(PageContext ctx, int x, int y, string text, Rgb color)
    {
        var canvas = ctx.Canvas;
        var size = canvas.Measure(text, TextStyle.Body);
        var rect = new Rectangle(x, y, size.Width + ctx.Scale(34), ctx.Scale(26));
        canvas.FillRounded(rect, 13, Palette.SubtleBackground);
        var dot = ctx.Scale(9);
        canvas.FillRounded(new Rectangle(rect.Left + ctx.Scale(10), rect.Top + (rect.Height - dot) / 2, dot, dot), 5, color);
        canvas.Text(text, new Rectangle(rect.Left + ctx.Scale(26), rect.Top, size.Width + ctx.Scale(4), rect.Height), Palette.TextPrimary, TextStyle.Body);
    }
}
