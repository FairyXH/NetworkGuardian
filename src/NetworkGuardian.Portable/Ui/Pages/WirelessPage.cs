using System.Drawing;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Portable.Ui.Pages;

/// <summary>无线网卡：每块物理网卡一张卡片，含按网卡扫描结果与已保存配置。</summary>
internal sealed class WirelessPage : IPage
{
    private string _status = "尚未扫描";

    public string Tag => "wireless";

    public string Label => "无线网卡";

    public string Title => "无线网卡";

    public string Description => "仅管理物理无线网卡；已正常连接的网卡不会被主动切换，扫描结果按网卡分别保存。";

    public int Render(PageContext ctx, Rectangle area)
    {
        var canvas = ctx.Canvas;
        var snapshot = ctx.Snapshot;
        var y = area.Top + Widgets.Heading(ctx, area, Title, Description);

        // ---------- toolbar ----------
        var toolbarHeight = ctx.Scale(32);
        var scanAll = Widgets.Button(ctx, area.Left, y, "重新扫描全部网卡", () =>
        {
            _status = "正在扫描全部无线网卡…";
            ctx.Window.RunBackground(async () =>
            {
                await ctx.Host.RescanAllAsync(CancellationToken.None).ConfigureAwait(false);
                _status = "全部无线网卡扫描完成";
            });
        });

        Widgets.Button(ctx, scanAll.Right + ctx.Scale(8), y, "刷新设备", () =>
        {
            ctx.Host.RequestImmediateCycle();
            _status = "已请求刷新";
        });

        canvas.Text(
            _status,
            new Rectangle(scanAll.Right + ctx.Scale(160), y, Math.Max(ctx.Scale(80), area.Right - scanAll.Right - ctx.Scale(170)), toolbarHeight),
            Palette.TextMuted,
            TextStyle.Caption);

        y += toolbarHeight + ctx.Scale(14);

        if (snapshot.WifiAdapters.Count == 0)
        {
            var emptyRect = new Rectangle(area.Left, y, area.Width, ctx.Scale(70));
            canvas.Card(emptyRect);
            canvas.Text(
                "未检测到物理无线网卡（虚拟适配器会被过滤，详见下方设备列表）。",
                new Rectangle(emptyRect.Left + ctx.Scale(16), emptyRect.Top, emptyRect.Width - ctx.Scale(32), emptyRect.Height),
                Palette.TextSecondary,
                TextStyle.Body);
            y = emptyRect.Bottom + ctx.Scale(12);
        }

        foreach (var adapter in snapshot.WifiAdapters)
        {
            y = RenderAdapter(ctx, area, adapter, snapshot, y);
        }

        // ---------- detected devices ----------
        var devices = ctx.Host.Devices;
        var deviceHeight = ctx.Scale(16 + 24) + (devices.Count * ctx.Scale(46)) + ctx.Scale(16);
        var devicesRect = new Rectangle(area.Left, y, area.Width, deviceHeight);
        canvas.Card(devicesRect);

        var dx = devicesRect.Left + ctx.Scale(16);
        var dy = devicesRect.Top + ctx.Scale(12);
        var dw = devicesRect.Width - ctx.Scale(32);
        dy += Widgets.SectionTitle(ctx, dx, dy, dw, $"检测到的无线设备（含被过滤的虚拟网卡，共 {devices.Count} 个）");

        if (devices.Count == 0)
        {
            Widgets.Mono(ctx, dx, dy, dw, "尚未枚举到网络类设备。");
        }

        foreach (var device in devices)
        {
            var record = device.Record;
            var name = record.FriendlyName ?? record.DeviceDescription ?? record.DeviceInstanceId;
            var statusText = record.IsPresent
                ? device.IsEnabled ? "已启用" : $"未启用（problem {record.ProblemCode}）"
                : "当前不存在";

            canvas.Text(
                $"{name}｜{statusText}｜{(device.Classification.IsPhysical ? "物理设备" : "已过滤")}｜{record.DeviceInstanceId}",
                new Rectangle(dx, dy, dw - ctx.Scale(90), ctx.Scale(20)),
                device.Classification.IsPhysical ? Palette.TextPrimary : Palette.TextSecondary,
                TextStyle.Body);

            Widgets.Mono(
                ctx,
                dx,
                dy + ctx.Scale(20),
                dw - ctx.Scale(90),
                $"规则：{device.Classification.Rule}　服务：{record.Service ?? "—"}　枚举器：{record.EnumeratorName ?? "—"}　mediaType：{record.PhysicalMediaType?.ToString() ?? "—"}");

            Widgets.Mono(ctx, dx, dy + ctx.Scale(36), dw - ctx.Scale(90), $"原因：{device.Classification.Reason}");

            var deviceId = record.DeviceInstanceId;
            var enableLabel = record.IsPresent && !device.IsEnabled ? "启用" : "查询状态";
            var buttonWidth = Widgets.MeasureButtonWidth(ctx, enableLabel);
            var enableRect = new Rectangle(devicesRect.Right - ctx.Scale(16) - buttonWidth, dy + ctx.Scale(4), buttonWidth, ctx.Scale(30));

            Widgets.ButtonAt(
                ctx,
                enableRect,
                enableLabel,
                () =>
                {
                    _status = $"正在请求启用 {name}（可能出现 UAC 提示）…";
                    ctx.Window.RunBackground(async () =>
                    {
                        var result = await ctx.Host.EnableDeviceAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
                        _status = result.Success
                            ? $"启用请求完成：{result.Outcome}"
                            : $"启用失败：{result.Outcome} {result.Detail} {result.Win32Message}";
                    });
                },
                enabled: device.Classification.IsPhysical && record.IsPresent);

            dy += ctx.Scale(46);
        }

        return devicesRect.Bottom - area.Top;
    }

