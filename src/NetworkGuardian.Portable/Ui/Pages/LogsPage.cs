using System.Drawing;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Portable.Ui.Pages;

/// <summary>日志：级别/分类筛选、搜索、实时缓冲与日志目录操作。</summary>
internal sealed class LogsPage : IPage
{
    private static readonly string[] LevelLabels = { "全部", "调试及以上", "信息及以上", "警告及以上", "错误" };
    private static readonly GuardianLogLevel[] LevelValues =
    {
        GuardianLogLevel.Trace,
        GuardianLogLevel.Debug,
        GuardianLogLevel.Information,
        GuardianLogLevel.Warning,
        GuardianLogLevel.Error,
    };

    private static readonly string[] ChannelLabels =
    {
        "全部", "WiFi", "Network", "Ethernet", "Device", "Authentication", "Configuration", "Application",
    };

    private readonly ComboBoxState _level = new(2);
    private readonly ComboBoxState _channel = new(0);
    private string _search = string.Empty;
    private bool _autoScroll = true;
    private const int MaxLines = 200;

    public string Tag => "logs";

    public string Label => "日志";

    public string Title => "日志";

    public string Description => "实时缓冲（最近若干条），可切换最小级别、分类与关键字过滤。完整日志按天滚动写入文件。";

    public int Render(PageContext ctx, Rectangle area)
    {
        var canvas = ctx.Canvas;
        var y = area.Top + Widgets.Heading(ctx, area, Title, Description);

        Widgets.Mono(ctx, area.Left, y, area.Width, ctx.Host.LogDirectory);
        y += ctx.Scale(22);

        // ---------- filters ----------
        var filterRect = new Rectangle(area.Left, y, area.Width, ctx.Scale(16 + 30 + 52 + 16));
        canvas.Card(filterRect);
        var x = filterRect.Left + ctx.Scale(16);
        var cy = filterRect.Top + ctx.Scale(14);
        var fieldWidth = ctx.Scale(200);

        Widgets.Dropdown(ctx, x, cy, fieldWidth, "等级", LevelLabels, _level.Index, i => { _level.Index = i; _autoScroll = true; });
        Widgets.Dropdown(ctx, x + fieldWidth + ctx.Scale(12), cy, fieldWidth, "分类", ChannelLabels, _channel.Index, i => _channel.Index = i);

        Widgets.Field(
            ctx,
            x + ((fieldWidth + ctx.Scale(12)) * 2),
            cy,
            ctx.Scale(240),
            "搜索（消息或分类）",
            _search,
            value => _search = value);

        var toggleTop = cy + ctx.Scale(52);
        Widgets.Toggle(ctx, x, toggleTop, fieldWidth, "自动滚动到最新", _autoScroll, () => _autoScroll = !_autoScroll);

        var actionX = x + ((fieldWidth + ctx.Scale(12)) * 2);
        var refresh = Widgets.Button(ctx, actionX, toggleTop + ctx.Scale(2), "刷新", () => ctx.Window.Invalidate());
        Widgets.Button(ctx, refresh.Right + ctx.Scale(8), toggleTop + ctx.Scale(2), "清空缓冲", () =>
        {
            ctx.Host.LogSink.Clear();
            ctx.Window.ShowToast("已清空内存日志缓冲（文件日志不受影响）");
        });
        Widgets.Button(ctx, refresh.Right + ctx.Scale(8) + Widgets.MeasureButtonWidth(ctx, "清空缓冲") + ctx.Scale(8), toggleTop + ctx.Scale(2),
            "打开日志目录", () => ctx.Host.OpenLogFolder());

        y = filterRect.Bottom + ctx.Scale(12);

        // ---------- entries ----------
        var level = LevelValues[_level.Index];
        var channel = ChannelLabels[_channel.Index];
        var records = ctx.Host.LogSink.SnapshotFiltered(level, channel, _search, MaxLines);
        var total = ctx.Host.LogSink.Snapshot().Count;

        var lineWidth = area.Width - ctx.Scale(48);
        var totalHeight = 0;
        var heights = new int[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            var height = Math.Max(ctx.Scale(18), canvas.MeasureWrappedHeight(records[i].ToLine(), lineWidth, TextStyle.Mono));
            heights[i] = height;
            totalHeight += height + ctx.Scale(2);
        }

        var listRect = new Rectangle(area.Left, y, area.Width, Math.Max(ctx.Scale(120), totalHeight + ctx.Scale(28)));
        canvas.Card(listRect);

        var ly = listRect.Top + ctx.Scale(14);
        if (records.Count == 0)
        {
            canvas.Text("没有符合筛选条件的日志。", new Rectangle(listRect.Left + ctx.Scale(16), ly, lineWidth, ctx.Scale(24)), Palette.TextMuted, TextStyle.Body);
        }

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            canvas.Text(
                record.ToLine(),
                new Rectangle(listRect.Left + ctx.Scale(16), ly, lineWidth, heights[i]),
                ColorFor(record.Level),
                TextStyle.Mono,
                wrap: TextWrap.Wrap);
            ly += heights[i] + ctx.Scale(2);
        }

        y = listRect.Bottom + ctx.Scale(12);

        // Sticky auto scroll: follow new entries only while the view is already at the end, so
        // scrolling back to read history is never fought by the refresh timer.
        if (_autoScroll && ctx.Window.IsScrolledToEnd)
        {
            ctx.Window.ScrollToEnd();
        }

        var status = $"显示 {records.Count} 条（内存缓冲共 {total} 条，最多显示 {MaxLines} 条）｜等级 {LevelLabels[_level.Index]}｜分类 {ChannelLabels[_channel.Index]}";
        if (!string.IsNullOrEmpty(_search))
        {
            status += $"｜搜索 “{_search}”";
        }

        canvas.Text(status, new Rectangle(area.Left, y, area.Width, ctx.Scale(20)), Palette.TextMuted, TextStyle.Caption);
        y += ctx.Scale(20);

        return y - area.Top;
    }

    private static Rgb ColorFor(GuardianLogLevel level) => level switch
    {
        GuardianLogLevel.Error or GuardianLogLevel.Critical => Palette.Bad,
        GuardianLogLevel.Warning => Palette.Warn,
        GuardianLogLevel.Debug or GuardianLogLevel.Trace => Palette.TextMuted,
        _ => Palette.TextSecondary,
    };

    /// <summary>Tiny helper so the dropdowns keep their selection across repaints.</summary>
    private sealed class ComboBoxState
    {
        public ComboBoxState(int index) => Index = index;

        public int Index { get; set; }
    }
}
