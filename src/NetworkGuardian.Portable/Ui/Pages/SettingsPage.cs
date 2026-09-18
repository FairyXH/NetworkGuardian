using System.Drawing;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Portable.Ui.Pages;

/// <summary>
/// 设置：所有开关与阈值、探测端点、离线命令。编辑的是一份深拷贝，只有“保存并应用”才写回。
/// </summary>
internal sealed class SettingsPage : IPage
{
    private static readonly string[] ProbeKindLabels = { "TCP", "HTTP", "HTTPS", "DNS", "ICMP" };
    private static readonly string[] CommandKindLabels = { "程序（可执行文件 + 参数）", "命令行（通过 cmd /c 执行整行命令）" };
    private static readonly string[] LogLevelLabels = { "trace", "debug", "information", "warning", "error" };
    private static readonly GuardianLogLevel[] LogLevelValues =
    {
        GuardianLogLevel.Trace, GuardianLogLevel.Debug, GuardianLogLevel.Information,
        GuardianLogLevel.Warning, GuardianLogLevel.Error,
    };

    private GuardianConfig? _working;
    private string _status = "尚未加载";
    private bool _dirty;

    public string Tag => "settings";

    public string Label => "设置";

    public string Title => "设置";

    public string Description => "所有改动先落在工作副本上，点击“保存并应用”才会写入 config.json 并立即生效。";