    private int RenderAdapter(PageContext ctx, Rectangle area, WifiAdapterRuntimeState adapter, GuardianSnapshot snapshot, int y)
    {
        var canvas = ctx.Canvas;
        var networkCount = adapter.LastScan?.Networks.Count ?? 0;
        var networkRows = networkCount > 0 ? networkCount : 1;
        var profilesRows = Math.Max(1, (adapter.SavedProfiles.Count + 5) / 6);

        var cardHeight =
            ctx.Scale(16 + 24 + 8) +          // padding + title
            (ctx.Scale(20) * 7) +             // seven detail lines
            ctx.Scale(22) +                   // "已保存的配置" caption
            (profilesRows * ctx.Scale(24)) +
            ctx.Scale(22) +                   // scanned networks caption
            (networkRows * ctx.Scale(26)) +
            ctx.Scale(16);

        var card = new Rectangle(area.Left, y, area.Width, cardHeight);
        canvas.Card(card);

        var x = card.Left + ctx.Scale(16);
        var width = card.Width - ctx.Scale(32);
        var cy = card.Top + ctx.Scale(14);
        var buttonsLeft = card.Right - ctx.Scale(16);

        canvas.Text(adapter.Description, new Rectangle(x, cy, width - ctx.Scale(150), ctx.Scale(24)), Palette.TextPrimary, TextStyle.Section);
        cy += ctx.Scale(28);

        var connection = adapter.Connection;
        var bssid = string.IsNullOrEmpty(connection?.Bssid) ? "—" : connection!.Bssid;
        var signal = adapter.IsConnected ? $"{adapter.SignalQuality}% / {connection?.Rssi ?? 0} dBm" : "—";
        var band = connection is null || connection.Band == NetworkBand.Unknown
            ? "—"
            : $"{Format.Band(connection.Band)} ch{connection.Channel}（{connection.FrequencyKhz / 1000} MHz）";
        var iface = snapshot.Interfaces.FirstOrDefault(i => i.WlanInterfaceGuid == adapter.InterfaceGuid);
        var ip = iface?.PrimaryIpv4Address ?? "—";
        var mac = string.IsNullOrEmpty(adapter.MacAddress) ? "—" : adapter.MacAddress!;
        var lastScan = adapter.LastScan?.CompletedAtUtc is { } completed
            ? $"{completed.ToLocalTime():HH:mm:ss}（{adapter.LastScan.Networks.Count} 个网络）"
            : "尚未扫描";
        var lastAttempt = adapter.LastConnectAttemptUtc is { } attempt
            ? $"{attempt.ToLocalTime():HH:mm:ss} → {adapter.LastAttemptedProfile}"
            : "—";
        var lastFailure = string.IsNullOrEmpty(adapter.LastFailure) ? "—" : adapter.LastFailure!;
        var profileText = adapter.ProfileListKnown ? $"{adapter.SavedProfiles.Count} 个已保存配置" : "未知";

        var lines = new[]
        {
            $"状态：{(adapter.IsConnected ? "已连接" : "未连接")}　SSID：{adapter.CurrentSsid ?? "—"}　信号：{signal}",
            $"BSSID：{bssid}　频段：{band}",
            $"IP：{ip}　MAC：{mac}",
            $"InterfaceGUID：{adapter.InterfaceGuid:D}",
            $"DeviceInstanceId：{adapter.DeviceInstanceId ?? "—"}",
            $"配置数：{profileText}　上次扫描：{lastScan}",
            $"上次连接尝试：{lastAttempt}　最近失败：{lastFailure}",
        };

        foreach (var line in lines)
        {
            canvas.Text(line, new Rectangle(x, cy, width - ctx.Scale(150), ctx.Scale(20)), Palette.TextSecondary, TextStyle.Mono);
            cy += ctx.Scale(20);
        }

        // ---------- adapter actions ----------
        var guid = adapter.InterfaceGuid;
        var disconnectWidth = Widgets.MeasureButtonWidth(ctx, "断开");
        var scanWidth = Widgets.MeasureButtonWidth(ctx, "扫描此网卡");

        var disconnectRect = new Rectangle(buttonsLeft - disconnectWidth, card.Top + ctx.Scale(14), disconnectWidth, ctx.Scale(30));
        Widgets.ButtonAt(ctx, disconnectRect, "断开", () =>
        {
            _status = $"正在断开 {adapter.Description}…";
            ctx.Window.RunBackground(async () =>
            {
                var result = await ctx.Host.DisconnectAsync(guid, CancellationToken.None).ConfigureAwait(false);
                _status = result.Success ? "已断开" : $"断开失败：{result.Failure}";
            });
        }, enabled: adapter.IsConnected);

        var scanRect = new Rectangle(buttonsLeft - scanWidth, disconnectRect.Bottom + ctx.Scale(6), scanWidth, ctx.Scale(30));
        Widgets.ButtonAt(ctx, scanRect, "扫描此网卡", () =>
        {
            _status = $"正在扫描 {adapter.Description}…";
            ctx.Window.RunBackground(async () =>
            {
                var result = await ctx.Host.ScanAdapterAsync(guid, CancellationToken.None).ConfigureAwait(false);
                _status = result.Failed ? $"扫描失败：{result.FailureReason}" : $"扫描完成：{result.Networks.Count} 个网络";
            });
        });

        // ---------- saved profiles ----------
        canvas.Text("已保存的配置", new Rectangle(x, cy, width, ctx.Scale(20)), Palette.TextMuted, TextStyle.Caption);
        cy += ctx.Scale(22);

        if (adapter.SavedProfiles.Count == 0)
        {
            canvas.Text(adapter.ProfileListKnown ? "（无）" : "（未知）", new Rectangle(x, cy, width, ctx.Scale(20)), Palette.TextMuted, TextStyle.Caption);
            cy += ctx.Scale(24);
        }
        else
        {
            var chipX = x;
            foreach (var profile in adapter.SavedProfiles)
            {
                var chipRight = Widgets.Chip(ctx, chipX, cy, profile);
                chipX = chipRight + ctx.Scale(6);
                if (chipX > card.Right - ctx.Scale(90))
                {
                    chipX = x;
                    cy += ctx.Scale(24);
                }
            }

            cy += ctx.Scale(24);
        }

        // ---------- scanned networks ----------
        canvas.Text("扫描到的网络（仅列出，可手动连接已保存的）", new Rectangle(x, cy, width, ctx.Scale(20)), Palette.TextMuted, TextStyle.Caption);
        cy += ctx.Scale(22);

        var networks = adapter.LastScan?.Networks ?? Array.Empty<ScannedNetwork>();
        if (networks.Count == 0)
        {
            canvas.Text(
                adapter.IsScanInProgress ? "正在扫描…" : "尚无扫描结果，点击右侧“扫描此网卡”。",
                new Rectangle(x, cy, width, ctx.Scale(24)),
                Palette.TextMuted,
                TextStyle.Body);
            cy += ctx.Scale(26);
        }
        else
        {
            var columns = new[] { ctx.Scale(240), ctx.Scale(70), ctx.Scale(90), ctx.Scale(150), ctx.Scale(180) };
            var headerX = x;
            var headers = new[] { "SSID", "信号", "RSSI", "频段", "配置 / 状态" };
            for (var i = 0; i < headers.Length; i++)
            {
                canvas.Text(headers[i], new Rectangle(headerX, cy, columns[i], ctx.Scale(18)), Palette.TextMuted, TextStyle.Caption);
                headerX += columns[i];
            }

            cy += ctx.Scale(20);

            foreach (var network in networks)
            {
                var columnX = x;
                var cells = new[]
                {
                    network.Ssid,
                    $"{network.SignalQuality}%",
                    $"{network.Rssi} dBm",
                    network.Band == NetworkBand.Unknown ? "—" : $"{Format.Band(network.Band)} ch{network.Channel}",
                    network.HasProfile ? network.ProfileName ?? network.Ssid : "无配置（忽略）",
                };

                for (var i = 0; i < cells.Length; i++)
                {
                    canvas.Text(cells[i], new Rectangle(columnX, cy, columns[i] - ctx.Scale(6), ctx.Scale(20)),
                        network.IsCurrentConnection ? Palette.Good : Palette.TextPrimary, TextStyle.Body);
                    columnX += columns[i];
                }

                if (network.HasProfile && network.Connectable && !network.IsCurrentConnection)
                {
                    var connectWidth = Widgets.MeasureButtonWidth(ctx, "连接");
                    var connectRect = new Rectangle(card.Right - ctx.Scale(16) - connectWidth, cy - ctx.Scale(4), connectWidth, ctx.Scale(26));
                    var profileName = network.ProfileName ?? network.Ssid;
                    var ssid = network.Ssid;
                    Widgets.ButtonAt(ctx, connectRect, "连接", () =>
                    {
                        _status = $"正在连接 {ssid}…";
                        ctx.Window.RunBackground(async () =>
                        {
                            var result = await ctx.Host.ConnectAsync(guid, profileName, CancellationToken.None).ConfigureAwait(false);
                            _status = result.Success ? $"已连接 {ssid}" : $"连接失败：{result.Failure}";
                        });
                    });
                }

                cy += ctx.Scale(26);
            }
        }

        return card.Bottom + ctx.Scale(12);
    }
}
