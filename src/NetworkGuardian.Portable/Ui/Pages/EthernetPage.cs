using System.Drawing;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Portable.Ui.Pages;

/// <summary>以太网：物理以太网接口、全部接口明细、DNS/MAC 与 PnP 设备列表（全部只读）。</summary>
internal sealed class EthernetPage : IPage
{
    public string Tag => "ethernet";

    public string Label => "以太网";

    public string Title => "以太网";

    public string Description => "只读展示链路、地址、网关与探测结果；默认不修改任何路由或接口度量。";

    public int Render(PageContext ctx, Rectangle area)
    {
        var canvas = ctx.Canvas;
        var snapshot = ctx.Snapshot;
        var y = area.Top + Widgets.Heading(ctx, area, Title, Description);

        // The "physical Ethernet" lists exclude virtual adapters and the Bluetooth PAN, which also
        // report an 802.3 interface type.
        var ethernet = snapshot.Interfaces
            .Where(i => i.Kind == InterfaceKind.Ethernet && i.IsPhysicalDevice != false)
            .ToList();
        var up = ethernet.Count(i => i.IsUp);
        var online = ethernet.Count(i => i.Probe?.IsStableOnline == true);

        var summary = $"{ethernet.Count} 个物理以太网，{up} 个链路已连接，{online} 个可访问外网";
        var summaryHeight = canvas.MeasureWrappedHeight(summary, area.Width, TextStyle.Body);
        canvas.Text(summary, new Rectangle(area.Left, y, area.Width, summaryHeight), Palette.TextSecondary, TextStyle.Body, wrap: TextWrap.Wrap);
        y += summaryHeight + ctx.Scale(10);

        // ---------- 物理以太网接口 ----------
        var physicalRows = Math.Max(1, ethernet.Count);
        var physicalRect = new Rectangle(area.Left, y, area.Width, ctx.Scale(16 + 24) + (physicalRows * ctx.Scale(40)) + ctx.Scale(12));
        canvas.Card(physicalRect);
        var x = physicalRect.Left + ctx.Scale(16);
        var width = physicalRect.Width - ctx.Scale(32);
        var cy = physicalRect.Top + ctx.Scale(12);
        cy += Widgets.SectionTitle(ctx, x, cy, width, "物理以太网接口");

        if (ethernet.Count == 0)
        {
            Widgets.Mono(ctx, x, cy, width, "未检测到物理以太网接口。", Palette.TextMuted);
            cy += ctx.Scale(40);
        }

        foreach (var iface in ethernet)
        {
            canvas.Text(iface.Name, new Rectangle(x, cy, width, ctx.Scale(20)), Palette.TextPrimary, TextStyle.Body);
            Widgets.Mono(ctx, x, cy + ctx.Scale(19), width, $"链路：{(iface.IsUp ? "Up" : "Down")}　{DescribeLink(iface)}");
            Widgets.Mono(ctx, x, cy + ctx.Scale(34), width, $"探测：{Format.ProbeReport(iface.Probe)}");
            cy += ctx.Scale(40);
        }

        y = physicalRect.Bottom + ctx.Scale(12);

        // ---------- 接口详细信息 ----------
        var all = snapshot.Interfaces;
        var tableRows = Math.Max(1, all.Count);
        var tableRect = new Rectangle(area.Left, y, area.Width, ctx.Scale(16 + 24 + 22) + (tableRows * ctx.Scale(34)) + ctx.Scale(12));
        canvas.Card(tableRect);
        x = tableRect.Left + ctx.Scale(16);
        width = tableRect.Width - ctx.Scale(32);
        cy = tableRect.Top + ctx.Scale(12);
        cy += Widgets.SectionTitle(ctx, x, cy, width, "接口详细信息");

        var columns = new[] { ctx.Scale(220), ctx.Scale(80), ctx.Scale(60), ctx.Scale(150), ctx.Scale(180), ctx.Scale(140), ctx.Scale(120), ctx.Scale(80) };
        var headers = new[] { "名称", "类型", "链路", "探测", "IPv4", "网关", "度量", "默认路由" };

        DrawRow(ctx, x, cy, columns, headers, Palette.TextMuted, TextStyle.Caption);
        cy += ctx.Scale(22);

        if (all.Count == 0)
        {
            Widgets.Mono(ctx, x, cy, width, "尚未采集到接口状态。", Palette.TextMuted);
            cy += ctx.Scale(34);
        }

        foreach (var iface in all)
        {
            var cells = new[]
            {
                iface.Name,
                Format.InterfaceKind(iface.Kind),
                iface.IsUp ? "Up" : "Down",
                DescribeProbeShort(iface.Probe),
                iface.Ipv4Addresses.Count == 0 ? "—" : string.Join(", ", iface.Ipv4Addresses),
                iface.PrimaryGateway ?? "—",
                iface.MetricDescription,
                iface.IsDefaultRoute ? "是" : "否",
            };

            DrawRow(ctx, x, cy, columns, cells, iface.IsUp ? Palette.TextPrimary : Palette.TextSecondary, TextStyle.Body);
            cy += ctx.Scale(34);
        }

        y = tableRect.Bottom + ctx.Scale(12);

        // ---------- DNS 与 MAC ----------
        var detailRows = Math.Max(1, all.Count);
        var detailRect = new Rectangle(area.Left, y, area.Width, ctx.Scale(16 + 24) + (detailRows * ctx.Scale(58)) + ctx.Scale(12));
        canvas.Card(detailRect);
        x = detailRect.Left + ctx.Scale(16);
        width = detailRect.Width - ctx.Scale(32);
        cy = detailRect.Top + ctx.Scale(12);
        cy += Widgets.SectionTitle(ctx, x, cy, width, "DNS 与 MAC");

        if (all.Count == 0)
        {
            Widgets.Mono(ctx, x, cy, width, "—", Palette.TextMuted);
            cy += ctx.Scale(58);
        }

        foreach (var iface in all)
        {
            canvas.Text(iface.Description, new Rectangle(x, cy, width, ctx.Scale(20)), Palette.TextPrimary, TextStyle.Body);
            Widgets.Mono(ctx, x, cy + ctx.Scale(19), width, $"DNS：{(iface.DnsServers.Count == 0 ? "—" : string.Join(", ", iface.DnsServers))}");
            Widgets.Mono(ctx, x, cy + ctx.Scale(34), width, $"MAC：{(string.IsNullOrEmpty(iface.MacAddress) ? "—" : iface.MacAddress)}　速率：{Format.Bytes((long)iface.SpeedBitsPerSecond)}");
            Widgets.Mono(ctx, x, cy + ctx.Scale(49), width, $"标识：{iface.Id}");
            cy += ctx.Scale(58);
        }

        y = detailRect.Bottom + ctx.Scale(12);

        // ---------- 物理以太网设备（PnP） ----------
        var devices = snapshot.EthernetDevices;
        var deviceRows = Math.Max(1, devices.Count);
        var devicesRect = new Rectangle(area.Left, y, area.Width, ctx.Scale(16 + 24) + (deviceRows * ctx.Scale(42)) + ctx.Scale(12));
        canvas.Card(devicesRect);
        x = devicesRect.Left + ctx.Scale(16);
        width = devicesRect.Width - ctx.Scale(32);
        cy = devicesRect.Top + ctx.Scale(12);
        cy += Widgets.SectionTitle(ctx, x, cy, width, $"物理以太网设备（PnP，{devices.Count} 个）");

        if (devices.Count == 0)
        {
            Widgets.Mono(ctx, x, cy, width, "未检测到物理以太网设备。", Palette.TextMuted);
            cy += ctx.Scale(42);
        }

        foreach (var device in devices)
        {
            var record = device.Record;
            var name = record.FriendlyName ?? record.DeviceDescription ?? record.DeviceInstanceId;
            canvas.Text($"{name}｜{record.DeviceInstanceId}", new Rectangle(x, cy, width, ctx.Scale(20)), Palette.TextPrimary, TextStyle.Body);
            Widgets.Mono(ctx, x, cy + ctx.Scale(19), width,
                $"规则：{device.Classification.Rule}　服务：{record.Service ?? "—"}　mediaType：{record.PhysicalMediaType?.ToString() ?? "—"}　MAC：{record.MacAddress ?? "—"}");
            cy += ctx.Scale(42);
        }

        return devicesRect.Bottom - area.Top;
    }