    public int Render(PageContext ctx, Rectangle area)
    {
        var canvas = ctx.Canvas;
        var config = Working(ctx);
        var y = area.Top + Widgets.Heading(ctx, area, Title, Description);

        Widgets.Mono(ctx, area.Left, y, area.Width, $"配置文件：{ctx.Host.ConfigPath}");
        y += ctx.Scale(22);

        // ---------- toolbar ----------
        var save = Widgets.Button(ctx, area.Left, y, "保存并应用", () =>
        {
            var issues = new ConfigValidator().Normalize(config);
            _status = issues.Count == 0
                ? $"已保存 {DateTimeOffset.Now:HH:mm:ss}"
                : $"已保存；{issues.Count} 项被修正：{string.Join("；", issues)}";
            _dirty = false;

            // The live configuration gets its own copy so later edits keep hitting the working copy
            // until they are saved again.
            var target = Clone(config);
            ctx.Window.RunBackground(
                () => ctx.Window.App.ApplyConfigAsync(target),
                "配置已保存并应用");
        }, primary: true);

        Widgets.Button(ctx, save.Right + ctx.Scale(8), y, "重新加载", () =>
        {
            ctx.Window.RunBackground(async () =>
            {
                var reloaded = await ctx.Window.App.ReloadConfigAsync().ConfigureAwait(false);
                _working = Clone(reloaded);
                _status = "已从磁盘重新加载";
                _dirty = false;
            });
        });

        Widgets.Button(
            ctx,
            save.Right + ctx.Scale(8) + Widgets.MeasureButtonWidth(ctx, "重新加载") + ctx.Scale(8),
            y,
            "打开日志目录",
            () => ctx.Host.OpenLogFolder());

        canvas.Text(
            _status + (_dirty ? "（有未保存的改动）" : string.Empty),
            new Rectangle(save.Right + ctx.Scale(340), y, Math.Max(ctx.Scale(80), area.Right - save.Right - ctx.Scale(350)), ctx.Scale(32)),
            _dirty ? Palette.Warn : Palette.TextMuted,
            TextStyle.Caption);

        y += ctx.Scale(44);

        // ---------- 常规 ----------
        var general = new Form(ctx, this);
        general.Toggle("启用自动网络恢复", config.General.AutomaticRecovery, v => config.General.AutomaticRecovery = v);
        general.Toggle("以太网优先（不主动扰动 Wi-Fi 连接）", config.General.PreferEthernet, v => config.General.PreferEthernet = v);
        general.Toggle("允许自动打开 Wi-Fi 无线电开关", config.General.AutoEnableWifiRadio, v => config.General.AutoEnableWifiRadio = v);
        general.Toggle("允许自动启用被禁用的物理无线网卡", config.General.AutoEnableWifiDevices, v => config.General.AutoEnableWifiDevices = v);
        general.Toggle("故障无线网卡自动重启（禁用+启用，用于驱动启动失败）", config.General.AutoRestartFaultedWifiDevices, v => config.General.AutoRestartFaultedWifiDevices = v);
        general.Toggle("启动时确保无线电为开启", config.General.EnsureRadioOnAtStartup, v => config.General.EnsureRadioOnAtStartup = v);
        general.Number("巡检周期（秒）", config.General.HealthSweepSeconds, 5, 3600, v => config.General.HealthSweepSeconds = v);
        general.Number("设备枚举刷新周期（秒）", config.General.EnumerationRefreshSeconds, 30, 3600, v => config.General.EnumerationRefreshSeconds = v);
        general.Number("睡眠唤醒后的稳定等待（秒）", config.General.ResumeSettleSeconds, 0, 300, v => config.General.ResumeSettleSeconds = v);
        general.Number("同一网卡两次扫描最小间隔（秒）", config.General.MinimumScanIntervalSeconds, 5, 3600, v => config.General.MinimumScanIntervalSeconds = v);
        general.Number("扫描超时（秒）", config.General.ScanTimeoutSeconds, 3, 120, v => config.General.ScanTimeoutSeconds = v);
        general.Number("手动扫描超时（秒）", config.General.ManualScanTimeoutSeconds, 3, 120, v => config.General.ManualScanTimeoutSeconds = v);
        general.Number("连接后等待 DHCP（秒）", config.General.DhcpWaitSeconds, 3, 300, v => config.General.DhcpWaitSeconds = v);
        general.Toggle("自动调整接口度量（默认关闭，会改变路由优先级）", config.General.ManageInterfaceMetrics, v => config.General.ManageInterfaceMetrics = v);
        general.Number("以太网接口度量", config.General.PreferredEthernetMetric, 1, 9999, v => config.General.PreferredEthernetMetric = v);
        general.Number("Wi-Fi 接口度量", config.General.PreferredWifiMetric, 1, 9999, v => config.General.PreferredWifiMetric = v);
        y = DrawCard(ctx, area, y, "常规", general);

        // ---------- Internet 探测 ----------
        var probe = new Form(ctx, this);
        probe.Toggle("启用探测", config.Probe.Enabled, v => config.Probe.Enabled = v);
        probe.Number("探测周期（秒）", config.Probe.IntervalSeconds, 3, 3600, v => config.Probe.IntervalSeconds = v);
        probe.Number("单次超时（毫秒）", config.Probe.TimeoutMs, 200, 60000, v => config.Probe.TimeoutMs = v);
        probe.Number("整轮超时（毫秒）", config.Probe.RoundTimeoutMs, 500, 120000, v => config.Probe.RoundTimeoutMs = v);
        probe.Number("判定在线所需成功数", config.Probe.RequiredSuccessCount, 1, 10, v => config.Probe.RequiredSuccessCount = v);
        probe.Toggle("按接口分别探测（判断某张网卡自身是否可用）", config.Probe.PerInterfaceProbing, v => config.Probe.PerInterfaceProbing = v);
        probe.Toggle("允许 ICMP 探测", config.Probe.AllowIcmp, v => config.Probe.AllowIcmp = v);
        probe.Toggle("被认证页拦截时视为离线", config.Probe.TreatCaptivePortalAsOffline, v => config.Probe.TreatCaptivePortalAsOffline = v);
        probe.Subtitle("探测端点");

        var endpoints = config.ProbeEndpoints;
        for (var i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];
            var index = i;
            probe.Custom(ctx.Scale(184), rect =>
            {
                var inner = new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);
                Widgets.Field(ctx, inner.Left, inner.Top, ctx.Scale(200), "名称", endpoint.Name, v => { endpoint.Name = v; MarkDirty(); });
                Widgets.Dropdown(
                    ctx,
                    inner.Left + ctx.Scale(212),
                    inner.Top,
                    ctx.Scale(150),
                    "类型",
                    ProbeKindLabels,
                    (int)endpoint.Kind,
                    v => { endpoint.Kind = (ProbeKind)v; MarkDirty(); });
                Widgets.Toggle(
                    ctx,
                    inner.Left + ctx.Scale(380),
                    inner.Top + ctx.Scale(20),
                    inner.Width - ctx.Scale(400),
                    "启用",
                    endpoint.Enabled,
                    () => { endpoint.Enabled = !endpoint.Enabled; MarkDirty(); });

                Widgets.Field(ctx, inner.Left, inner.Top + ctx.Scale(62), inner.Width, "目标（host:port 或 URL；DNS 为域名）", endpoint.Target, v => { endpoint.Target = v; MarkDirty(); });

                Widgets.Number(ctx, inner.Left, inner.Top + ctx.Scale(124), ctx.Scale(170), "超时（毫秒）", endpoint.TimeoutMs ?? 0, 0, 60000, v => { endpoint.TimeoutMs = v == 0 ? null : v; MarkDirty(); });
                Widgets.Field(
                    ctx,
                    inner.Left + ctx.Scale(182),
                    inner.Top + ctx.Scale(124),
                    Math.Max(ctx.Scale(160), inner.Width - ctx.Scale(182) - ctx.Scale(100)),
                    "响应正文标记（可空）",
                    endpoint.BodyMarker ?? string.Empty,
                    v => { endpoint.BodyMarker = string.IsNullOrWhiteSpace(v) ? null : v; MarkDirty(); });

                var buttonWidth = Widgets.MeasureButtonWidth(ctx, "删除");
                Widgets.ButtonAt(
                    ctx,
                    new Rectangle(inner.Right - buttonWidth, inner.Top + ctx.Scale(142), buttonWidth, ctx.Scale(30)),
                    "删除",
                    () =>
                    {
                        endpoints.RemoveAt(index);
                        MarkDirty();
                    });
            });
        }

        probe.Button("新增探测端点", () =>
        {
            endpoints.Add(new ProbeEndpointSettings { Name = "NewEndpoint", Kind = ProbeKind.Tcp, Target = "223.5.5.5:53", TimeoutMs = 2000 });
            MarkDirty();
        });
        y = DrawCard(ctx, area, y, "Internet 探测", probe);

        // ---------- 无线策略 ----------
        var wifi = new Form(ctx, this);
        wifi.Toggle("粘性连接：已正常连接的网卡不参与择优切换", config.Wifi.StickyConnection, v => config.Wifi.StickyConnection = v);
        wifi.Toggle("允许在连接仍然存在但已无法上网时重新选网", config.Wifi.RecoverStaleConnections, v => config.Wifi.RecoverStaleConnections = v);
        wifi.Toggle("优先 5GHz / 6GHz", config.Wifi.PreferHighBand, v => config.Wifi.PreferHighBand = v);
        wifi.Toggle("只连接已有配置的网络（绝不加入陌生 Wi-Fi）", config.Wifi.OnlySavedProfiles, v => config.Wifi.OnlySavedProfiles = v);
        wifi.Toggle("允许隐藏的已保存配置", config.Wifi.AllowHiddenProfiles, v => config.Wifi.AllowHiddenProfiles = v);
        wifi.Toggle("允许多张网卡连接同一个 SSID（关闭时重复连接中信号较弱的网卡会被断开）", config.Wifi.AllowSameSsidOnMultipleAdapters, v => config.Wifi.AllowSameSsidOnMultipleAdapters = v);
        wifi.Toggle("优先最近连接过的配置", config.Wifi.PreferRecentProfiles, v => config.Wifi.PreferRecentProfiles = v);
        wifi.Number("信号强度迟滞（百分点）", config.Wifi.SignalHysteresis, 0, 100, v => config.Wifi.SignalHysteresis = v);
        wifi.Number("最低可用信号（%）", config.Wifi.MinimumSignalQuality, 0, 100, v => config.Wifi.MinimumSignalQuality = v);
        wifi.Number("掉线后进入恢复的宽限期（秒）", config.Wifi.DisconnectGraceSeconds, 0, 600, v => config.Wifi.DisconnectGraceSeconds = v);
        wifi.Number("5/6GHz 加成", (int)config.Wifi.HighBandBonus, 0, 50, v => config.Wifi.HighBandBonus = v);
        wifi.Number("最近配置加成", (int)config.Wifi.RecentProfileBonus, 0, 50, v => config.Wifi.RecentProfileBonus = v);
        wifi.Text("SSID 黑名单（每行一个，可留空）", Join(config.Wifi.SsidDenyList), v => config.Wifi.SsidDenyList = Split(v), multiline: true);
        wifi.Text("SSID 白名单（非空时只连接其中的 SSID）", Join(config.Wifi.SsidAllowList), v => config.Wifi.SsidAllowList = Split(v), multiline: true);
        y = DrawCard(ctx, area, y, "无线策略（粘性连接）", wifi);

        // ---------- 失败与重试 ----------
        var recovery = new Form(ctx, this);
        recovery.Number("外网连续失败阈值", config.Recovery.InternetFailureThreshold, 1, 100, v => config.Recovery.InternetFailureThreshold = v);
        recovery.Number("恢复判定所需连续成功次数", config.Recovery.InternetRecoveryThreshold, 1, 100, v => config.Recovery.InternetRecoveryThreshold = v);
        recovery.Number("无线连接失败阈值", config.Recovery.WifiFailureThreshold, 1, 100, v => config.Recovery.WifiFailureThreshold = v);
        recovery.Number("恢复冷却（秒）", config.Recovery.CooldownSeconds, 0, 7200, v => config.Recovery.CooldownSeconds = v);
        recovery.Number("退避基数（秒）", config.Recovery.BaseBackoffSeconds, 1, 3600, v => config.Recovery.BaseBackoffSeconds = v);
        recovery.Number("退避上限（秒）", config.Recovery.MaxBackoffSeconds, 5, 86400, v => config.Recovery.MaxBackoffSeconds = v);
        recovery.Number("单轮最大连接尝试次数", config.Recovery.MaxConnectAttemptsPerRound, 1, 50, v => config.Recovery.MaxConnectAttemptsPerRound = v);
        recovery.Number("连接失败后的短期封禁（秒）", config.Recovery.ConnectFailureBlacklistSeconds, 0, 86400, v => config.Recovery.ConnectFailureBlacklistSeconds = v);
        recovery.Number("启用网卡后的等待（秒）", config.Recovery.DeviceEnableSettleSeconds, 1, 120, v => config.Recovery.DeviceEnableSettleSeconds = v);
        recovery.Number("每小时最多启用次数", config.Recovery.MaxDeviceEnablePerHour, 1, 120, v => config.Recovery.MaxDeviceEnablePerHour = v);
        recovery.Number("同一操作熔断阈值（次）", config.Recovery.OperationCircuitBreakerThreshold, 1, 100, v => config.Recovery.OperationCircuitBreakerThreshold = v);
        recovery.Number("熔断保持时间（秒）", config.Recovery.OperationCircuitBreakerSeconds, 0, 86400, v => config.Recovery.OperationCircuitBreakerSeconds = v);
        y = DrawCard(ctx, area, y, "失败与重试", recovery);

        // ---------- 校园网认证 ----------
        var campus = new Form(ctx, this);
        var auth = config.CampusAuth;
        campus.Toggle("启用校园网认证程序", auth.Enabled, v => auth.Enabled = v);
        campus.Text("名称", auth.Name, v => auth.Name = v);
        campus.Choice("运行方式", CommandKindLabels, (int)auth.Kind, v => auth.Kind = (CommandKind)v);
        campus.Path("可执行文件路径 / 命令行", auth.ExecutablePath, v => auth.ExecutablePath = v, "选择认证程序");
        campus.Text("参数", auth.Arguments, v => auth.Arguments = v);
        campus.Path("工作目录", auth.WorkingDirectory ?? string.Empty, v => auth.WorkingDirectory = string.IsNullOrWhiteSpace(v) ? null : v, "选择工作目录（可选）");
        campus.Toggle("以管理员身份运行（会弹出 UAC）", auth.RunAsAdministrator, v => auth.RunAsAdministrator = v);
        campus.Number("连续失败多少次后触发", auth.TriggerAfterConsecutiveFailures, 1, 100, v => auth.TriggerAfterConsecutiveFailures = v);
        campus.Number("执行后等待（秒）", auth.WaitAfterRunSeconds, 0, 3600, v => auth.WaitAfterRunSeconds = v);
        campus.Number("最短重复间隔（秒）", auth.MinIntervalSeconds, 0, 86400, v => auth.MinIntervalSeconds = v);
        campus.Number("每小时最大次数", auth.MaxRunsPerHour, 1, 1000, v => auth.MaxRunsPerHour = v);
        campus.Number("最大连续次数（无成功则停止）", auth.MaxConsecutiveRuns, 1, 1000, v => auth.MaxConsecutiveRuns = v);
        campus.Number("执行超时（秒，0 = 不限）", auth.ExecutionTimeoutSeconds, 0, 3600, v => auth.ExecutionTimeoutSeconds = v);
        campus.Toggle("超时后结束进程", auth.KillOnTimeout, v => auth.KillOnTimeout = v);
        campus.Toggle("已运行时不再启动新实例", auth.SkipIfAlreadyRunning, v => auth.SkipIfAlreadyRunning = v);
        campus.Toggle("等待进程退出后再继续", auth.WaitForExit, v => auth.WaitForExit = v);
        campus.Toggle("检测到认证页拦截时也执行", auth.RunOnCaptivePortal, v => auth.RunOnCaptivePortal = v);
        campus.Toggle("仅在物理以太网链路可用时执行", auth.RequireEthernetLink, v => auth.RequireEthernetLink = v);
        campus.Number("认证后额外验证探测次数", auth.VerificationProbes, 0, 10, v => auth.VerificationProbes = v);
        y = DrawCard(ctx, area, y, "校园网认证", campus);

        // ---------- 离线命令 ----------
        var commands = new Form(ctx, this);
        commands.Note("当外网不可用时按顺序执行下列命令，适用于“软件式校园网”需要运行客户端或命令行的场景。");
        commands.Button("新增命令", () =>
        {
            config.OfflineCommands.Add(new CommandDefinition
            {
                Name = "新命令",
                Kind = CommandKind.Shell,
                ExecutablePath = "curl -s -X POST http://10.0.0.1/login",
                WorkingDirectory = "C:\\Windows\\System32",
            });
            MarkDirty();
        });
        y = DrawCard(ctx, area, y, "断网时运行命令行 / 自定义命令", commands);

        for (var i = 0; i < config.OfflineCommands.Count; i++)
        {
            var command = config.OfflineCommands[i];
            var commandForm = new Form(ctx, this);
            commandForm.Subtitle($"{command.Name}　({(command.Enabled ? "启用" : "停用")})");
            commandForm.Custom(ctx.Scale(232), rect =>
            {
                Widgets.Field(ctx, rect.Left, rect.Top, ctx.Scale(200), "名称", command.Name, v => { command.Name = v; MarkDirty(); });
                Widgets.Dropdown(ctx, rect.Left + ctx.Scale(212), rect.Top, ctx.Scale(220), "运行方式", CommandKindLabels, (int)command.Kind, v => { command.Kind = (CommandKind)v; MarkDirty(); });
                Widgets.Toggle(ctx, rect.Left + ctx.Scale(444), rect.Top + ctx.Scale(20), Math.Max(ctx.Scale(120), rect.Width - ctx.Scale(464)), "启用该命令", command.Enabled, () => { command.Enabled = !command.Enabled; MarkDirty(); });

                Widgets.Field(ctx, rect.Left, rect.Top + ctx.Scale(62), Math.Max(ctx.Scale(160), rect.Width - ctx.Scale(100)), "可执行文件或命令行", command.ExecutablePath, v => { command.ExecutablePath = v; MarkDirty(); });
                var browseWidth = Widgets.MeasureButtonWidth(ctx, "浏览…");
                Widgets.ButtonAt(ctx, new Rectangle(rect.Right - browseWidth, rect.Top + ctx.Scale(80), browseWidth, ctx.Scale(30)), "浏览…", () =>
                {
                    var picked = FileDialog.PickExecutable(ctx.Window.Handle, "选择命令目标");
                    if (picked is not null)
                    {
                        command.ExecutablePath = picked;
                        MarkDirty();
                    }
                });

                Widgets.Field(ctx, rect.Left, rect.Top + ctx.Scale(118), rect.Width, "参数（可空）", command.Arguments, v => { command.Arguments = v; MarkDirty(); });
                Widgets.Field(
                    ctx,
                    rect.Left,
                    rect.Top + ctx.Scale(174),
                    Math.Max(ctx.Scale(160), rect.Width - ctx.Scale(100)),
                    "工作目录（可空）",
                    command.WorkingDirectory ?? string.Empty,
                    v => { command.WorkingDirectory = string.IsNullOrWhiteSpace(v) ? null : v; MarkDirty(); });
                Widgets.ButtonAt(ctx, new Rectangle(rect.Right - browseWidth, rect.Top + ctx.Scale(192), browseWidth, ctx.Scale(30)), "浏览…", () =>
                {
                    var picked = FileDialog.PickExecutable(ctx.Window.Handle, "选择工作目录下的程序");
                    if (picked is not null)
                    {
                        command.WorkingDirectory = Path.GetDirectoryName(picked);
                        MarkDirty();
                    }
                });
            });

            commandForm.Custom(ctx.Scale(196), rect =>
            {
                var width = (rect.Width - (ctx.Scale(12) * 3)) / 4;
                Widgets.Number(ctx, rect.Left, rect.Top, width, "超时（秒，0 = 不限）", command.ExecutionTimeoutSeconds, 0, 3600, v => { command.ExecutionTimeoutSeconds = v; MarkDirty(); });
                Widgets.Number(ctx, rect.Left + width + ctx.Scale(12), rect.Top, width, "最小间隔（秒）", command.MinIntervalSeconds, 0, 86400, v => { command.MinIntervalSeconds = v; MarkDirty(); });
                Widgets.Number(ctx, rect.Left + ((width + ctx.Scale(12)) * 2), rect.Top, width, "每小时上限", command.MaxRunsPerHour, 1, 1000, v => { command.MaxRunsPerHour = v; MarkDirty(); });
                Widgets.Number(ctx, rect.Left + ((width + ctx.Scale(12)) * 3), rect.Top, width, "连续上限", command.MaxConsecutiveRuns, 1, 1000, v => { command.MaxConsecutiveRuns = v; MarkDirty(); });

                var toggleWidth = (rect.Width - (ctx.Scale(12) * 3)) / 4;
                Widgets.Toggle(ctx, rect.Left, rect.Top + ctx.Scale(74), toggleWidth, "以管理员运行", command.RunAsAdministrator, () => { command.RunAsAdministrator = !command.RunAsAdministrator; MarkDirty(); });
                Widgets.Toggle(ctx, rect.Left + toggleWidth + ctx.Scale(12), rect.Top + ctx.Scale(74), toggleWidth, "已运行时跳过", command.SkipIfAlreadyRunning, () => { command.SkipIfAlreadyRunning = !command.SkipIfAlreadyRunning; MarkDirty(); });
                Widgets.Toggle(ctx, rect.Left + ((toggleWidth + ctx.Scale(12)) * 2), rect.Top + ctx.Scale(74), toggleWidth, "超时后结束", command.KillOnTimeout, () => { command.KillOnTimeout = !command.KillOnTimeout; MarkDirty(); });
                Widgets.Toggle(ctx, rect.Left + ((toggleWidth + ctx.Scale(12)) * 3), rect.Top + ctx.Scale(74), toggleWidth, "等待退出", command.WaitForExit, () => { command.WaitForExit = !command.WaitForExit; MarkDirty(); });

                Widgets.Number(ctx, rect.Left, rect.Top + ctx.Scale(112), width, "执行后等待（秒）", command.WaitAfterRunSeconds, 0, 3600, v => { command.WaitAfterRunSeconds = v; MarkDirty(); });
                Widgets.Mono(ctx, rect.Left + width + ctx.Scale(12), rect.Top + ctx.Scale(132), rect.Width - width - ctx.Scale(110),
                    command.Kind == CommandKind.Shell ? "shell：整行命令交给 cmd.exe /c 执行" : "executable：直接启动文件并附加参数");

                var buttonWidth = Widgets.MeasureButtonWidth(ctx, "删除");
                Widgets.ButtonAt(ctx, new Rectangle(rect.Right - buttonWidth, rect.Top + ctx.Scale(126), buttonWidth, ctx.Scale(30)), "删除", () =>
                {
                    config.OfflineCommands.Remove(command);
                    MarkDirty();
                });
            });

            y = DrawCard(ctx, area, y, $"离线命令 #{i + 1}", commandForm);
        }

        // ---------- 以太网 ----------
        var ethernet = new Form(ctx, this);
        ethernet.Toggle("启用以太网监控", config.Ethernet.Enabled, v => config.Ethernet.Enabled = v);
        ethernet.Toggle("链路在但无法上网时执行校园网认证", config.Ethernet.AuthenticateWhenLinkUpButOffline, v => config.Ethernet.AuthenticateWhenLinkUpButOffline = v);
        ethernet.Toggle("链路在但没有 IP 配置时也认证", config.Ethernet.AuthenticateOnNoIpConfiguration, v => config.Ethernet.AuthenticateOnNoIpConfiguration = v);
        ethernet.Number("以太网失败阈值", config.Ethernet.FailureThreshold, 1, 100, v => config.Ethernet.FailureThreshold = v);
        ethernet.Number("链路建立后的宽限（秒）", config.Ethernet.LinkUpGraceSeconds, 0, 600, v => config.Ethernet.LinkUpGraceSeconds = v);
        y = DrawCard(ctx, area, y, "以太网", ethernet);

        // ---------- 启动与托盘 ----------
        var startup = new Form(ctx, this);
        startup.Toggle("开机启动（HKCU Run，仅当前用户）", config.Startup.RunAtLogon, v =>
        {
            config.Startup.RunAtLogon = v;
            MarkDirty();
        });
        startup.Toggle("启动时最小化", config.Startup.StartMinimized, v => config.Startup.StartMinimized = v);
        startup.Toggle("最小化到托盘", config.Startup.MinimizeToTray, v => config.Startup.MinimizeToTray = v);
        startup.Toggle("关闭按钮隐藏到托盘（关闭则退出程序）", config.Startup.CloseToTray, v => config.Startup.CloseToTray = v);
        startup.Note($"当前注册表状态：{(ctx.Window.App.IsRunAtLogonEnabled() ? "已注册开机启动" : "未注册开机启动")}");
        y = DrawCard(ctx, area, y, "启动与托盘", startup);

        // ---------- 日志 ----------
        var logging = new Form(ctx, this);
        logging.Choice("最低日志等级", LogLevelLabels, Math.Max(0, Array.IndexOf(LogLevelValues, config.Logging.MinimumLevel)), v => config.Logging.MinimumLevel = LogLevelValues[v]);
        logging.Toggle("写入文件", config.Logging.WriteToFile, v => config.Logging.WriteToFile = v);
        logging.Number("保留天数", config.Logging.RetentionDays, 1, 3650, v => config.Logging.RetentionDays = v);
        logging.Number("单文件大小上限（KB）", config.Logging.MaxFileSizeKb, 64, 262144, v => config.Logging.MaxFileSizeKb = v);
        logging.Number("最大文件数", config.Logging.MaxFiles, 1, 1000, v => config.Logging.MaxFiles = v);
        logging.Number("界面日志缓冲条数", config.Logging.UiBufferSize, 100, 100000, v => config.Logging.UiBufferSize = v);
        logging.Note("日志中不记录任何 Wi-Fi 密码；外部命令的参数内容也不会写入日志。");
        y = DrawCard(ctx, area, y, "日志", logging);

        // ---------- 高级 ----------
        var advanced = new Form(ctx, this);
        advanced.Text("额外排除规则（每行一个通配符，匹配 DeviceInstanceId 或设备名）", Join(config.InterfaceDenyList), v => config.InterfaceDenyList = Split(v), multiline: true);
        advanced.Note("被过滤的设备仍会显示在“无线网卡”页的设备列表中，并标注过滤原因。");
        y = DrawCard(ctx, area, y, "高级（设备过滤）", advanced);

        return y - area.Top;
    }

    private GuardianConfig Working(PageContext ctx)
    {
        if (_working is null)
        {
            _working = Clone(ctx.Host.Config);
        }

        return _working;
    }

    private void MarkDirty() => _dirty = true;

    /// <summary>Deep copy through the source generated serializer: no reflection, no shared references.</summary>
    private static GuardianConfig Clone(GuardianConfig config) =>
        ConfigJson.Deserialize(ConfigJson.Serialize(config)) ?? GuardianConfig.CreateDefault();

    private static string Join(IReadOnlyList<string> values) => string.Join(Environment.NewLine, values);

    private static List<string> Split(string value) => value
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private int DrawCard(PageContext ctx, Rectangle area, int y, string title, Form form)
    {
        var padding = ctx.Scale(16);
        var height = form.Height + (padding * 2) + ctx.Scale(30);
        var rect = new Rectangle(area.Left, y, area.Width, height);
        ctx.Canvas.Card(rect);

        var inner = new Rectangle(rect.Left + padding, rect.Top + padding, rect.Width - (padding * 2), rect.Height);
        var contentTop = inner.Top + Widgets.SectionTitle(ctx, inner.Left, inner.Top, inner.Width, title) + ctx.Scale(6);
        form.Draw(new Rectangle(inner.Left, contentTop, inner.Width, form.Height));

        return rect.Bottom + ctx.Scale(12);
    }

    /// <summary>
    /// Row recorder: the page is measured and drawn in two passes so a card can be sized from its
    /// content before anything is painted.
    /// </summary>
    private sealed class Form
    {
        private readonly PageContext _ctx;
        private readonly SettingsPage _owner;
        private readonly List<(int Height, Action<Rectangle> Draw)> _rows = new();

        public Form(PageContext ctx, SettingsPage owner)
        {
            _ctx = ctx;
            _owner = owner;
        }

        public int Height { get; private set; }

        public void Draw(Rectangle rect)
        {
            var y = rect.Top;
            foreach (var row in _rows)
            {
                row.Draw(new Rectangle(rect.Left, y, rect.Width, row.Height));
                y += row.Height;
            }
        }

        public void Subtitle(string text) => Add(_ctx.Scale(28), r =>
            _ctx.Canvas.Text(text, r, Palette.Accent, TextStyle.Section));

        public void Note(string text) => Add(
            _ctx.Scale(24) + _ctx.Canvas.MeasureWrappedHeight(text, _ctx.Canvas.Width - _ctx.Scale(80), TextStyle.Caption),
            r => Widgets.Caption(_ctx, r.Left, r.Top, r.Width, text));

        public void Toggle(string label, bool value, Action<bool> set) => Add(_ctx.Scale(32), r =>
            Widgets.Toggle(_ctx, r.Left, r.Top, r.Width, label, value, () =>
            {
                set(!value);
                _owner.MarkDirty();
            }));

        public void Number(string label, int value, int min, int max, Action<int> set) => Add(_ctx.Scale(64), r =>
            Widgets.StepField(_ctx, r.Left, r.Top, Math.Min(r.Width, _ctx.Scale(320)), label, value.ToString(), delta =>
            {
                var next = Math.Clamp(value + delta, min, max);
                if (next == value)
                {
                    return;
                }

                set(next);
                _owner.MarkDirty();
            }));


        public void Text(string label, string value, Action<string> set, bool multiline = false) => Add(
            _ctx.Scale(multiline ? 96 : 56),
            r => Widgets.Field(_ctx, r.Left, r.Top, r.Width, label, value, v =>
            {
                set(v);
                _owner.MarkDirty();
            }, multiline));

        public void Path(string label, string value, Action<string> set, string dialogTitle) => Add(_ctx.Scale(56), r =>
        {
            var buttonWidth = Widgets.MeasureButtonWidth(_ctx, "浏览…");
            var fieldWidth = Math.Max(_ctx.Scale(160), r.Width - buttonWidth - _ctx.Scale(8));
            Widgets.Field(_ctx, r.Left, r.Top, fieldWidth, label, value, v =>
            {
                set(v);
                _owner.MarkDirty();
            });

            Widgets.ButtonAt(_ctx, new Rectangle(r.Left + fieldWidth + _ctx.Scale(8), r.Top + _ctx.Scale(18), buttonWidth, _ctx.Scale(30)), "浏览…", () =>
            {
                var picked = FileDialog.PickExecutable(_ctx.Window.Handle, dialogTitle);
                if (picked is not null)
                {
                    set(picked);
                    _owner.MarkDirty();
                }
            });
        });

        public void Choice(string label, IReadOnlyList<string> items, int selected, Action<int> set) => Add(_ctx.Scale(56), r =>
            Widgets.Dropdown(_ctx, r.Left, r.Top, Math.Min(r.Width, _ctx.Scale(360)), label, items, selected, v =>
            {
                set(v);
                _owner.MarkDirty();
            }));

        public void Button(string label, Action onClick) => Add(_ctx.Scale(40), r =>
            Widgets.Button(_ctx, r.Left, r.Top, label, () =>
            {
                onClick();
                _ctx.Window.Invalidate();
            }));

        public void Custom(int height, Action<Rectangle> draw) => Add(height, draw);

        private void Add(int height, Action<Rectangle> draw)
        {
            _rows.Add((height, draw));
            Height += height;
        }
    }
}
