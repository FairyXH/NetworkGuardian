using System.Drawing;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Location;

namespace NetworkGuardian.Portable.Ui.Pages;

/// <summary>总览：外网状态、以太网、默认路由、无线电、网卡数量、恢复状态机与最近动作。</summary>
internal sealed class DashboardPage : IPage
{
    public string Tag => "dashboard";

    public string Label => "总览";

    public string Title => "总览";

    public string Description => "系统持续保持现有可用连接，仅在连接失效后才重新选网。";

    public int Render(PageContext ctx, Rectangle area)
    {
        var canvas = ctx.Canvas;
        var snapshot = ctx.Snapshot;
        var y = area.Top + Widgets.Heading(ctx, area, Title, Description);

        // Windows 位置权限受限时先提示，并区分三种受限情形。
        if (snapshot.Location.HasProblem)
        {
            var guidance = LocationPermissionService.BuildGuidance(snapshot.Location);
            var textHeight = canvas.MeasureWrappedHeight(guidance, area.Width - ctx.Scale(32), TextStyle.Body);
            var cardHeight = ctx.Scale(12 + 22) + textHeight + ctx.Scale(14);
            var card = new Rectangle(area.Left, y, area.Width, cardHeight);
            canvas.Card(card, Palette.Warn);

            var inner = new Rectangle(card.Left + ctx.Scale(16), card.Top + ctx.Scale(12), card.Width - ctx.Scale(32), ctx.Scale(22));
            canvas.Text("Windows 位置权限限制", inner, Palette.Warn, TextStyle.Section);
            canvas.Text(guidance, new Rectangle(inner.Left, inner.Bottom, inner.Width, textHeight), Palette.TextPrimary, TextStyle.Body, wrap: TextWrap.Wrap);
            y = card.Bottom + ctx.Scale(12);
        }

        var columns = 2;
        var gap = ctx.Scale(12);
        var columnWidth = (area.Width - (gap * (columns - 1))) / columns;
        var height = y - area.Top;

        var internetState = snapshot.GlobalProbe.IsOnline ? "在线" : "离线";
        var internetDetail = Format.ProbeReport(snapshot.GlobalProbe);

        var ethernet = snapshot.Interfaces.Where(i => i.Kind == InterfaceKind.Ethernet).ToList();
        var ethernetUp = ethernet.Count(i => i.IsUp);
        var ethernetState = ethernet.Count == 0 ? "未检测到物理以太网" : ethernetUp == 0 ? "链路断开" : "链路已连接";
        var ethernetDetail = ethernet.Count == 0
            ? "—"
            : string.Join("；", ethernet.Select(i => $"{i.Name}: {(i.IsUp ? "Up" : "Down")} {i.PrimaryIpv4Address ?? "无 IPv4"}"));

        var route = snapshot.DefaultRoutes.FirstOrDefault();
        var routeText = route is null
            ? "—"
            : $"{Format.NextHop(route.NextHop)}（{route.InterfaceAlias ?? "未知接口"}，接口度量 " +
              $"{route.InterfaceMetric?.ToString() ?? "?"} + 路由度量 {route.RouteMetric?.ToString() ?? "?"}）";

        var wifiSummary = snapshot.WifiAdapters.Count == 0
            ? "未发现物理无线网卡"
            : string.Join("；", snapshot.WifiAdapters.Select(a =>
                $"{Format.AdapterName(snapshot, a)}: {(a.IsConnected ? $"{a.CurrentSsid}（{a.SignalQuality}%）" : "未连接")}"));

        var cards = new (string Title, string Value, string Detail, Rgb? Color)[]
        {
            ("Internet 状态", internetState, internetDetail, snapshot.GlobalProbe.IsOnline ? Palette.Good : Palette.Bad),
            ("以太网", ethernetState, ethernetDetail, ethernetUp > 0 ? Palette.Good : Palette.TextSecondary),
            ("默认路由", route is null ? "—" : (route.InterfaceAlias ?? "未知接口"), routeText, null),
            ("Wi-Fi 无线电", Format.RadioState(snapshot.Radio.State), snapshot.Radio.FailureReason ?? snapshot.Radio.Name ?? "—",
                snapshot.Radio.State == RadioState.On ? Palette.Good : Palette.Warn),
            ("物理无线网卡", snapshot.WifiAdapters.Count.ToString(), wifiSummary, null),
            ("恢复状态机", Format.RecoveryState(snapshot.State), Format.Health(snapshot.Health), null),
        };

        for (var row = 0; row < cards.Length; row += columns)
        {
            var cardHeight = 0;
            for (var column = 0; column < columns && row + column < cards.Length; column++)
            {
                var card = cards[row + column];
                cardHeight = Math.Max(cardHeight, MeasureStatCard(ctx, columnWidth, card.Title, card.Value, card.Detail));
            }

            for (var column = 0; column < columns && row + column < cards.Length; column++)
            {
                var card = cards[row + column];
                var rect = new Rectangle(area.Left + (column * (columnWidth + gap)), y, columnWidth, cardHeight);
                DrawStatCard(ctx, rect, card.Title, card.Value, card.Detail, card.Color);
            }

            y += cardHeight + gap;
            height += cardHeight + gap;
        }

        // ---------- 恢复与认证 ----------
        var lastAction = snapshot.LastRecoveryAction ?? "—";
        var lastActionTime = snapshot.LastRecoveryActionUtc is { } actionTime
            ? actionTime.ToLocalTime().ToString("HH:mm:ss")
            : "—";
        var campusAuth = snapshot.LastCampusAuthUtc is { } auth
            ? $"上次执行 {auth.ToLocalTime():HH:mm:ss}，最近一小时 {snapshot.CampusAuthRunCount} 次"
            : "尚未执行";

        var recoveryCardHeight = ctx.Scale(16 + 22 + 22 + 20 + 22 + 22 + 20 + 22 + 22 + 20) + ctx.Scale(24);
        var recoveryRect = new Rectangle(area.Left, y, area.Width, recoveryCardHeight);
        canvas.Card(recoveryRect);
        var rx = recoveryRect.Left + ctx.Scale(16);
        var ry = recoveryRect.Top + ctx.Scale(14);
        var rw = recoveryRect.Width - ctx.Scale(32);

        ry += Widgets.SectionTitle(ctx, rx, ry, rw, "恢复与认证");
        ry += Widgets.KeyValue(ctx, rx, ry, rw, "最近一次恢复动作", lastAction);
        ry += Widgets.KeyValue(ctx, rx, ry, rw, "动作时间", lastActionTime);
        ry += Widgets.KeyValue(ctx, rx, ry, rw, "连续外网失败次数", snapshot.ConsecutiveInternetFailures.ToString());
        Widgets.KeyValue(ctx, rx, ry, rw, "校园网认证", campusAuth);

        y = recoveryRect.Bottom + gap;
        height += recoveryCardHeight + gap;

        // ---------- 本轮计划的动作 ----------
        var actions = snapshot.PendingActions.Count == 0
            ? "无（当前不需要恢复动作）"
            : string.Join(Environment.NewLine, snapshot.PendingActions.Select(a => a.Describe()));
        var actionsHeight = Math.Max(ctx.Scale(20), canvas.MeasureWrappedHeight(actions, area.Width - ctx.Scale(32), TextStyle.Mono));
        var actionsRect = new Rectangle(area.Left, y, area.Width, actionsHeight + ctx.Scale(16 + 22 + 14));
        canvas.Card(actionsRect);

        var ax = actionsRect.Left + ctx.Scale(16);
        var ay = actionsRect.Top + ctx.Scale(14);
        ay += Widgets.SectionTitle(ctx, ax, ay, actionsRect.Width - ctx.Scale(32), "本轮计划的动作");
        Widgets.Mono(ctx, ax, ay, actionsRect.Width - ctx.Scale(32), actions);

        y = actionsRect.Bottom + gap;
        height += actionsRect.Height + gap;

        // ---------- 引擎提示（notes） ----------
        if (snapshot.Notes.Count > 0)
        {
            var notes = string.Join(Environment.NewLine, snapshot.Notes);
            var notesHeight = Math.Max(ctx.Scale(20), canvas.MeasureWrappedHeight(notes, area.Width - ctx.Scale(32), TextStyle.Mono));
            var notesRect = new Rectangle(area.Left, y, area.Width, notesHeight + ctx.Scale(16 + 22 + 14));
            canvas.Card(notesRect);

            var nx = notesRect.Left + ctx.Scale(16);
            var ny = notesRect.Top + ctx.Scale(14);
            ny += Widgets.SectionTitle(ctx, nx, ny, notesRect.Width - ctx.Scale(32), "引擎提示");
            Widgets.Mono(ctx, nx, ny, notesRect.Width - ctx.Scale(32), notes);

            y = notesRect.Bottom + gap;
            height += notesRect.Height + gap;
        }

        _ = height;
        return y - area.Top;
    }