    private static void DrawRow(PageContext ctx, int x, int y, int[] columns, string[] cells, Rgb color, TextStyle style)
    {
        var columnX = x;
        for (var i = 0; i < columns.Length && i < cells.Length; i++)
        {
            ctx.Canvas.Text(cells[i], new Rectangle(columnX, y, columns[i] - ctx.Scale(6), ctx.Scale(20)), color, style);
            columnX += columns[i];
        }
    }

    private static string DescribeLink(InterfaceRuntimeState iface)
    {
        var parts = new List<string>();
        parts.Add(iface.HasUsableIpv4 ? $"IPv4 {iface.PrimaryIpv4Address}" : "无可用 IPv4");
        parts.Add(iface.HasDefaultGateway ? $"网关 {iface.PrimaryGateway}" : "无默认网关");
        if (iface.IsPhysicalDevice == null)
        {
            parts.Add("未关联 PnP 记录");
        }

        return string.Join("　", parts);
    }

    private static string DescribeProbeShort(ConnectivityProbeReport? probe)
    {
        if (probe is null)
        {
            return "未探测";
        }

        if (probe.AttemptCount == 0)
        {
            return "未探测";
        }

        if (probe.IsOnline)
        {
            return $"在线（{probe.SuccessCount}/{probe.AttemptCount}）";
        }

        return probe.CaptivePortalSuspected
            ? "疑似被认证页拦截"
            : $"离线（{probe.SuccessCount}/{probe.AttemptCount}）";
    }
}
