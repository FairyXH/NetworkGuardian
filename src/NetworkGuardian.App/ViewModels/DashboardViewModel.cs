using NetworkGuardian.Core.Models;
using Models = NetworkGuardian.Core.Models;

namespace NetworkGuardian.App.ViewModels;

/// <summary>Read-only summary data shown on the dashboard.</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private string _internetState = "未知";
    private string _internetDetail = "尚未检测";
    private string _ethernetState = "未知";
    private string _ethernetDetail = string.Empty;
    private string _defaultRoute = "—";
    private string _radioState = "未知";
    private string _wifiAdapterCount = "0";
    private string _wifiSummary = string.Empty;
    private string _recoveryState = "初始化";
    private string _health = "未知";
    private string _lastAction = "—";
    private string _lastActionTime = "—";
    private string _failureCount = "0";
    private string _campusAuthInfo = "未配置";
    private string _locationWarning = string.Empty;
    private bool _showLocationWarning;
    private string _pendingActions = string.Empty;

    public string InternetState { get => _internetState; private set => Set(ref _internetState, value); }

    public string InternetDetail { get => _internetDetail; private set => Set(ref _internetDetail, value); }

    public string EthernetState { get => _ethernetState; private set => Set(ref _ethernetState, value); }

    public string EthernetDetail { get => _ethernetDetail; private set => Set(ref _ethernetDetail, value); }

    public string DefaultRoute { get => _defaultRoute; private set => Set(ref _defaultRoute, value); }

    public string RadioState { get => _radioState; private set => Set(ref _radioState, value); }

    public string WifiAdapterCount { get => _wifiAdapterCount; private set => Set(ref _wifiAdapterCount, value); }

    public string WifiSummary { get => _wifiSummary; private set => Set(ref _wifiSummary, value); }

    public string RecoveryState { get => _recoveryState; private set => Set(ref _recoveryState, value); }

    public string Health { get => _health; private set => Set(ref _health, value); }

    public string LastAction { get => _lastAction; private set => Set(ref _lastAction, value); }

    public string LastActionTime { get => _lastActionTime; private set => Set(ref _lastActionTime, value); }

    public string FailureCount { get => _failureCount; private set => Set(ref _failureCount, value); }

    public string CampusAuthInfo { get => _campusAuthInfo; private set => Set(ref _campusAuthInfo, value); }

    public string LocationWarning { get => _locationWarning; private set => Set(ref _locationWarning, value); }

    public bool ShowLocationWarning { get => _showLocationWarning; private set => Set(ref _showLocationWarning, value); }

    public string PendingActions { get => _pendingActions; private set => Set(ref _pendingActions, value); }

    public void Update(GuardianSnapshot snapshot)
    {
        InternetState = snapshot.GlobalProbe.IsOnline ? "在线" : "离线";
        InternetDetail = snapshot.GlobalProbe.AttemptCount == 0
            ? "尚未检测"
            : $"{snapshot.GlobalProbe.Summary}｜用时 {snapshot.GlobalProbe.Duration.TotalMilliseconds:F0} ms" +
              (snapshot.GlobalProbe.FirstFailureDetail is { Length: > 0 } detail ? $"｜{detail}" : string.Empty);

        var ethernet = snapshot.Interfaces.Where(i => i.Kind == InterfaceKind.Ethernet).ToList();
        var up = ethernet.Where(i => i.IsUp).ToList();
        EthernetState = ethernet.Count == 0
            ? "未检测到物理以太网"
            : up.Count == 0 ? "链路断开" : "链路已连接";
        EthernetDetail = ethernet.Count == 0
            ? "—"
            : string.Join("；", ethernet.Select(i =>
                $"{i.Name}: {(i.IsUp ? "Up" : "Down")} {i.PrimaryIpv4Address ?? "无 IPv4"}"));

        var route = snapshot.DefaultRoutes.FirstOrDefault();
        DefaultRoute = route is null
            ? "—"
            : $"{route.NextHop}（接口 {route.InterfaceAlias ?? route.InterfaceLuid?.ToString() ?? "?"}，" +
              $"metric {route.EffectiveMetric?.ToString() ?? "?"}）";

        RadioState = snapshot.Radio.State switch
        {
            Models.RadioState.On => "开启",
            Models.RadioState.Off => "关闭",
            Models.RadioState.Disabled => "被硬件/策略禁用",
            _ => "未知",
        };

        WifiAdapterCount = snapshot.WifiAdapters.Count.ToString();
        WifiSummary = snapshot.WifiAdapters.Count == 0
            ? "未发现物理无线网卡"
            : string.Join("；", snapshot.WifiAdapters.Select(a =>
                $"{a.Description}: {(a.IsConnected ? $"{a.CurrentSsid}（{a.SignalQuality}%）" : "未连接")}"));

        RecoveryState = DescribeState(snapshot.State);
        Health = snapshot.Health switch
        {
            GuardianHealth.Healthy => "正常",
            GuardianHealth.Recovering => "恢复中",
            GuardianHealth.Degraded => "降级",
            GuardianHealth.Error => "错误",
            GuardianHealth.Paused => "已暂停",
            _ => "未知",
        };

        LastAction = snapshot.LastRecoveryAction ?? "—";
        LastActionTime = snapshot.LastRecoveryActionUtc is { } time
            ? time.ToLocalTime().ToString("HH:mm:ss")
            : "—";
        FailureCount = snapshot.ConsecutiveInternetFailures.ToString();
        CampusAuthInfo = snapshot.LastCampusAuthUtc is { } auth
            ? $"上次执行 {auth.ToLocalTime():HH:mm:ss}，最近一小时 {snapshot.CampusAuthRunCount} 次"
            : "尚未执行";

        var location = snapshot.Location;
        ShowLocationWarning = location.HasProblem;
        LocationWarning = location.HasProblem
            ? Windows.Location.LocationPermissionService.BuildGuidance(location)
            : string.Empty;

        PendingActions = snapshot.PendingActions.Count == 0
            ? "无（当前不需要恢复动作）"
            : string.Join(Environment.NewLine, snapshot.PendingActions.Select(a => a.Describe()));
    }

    private static string DescribeState(Models.RecoveryState state) => state switch
    {
        Models.RecoveryState.Initializing => "初始化",
        Models.RecoveryState.Healthy => "正常",
        Models.RecoveryState.Degraded => "降级",
        Models.RecoveryState.EthernetNoInternet => "以太网无外网",
        Models.RecoveryState.Authenticating => "正在认证",
        Models.RecoveryState.WaitingForAuthentication => "等待认证完成",
        Models.RecoveryState.WifiRadioOff => "Wi-Fi 无线电关闭",
        Models.RecoveryState.EnablingWifiRadio => "正在开启无线电",
        Models.RecoveryState.EnablingWifiDevices => "正在启用无线网卡",
        Models.RecoveryState.WifiScanning => "正在扫描",
        Models.RecoveryState.WifiConnecting => "正在连接",
        Models.RecoveryState.WaitingForDhcp => "等待 DHCP",
        Models.RecoveryState.VerifyingInternet => "验证连通性",
        Models.RecoveryState.Recovering => "恢复中",
        Models.RecoveryState.Cooldown => "冷却中",
        Models.RecoveryState.Paused => "已暂停",
        Models.RecoveryState.Error => "错误",
        _ => state.ToString(),
    };
}