    private static int MeasureStatCard(PageContext ctx, int width, string title, string value, string detail)
    {
        var inner = width - ctx.Scale(32);
        var height = ctx.Scale(14) + ctx.Scale(22) + ctx.Scale(26);
        height += Math.Max(ctx.Scale(18), ctx.Canvas.MeasureWrappedHeight(detail, inner, TextStyle.Body));
        return height + ctx.Scale(14);
    }

    private static void DrawStatCard(PageContext ctx, Rectangle rect, string title, string value, string detail, Rgb? valueColor)
    {
        var canvas = ctx.Canvas;
        canvas.Card(rect);

        var x = rect.Left + ctx.Scale(16);
        var width = rect.Width - ctx.Scale(32);
        var y = rect.Top + ctx.Scale(12);

        y += Widgets.SectionTitle(ctx, x, y, width, title);
        canvas.Text(value, new Rectangle(x, y, width, ctx.Scale(26)), valueColor ?? Palette.TextPrimary, TextStyle.Value);
        y += ctx.Scale(28);

        var detailHeight = Math.Max(ctx.Scale(18), canvas.MeasureWrappedHeight(detail, width, TextStyle.Body));
        canvas.Text(detail, new Rectangle(x, y, width, detailHeight), Palette.TextSecondary, TextStyle.Body, wrap: TextWrap.Wrap);
    }
}
