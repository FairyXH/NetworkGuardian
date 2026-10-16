using System.Drawing;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;

namespace NetworkGuardian.Portable.Ui.Pages;

internal sealed class DevicesPage : IPage
{
    private string _status = "保持禁用规则会保存到本机配置，并在软件运行期间持续执行。";
    private bool _showVirtual;
    public string Tag => "devices";
    public string Label => "网卡管理";
    public string Title => "网卡管理";
    public string Description => "管理物理无线与有线网卡的启用状态。保持禁用优先于自动启用、修复和选网，取消规则后可恢复启用。";

    public int Render(PageContext ctx, Rectangle area)
    {
        var canvas = ctx.Canvas;
        var y = area.Top + Widgets.Heading(ctx, area, Title, Description);
        var refresh = Widgets.Button(ctx, area.Left, y, "刷新设备", () => ctx.Host.RequestImmediateCycle(), confirm: false);
        Widgets.Toggle(ctx, refresh.Right + ctx.Scale(20), y, ctx.Scale(260), "显示虚拟与过滤设备",
            _showVirtual, () => _showVirtual = !_showVirtual, confirm: false);
        y += ctx.Scale(40);
        y += Widgets.Caption(ctx, area.Left, y, area.Width, _status) + ctx.Scale(8);

        // ---------- detected devices ----------
        var devices = ctx.Host.Devices.Where(d => _showVirtual || d.Classification.IsPhysical).ToArray();
        var deviceHeight = ctx.Scale(16 + 24) + (devices.Length * ctx.Scale(78)) + ctx.Scale(16);
        var devicesRect = new Rectangle(area.Left, y, area.Width, deviceHeight);
        canvas.Card(devicesRect);

        var dx = devicesRect.Left + ctx.Scale(16);
        var dy = devicesRect.Top + ctx.Scale(12);
        var dw = devicesRect.Width - ctx.Scale(32);
        dy += Widgets.SectionTitle(ctx, dx, dy, dw,
            $"{(_showVirtual ? "全部网络设备" : "物理网卡")}（{devices.Length} 个）");

        if (devices.Length == 0)
        {
            Widgets.Mono(ctx, dx, dy, dw, "尚未枚举到网络类设备。");
        }

        foreach (var device in devices)
        {
            var record = device.Record;
            var name = record.FriendlyName ?? record.DeviceDescription ?? record.DeviceInstanceId;
            var keptDisabled = DeviceDisablePolicy.IsDisabled(ctx.Config, record.DeviceInstanceId);
            var statusText = record.IsPresent
                ? device.IsEnabled ? "已启用" : $"未启用（problem {record.ProblemCode}）"
                : "当前不存在";
            if (keptDisabled) statusText += " · 保持禁用";

            canvas.Text(
                $"{name}｜{statusText}｜{(device.Classification.IsPhysical ? "物理设备" : "已过滤")}｜{record.DeviceInstanceId}",
                new Rectangle(dx, dy, dw - ctx.Scale(190), ctx.Scale(20)),
                device.Classification.IsPhysical ? Palette.TextPrimary : Palette.TextSecondary,
                TextStyle.Body);

            Widgets.Mono(
                ctx,
                dx,
                dy + ctx.Scale(20),
                dw - ctx.Scale(190),
                $"规则：{device.Classification.Rule}　服务：{record.Service ?? "—"}　枚举器：{record.EnumeratorName ?? "—"}　mediaType：{record.PhysicalMediaType?.ToString() ?? "—"}");

            Widgets.Mono(ctx, dx, dy + ctx.Scale(36), dw - ctx.Scale(190), $"原因：{device.Classification.Reason}");

            var deviceId = record.DeviceInstanceId;
            var enableOperation = $"device-state-{deviceId}";
            var enabling = ctx.Window.IsOperationRunning(enableOperation);
            var canManage = device.Classification.IsPhysical && device.Classification.Category is
                DeviceCategory.PhysicalWifi or DeviceCategory.PhysicalEthernet;
            var policyLabel = keptDisabled ? "取消禁用并启用" : "保持禁用";
            var policyWidth = Widgets.MeasureButtonWidth(ctx, policyLabel);
            Widgets.ButtonAt(ctx, new Rectangle(devicesRect.Right - ctx.Scale(16) - policyWidth, dy, policyWidth, ctx.Scale(30)),
                enabling ? "处理中…" : policyLabel, () =>
                {
                    if (!ctx.Window.Confirm(keptDisabled
                        ? $"确定取消“{name}”的保持禁用规则并启用该网卡吗？"
                        : $"确定保持禁用“{name}”吗？该网卡的现有连接会断开；规则将保存并优先于自动启用，重启软件后仍有效。")) return;
                    ctx.Window.RunBackground(enableOperation, $"正在{policyLabel}：{name}", async () =>
                    {
                        await ctx.Window.App.UpdateConfigAsync(config =>
                        {
                            config.DisabledDeviceInstanceIds.RemoveAll(id => string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase));
                            if (!keptDisabled) config.DisabledDeviceInstanceIds.Add(deviceId);
                        }).ConfigureAwait(false);
                        if (!record.IsPresent)
                        {
                            _status = "已取消禁用规则；设备当前离线";
                            ctx.Host.RequestImmediateCycle();
                            return;
                        }
                        var result = keptDisabled
                            ? await ctx.Host.EnableDeviceAsync(deviceId, CancellationToken.None).ConfigureAwait(false)
                            : await ctx.Host.DisableDeviceAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
                        _status = result.Outcome == DeviceOperationOutcome.RebootRequired ? $"{name}：规则已保存，Windows 要求重启后完成设备操作"
                            : result.Success ? $"{name}：{policyLabel}已完成"
                            : $"规则已保存，但设备操作失败：{result.Outcome} {result.Detail} {result.Win32Message}";
                        ctx.Host.RequestImmediateCycle();
                    });
                }, enabled: !enabling && canManage && (record.IsPresent || keptDisabled), confirm: false);

            var enableLabel = record.IsPresent && !device.IsEnabled ? "启用" : "刷新状态";
            var buttonWidth = Widgets.MeasureButtonWidth(ctx, enableLabel);
            var enableRect = new Rectangle(devicesRect.Right - ctx.Scale(16) - buttonWidth, dy + ctx.Scale(36), buttonWidth, ctx.Scale(30));

            Widgets.ButtonAt(
                ctx,
                enableRect,
                enabling ? "处理中…" : enableLabel,
                () =>
                {
                    if (device.IsEnabled)
                    {
                        ctx.Host.RequestImmediateCycle();
                        _status = "已请求刷新设备状态";
                        return;
                    }
                    _status = $"正在请求启用 {name}（可能出现 UAC 提示）…";
                    ctx.Window.RunBackground(enableOperation, $"正在处理网卡 {name}", async () =>
                    {
                        var result = await ctx.Host.EnableDeviceAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
                        _status = result.Success
                            ? $"启用请求完成：{result.Outcome}"
                            : $"启用失败：{result.Outcome} {result.Detail} {result.Win32Message}";
                    });
                },
                enabled: !enabling && canManage && record.IsPresent && !keptDisabled, confirm: !device.IsEnabled);

            dy += ctx.Scale(78);
        }

