using System.Drawing;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Location;

namespace NetworkGuardian.Portable.Ui.Pages;

/// <summary>总览：外网状态、以太网、默认路由、无线电、网卡数量、恢复状态机与最近动作。</summary>
internal sealed class DashboardPage : IPage
{
    private const string ProxyCompatibilityGuidance =
        "为保证每张网卡的外网探测只走该网卡，请按以下方式放行：\n" +
        "• Proxifier：添加最高优先级规则，应用程序 NetworkGuardian.exe，目标/端口任意，动作选择 Direct；不要使用“Proxifier”右键方式启动本程序。\n" +
        "• YogaDNS：将探测域名设为 Bypass，或让 DNS 规则绑定对应网络接口，并启用“接口断开时忽略规则”。\n" +
        "• 其他代理、VPN、加速器或流量聚合软件：把 NetworkGuardian.exe 加入直连/绕过名单，禁止透明代理或强制接管。否则探测结果可能代表代理出口，而不是对应网卡。";

    public string Tag => "dashboard";

    public string Label => "总览";

    public string Title => "总览";

    public string Description => "系统持续保持现有可用连接，仅在连接失效后才重新选网。";

    public int Render(PageContext ctx, Rectangle area)
    {
        var canvas = ctx.Canvas;
        var snapshot = ctx.Snapshot;
        var y = area.Top + Widgets.Heading(ctx, area, Title, Description);

        var proxyTextWidth = area.Width - ctx.Scale(32);
        var proxyTextHeight = canvas.MeasureWrappedHeight(ProxyCompatibilityGuidance, proxyTextWidth, TextStyle.Body);
        var proxyCardHeight = ctx.Scale(12 + 24) + proxyTextHeight + ctx.Scale(14);
        var proxyCard = new Rectangle(area.Left, y, area.Width, proxyCardHeight);
        canvas.Card(proxyCard, Palette.Warn);

        var proxyInner = new Rectangle(
            proxyCard.Left + ctx.Scale(16),
            proxyCard.Top + ctx.Scale(12),
            proxyTextWidth,
            ctx.Scale(24));
        canvas.Text("重要：代理与 DNS 软件必须设置直连", proxyInner, Palette.Warn, TextStyle.Section);
        canvas.Text(
            ProxyCompatibilityGuidance,
            new Rectangle(proxyInner.Left, proxyInner.Bottom, proxyInner.Width, proxyTextHeight),
            Palette.TextPrimary,
            TextStyle.Body,
            wrap: TextWrap.Wrap);
        y = proxyCard.Bottom + ctx.Scale(12);

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

        var internetState = snapshot.GlobalProbe.IsStableOnline ? "在线" : "离线";
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
        var outlet = FindOutletInterface(snapshot, route);
        var outletName = outlet?.Name ?? route?.InterfaceAlias ?? "未确定";
        var outletOnline = outlet?.Probe?.IsStableOnline ?? snapshot.GlobalProbe.IsStableOnline;
        var expectedOutlet = snapshot.Interfaces.FirstOrDefault(iface =>
            string.Equals(iface.Id, snapshot.ExpectedOutletInterfaceId, StringComparison.OrdinalIgnoreCase));
        var policyState = snapshot.OutletMatchesPolicy switch
        {
            true => "出口符合策略",
            false => $"出口偏离策略，正在纠正（预期 {expectedOutlet?.Name ?? "未知接口"}）",
            _ => "正在核对出口策略",
        };
        var routeAge = snapshot.RouteObservedAtUtc is { } observed
            ? $"｜路由刷新 {observed.ToLocalTime():HH:mm:ss}"
            : string.Empty;
        var outletDetail = outlet is null
            ? $"{routeText}｜{policyState}{routeAge}"
            : $"{outlet.Description}｜{FormatInterfaceKind(outlet.Kind)}｜IPv4 {outlet.PrimaryIpv4Address ?? "无"}｜" +
              $"下一跳 {Format.NextHop(route?.NextHop)}｜有效跃点 {route?.EffectiveMetric?.ToString() ?? "?"}｜" +
              $"{policyState}{routeAge}";

        var wifiSummary = snapshot.WifiAdapters.Count == 0
            ? "未发现物理无线网卡"
            : string.Join("；", snapshot.WifiAdapters.Select(a =>
                $"{Format.AdapterName(snapshot, a)}: {(a.IsConnected ? $"{a.CurrentSsid}（{a.SignalQuality}%）" : "未连接")}"));
        var npcap = ctx.Host.NpcapStatus;

        var cards = new (string Title, string Value, string Detail, Rgb? Color)[]
        {
            ("Internet 状态", internetState, internetDetail, snapshot.GlobalProbe.IsStableOnline ? Palette.Good : Palette.Bad),
            ("以太网", ethernetState, ethernetDetail, ethernetUp > 0 ? Palette.Good : Palette.TextSecondary),
            ("当前外网出口", outletOnline ? outletName : $"{outletName}（外网不可用）", outletDetail,
                snapshot.OutletMatchesPolicy == false ? Palette.Warn : outletOnline ? Palette.Good : Palette.Bad),
            ("Wi-Fi 无线电", Format.RadioState(snapshot.Radio.State), snapshot.Radio.FailureReason ?? snapshot.Radio.Name ?? "—",
                snapshot.Radio.State == RadioState.On ? Palette.Good : Palette.Warn),
            ("物理无线网卡", snapshot.WifiAdapters.Count.ToString(), wifiSummary, null),
            ("Npcap 出口验证", npcap.IsAvailable ? "可用" : "不可用", npcap.Detail,
                npcap.IsAvailable ? Palette.Good : Palette.Warn),
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

        // ---------- Windows 系统真实跃点 ----------
        // Values below come from GetAdaptersAddresses/GetIpForwardTable2 in the one-second route
        // snapshot. They are deliberately not derived from the policy planner.
        var systemInterfaces = snapshot.Interfaces
            .Where(iface => iface.Kind != InterfaceKind.Loopback)
            .OrderByDescending(iface => iface.IsUp)
            .ThenBy(iface => iface.Kind)
            .ThenBy(iface => iface.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var metricLines = systemInterfaces.Count == 0
            ? "系统未返回网络接口"
            : string.Join(Environment.NewLine, systemInterfaces.Select(iface =>
                FormatSystemMetricLine(snapshot, iface)));
        var metricTextHeight = Math.Max(
            ctx.Scale(20),
            canvas.MeasureWrappedHeight(metricLines, area.Width - ctx.Scale(32), TextStyle.Mono));
        var metricCardHeight = ctx.Scale(14 + 22 + 20 + 8 + 14) + metricTextHeight;
        var metricCard = new Rectangle(area.Left, y, area.Width, metricCardHeight);
        canvas.Card(metricCard);
        var mx = metricCard.Left + ctx.Scale(16);
        var my = metricCard.Top + ctx.Scale(14);
        var mw = metricCard.Width - ctx.Scale(32);
        my += Widgets.SectionTitle(ctx, mx, my, mw, "系统网卡跃点详情");
        var observedText = snapshot.RouteObservedAtUtc is { } metricObserved
            ? $"Windows 实时读取｜{systemInterfaces.Count} 个接口｜刷新 {metricObserved.ToLocalTime():HH:mm:ss}"
            : $"Windows 实时读取｜{systemInterfaces.Count} 个接口";
        canvas.Text(observedText, new Rectangle(mx, my, mw, ctx.Scale(20)), Palette.TextMuted, TextStyle.Caption);
        my += ctx.Scale(28);
        Widgets.Mono(ctx, mx, my, mw, metricLines);

        y = metricCard.Bottom + gap;
        height += metricCardHeight + gap;

        // ---------- 每个物理适配器的外网状态 ----------
        var physicalInterfaces = snapshot.Interfaces
            .Where(i => i.IsPhysicalDevice != false && i.Kind is InterfaceKind.Ethernet or InterfaceKind.Wifi)
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (physicalInterfaces.Count > 0)
        {
            canvas.Text("适配器外网状态", new Rectangle(area.Left, y, area.Width, ctx.Scale(24)), Palette.TextPrimary, TextStyle.Section);
            y += ctx.Scale(30);

            for (var row = 0; row < physicalInterfaces.Count; row += columns)
            {
                var rowItems = physicalInterfaces.Skip(row).Take(columns).ToList();
                var rowHeight = rowItems.Max(iface => MeasureStatCard(ctx, columnWidth,
                    iface.Kind == InterfaceKind.Ethernet ? "以太网适配器" : "无线适配器",
                    InterfaceInternetState(iface), InterfaceInternetDetail(snapshot, iface)));

                for (var column = 0; column < rowItems.Count; column++)
                {
                    var iface = rowItems[column];
                    var online = iface.Probe?.IsStableOnline == true;
                    DrawStatCard(ctx,
                        new Rectangle(area.Left + (column * (columnWidth + gap)), y, columnWidth, rowHeight),
                        iface.Name,
                        InterfaceInternetState(iface),
                        InterfaceInternetDetail(snapshot, iface),
                        online ? Palette.Good : iface.IsUp ? Palette.Warn : Palette.Bad);
                }

                y += rowHeight + gap;
                height += rowHeight + gap;
            }
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

    private static string InterfaceInternetState(InterfaceRuntimeState iface) => iface.Probe switch
    {
        { IsStableOnline: true } => "外网正常",
        { AttemptCount: > 0 } => "外网不可用",
        _ when !iface.IsUp => "链路断开",
        _ => "等待探测",
    };

    private static string InterfaceInternetDetail(GuardianSnapshot snapshot, InterfaceRuntimeState iface)
    {
        var wifi = iface.WlanInterfaceGuid is { } guid
            ? snapshot.WifiAdapters.FirstOrDefault(adapter => adapter.InterfaceGuid == guid)
            : null;
        var connection = wifi?.IsConnected == true ? $"｜SSID {wifi.CurrentSsid}" : string.Empty;
        var probe = iface.Probe is null ? "尚无按接口探测结果" : Format.ProbeReport(iface.Probe);
        var probeTime = iface.Probe is null
            ? "上次探测 —"
            : $"上次探测 {iface.Probe.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        return $"{iface.Description}{connection}｜IPv4 {iface.PrimaryIpv4Address ?? "无"}｜{probe}｜{probeTime}";
    }

    private static InterfaceRuntimeState? FindOutletInterface(
        GuardianSnapshot snapshot,
        DefaultRouteInfo? route)
    {
        if (route is null)
        {
            return null;
        }

        if (route.InterfaceLuid is { } luid)
        {
            var byLuid = snapshot.Interfaces.FirstOrDefault(iface =>
                string.Equals(iface.Id, $"luid:{luid}", StringComparison.OrdinalIgnoreCase));
            if (byLuid is not null)
            {
                return byLuid;
            }
        }

        return snapshot.Interfaces.FirstOrDefault(iface =>
            string.Equals(iface.Name, route.InterfaceAlias, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatSystemMetricLine(GuardianSnapshot snapshot, InterfaceRuntimeState iface)
    {
        var routes = snapshot.DefaultRoutes.Where(route =>
                route.InterfaceIndex == iface.InterfaceIndex ||
                route.InterfaceLuid is { } luid &&
                string.Equals(iface.Id, $"luid:{luid}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var routeText = routes.Count == 0
            ? "默认路由 —｜有效 —"
            : $"默认路由 {string.Join(",", routes.Select(route => route.RouteMetric?.ToString() ?? "?"))}" +
              $"｜有效 {string.Join(",", routes.Select(route => route.EffectiveMetric?.ToString() ?? "?"))}";
        var state = iface.IsUp ? "Up" : "Down";
        return $"{iface.Name}｜{FormatInterfaceKind(iface.Kind)}｜{state}｜接口 {iface.InterfaceMetric?.ToString() ?? "?"}｜{routeText}";
    }

    private static string FormatInterfaceKind(InterfaceKind kind) => kind switch
    {
        InterfaceKind.Ethernet => "以太网",
        InterfaceKind.Wifi => "无线网卡",
        _ => kind.ToString(),
    };
}