        var bottom = devicesRect.Bottom;
        var offlineRules = ctx.Config.DisabledDeviceInstanceIds.Where(id =>
            ctx.Host.Devices.All(d => !string.Equals(d.Record.DeviceInstanceId, id, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var deviceId in offlineRules)
        {
            var rect = new Rectangle(area.Left, bottom + ctx.Scale(10), area.Width, ctx.Scale(76));
            canvas.Card(rect);
            Widgets.Caption(ctx, rect.Left + ctx.Scale(16), rect.Top + ctx.Scale(10), rect.Width - ctx.Scale(200), "保持禁用 · 设备当前离线");
            Widgets.Mono(ctx, rect.Left + ctx.Scale(16), rect.Top + ctx.Scale(32), rect.Width - ctx.Scale(200), deviceId);
            var operation = $"device-state-{deviceId}";
            Widgets.ButtonAt(ctx, new Rectangle(rect.Right - ctx.Scale(170), rect.Top + ctx.Scale(20), ctx.Scale(154), ctx.Scale(32)),
                "取消离线禁用", () =>
                {
                    if (!ctx.Window.Confirm($"确定取消该离线设备的禁用规则吗？\n{deviceId}")) return;
                    ctx.Window.RunBackground(operation, "正在取消离线设备禁用规则",
                        () => ctx.Window.App.UpdateConfigAsync(config => config.DisabledDeviceInstanceIds.RemoveAll(id =>
                            string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase))), "已取消离线设备禁用规则");
                }, enabled: !ctx.Window.IsOperationRunning(operation), confirm: false);
            bottom = rect.Bottom;
        }
        return bottom - area.Top;
    }


}
