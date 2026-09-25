using NetworkGuardian.Core.Models;
using Models = NetworkGuardian.Core.Models;

namespace NetworkGuardian.Portable.Ui;

/// <summary>
/// Display text shared by the pages. The wording matches the WinUI version so screenshots and logs
/// stay comparable between the two builds.
/// </summary>
internal static class Format
{
    public static string RecoveryState(Models.RecoveryState state) => state switch
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

    public static string Health(GuardianHealth health) => health switch
    {
        GuardianHealth.Healthy => "正常",
        GuardianHealth.Recovering => "恢复中",
        GuardianHealth.Degraded => "降级",
        GuardianHealth.Error => "错误",
        GuardianHealth.Paused => "已暂停",
        _ => "未知",
    };

    public static string RadioState(Models.RadioState state) => state switch
    {
        Models.RadioState.On => "开启",
        Models.RadioState.Off => "关闭",
        Models.RadioState.Disabled => "被硬件/策略禁用",
        _ => "未知",
    };

    public static string Connectivity(ConnectivityLevel level) => level switch
    {
        ConnectivityLevel.Online => "在线",
        ConnectivityLevel.CaptivePortal => "认证页拦截",
        ConnectivityLevel.NoLink => "无链路",
        ConnectivityLevel.NoIpConfiguration => "无 IP 配置",
        ConnectivityLevel.NoDefaultRoute => "无默认路由",
        ConnectivityLevel.NoInternet => "无外网",
        ConnectivityLevel.ProbeFailed => "探测失败",
        _ => "未知",
    };

    /// <summary>An on-link default route reports 0.0.0.0, which means "no next hop" rather than an address.</summary>
    public static string NextHop(string? nextHop) =>
        string.IsNullOrWhiteSpace(nextHop) || nextHop == "0.0.0.0" || nextHop == "::"
            ? "0.0.0.0（直接路由，无下一跳）"
            : nextHop;

    public static string Band(NetworkBand band) => band switch
    {
        NetworkBand.Band2_4GHz => "2.4 GHz",
        NetworkBand.Band5GHz => "5 GHz",
        NetworkBand.Band6GHz => "6 GHz",
        NetworkBand.Band60GHz => "60 GHz",
        _ => "未知",
    };

    public static string Security(WifiSecurity security) => security switch
    {
        WifiSecurity.Open => "开放",
        WifiSecurity.Wep => "WEP",
        WifiSecurity.WpaPersonal => "WPA",
        WifiSecurity.Wpa2Personal => "WPA2",
        WifiSecurity.Wpa3Personal => "WPA3",
        WifiSecurity.WpaEnterprise => "WPA-企业",
        WifiSecurity.Wpa2Enterprise => "WPA2-企业",
        WifiSecurity.Wpa3Enterprise => "WPA3-企业",
        WifiSecurity.EnhancedOpen => "增强开放",
        _ => "未知",
    };

    public static string ConnectionState(WifiConnectionState state) => state switch
    {
        WifiConnectionState.Connected => "已连接",
        WifiConnectionState.Disconnected => "未连接",
        WifiConnectionState.Associating => "正在关联",
        WifiConnectionState.Authenticating => "正在认证",
        WifiConnectionState.Connecting => "正在连接",
        WifiConnectionState.Disconnecting => "正在断开",
        WifiConnectionState.AdHocFormed => "Ad-Hoc",
        WifiConnectionState.NotReady => "不可用",
        _ => "未知",
    };

    public static string InterfaceKind(Models.InterfaceKind kind) => kind switch
    {
        Models.InterfaceKind.Ethernet => "以太网",
        Models.InterfaceKind.Wifi => "Wi-Fi",
        Models.InterfaceKind.Loopback => "回环",
        Models.InterfaceKind.Tunnel => "隧道",
        Models.InterfaceKind.Virtual => "虚拟",
        _ => "其他",
    };

    public static string DeviceCategory(Models.DeviceCategory category) => category switch
    {
        Models.DeviceCategory.PhysicalEthernet => "物理以太网",
        Models.DeviceCategory.PhysicalWifi => "物理无线网卡",
        Models.DeviceCategory.Virtual => "虚拟网卡",
        _ => "其他网络设备",
    };

    public static string ProbeReport(ConnectivityProbeReport? report)
    {
        if (report is null)
        {
            return "未按接口探测";
        }

        if (report.AttemptCount == 0)
        {
            return "尚未检测";
        }

        var reachability = report.Reachability switch
        {
            InternetReachability.InternetVerified => "外网已验证",
            InternetReachability.InternetLikely => "疑似可访问外网",
            InternetReachability.CaptivePortal => "认证门户/受限网络",
            InternetReachability.LocalOnly => "仅局域网可达",
            _ => "状态不确定",
        };
        var stability = report.ConsecutiveFailures > 0
            ? $"｜连续失败 {report.ConsecutiveFailures}"
            : report.ConsecutiveSuccesses > 0
                ? $"｜连续成功 {report.ConsecutiveSuccesses}"
                : string.Empty;
        var capture = report.CaptureVerification switch
        {
            PacketCaptureVerification.VerifiedOnTargetInterface => "｜Npcap 已确认目标接口",
            PacketCaptureVerification.NoTrafficOnTargetInterface => "｜Npcap 未发现目标接口双向流量",
            PacketCaptureVerification.CaptureFailed => "｜Npcap 抓包失败",
            PacketCaptureVerification.Unavailable => "｜Npcap 不可用",
            _ => string.Empty,
        };
        var authority = report.IsAuthoritative ? "｜Npcap 原始权威判定" : string.Empty;
        var text = $"{reachability}｜强证据 {report.SuccessCount}/{report.AttemptCount}" +
                   $"｜用时 {report.Duration.TotalMilliseconds:F0} ms{authority}{stability}{capture}";
        return report.FirstFailureDetail is { Length: > 0 } detail ? $"{text}｜{detail}" : text;
    }

    public static string Bytes(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000.0:F1} Gbit/s",
        >= 1_000_000 => $"{value / 1_000_000.0:F1} Mbit/s",
        >= 1_000 => $"{value / 1_000.0:F0} kbit/s",
        _ => $"{value} bit/s",
    };

    public static string LocalTime(DateTimeOffset? value) =>
        value is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—";

    /// <summary>
    /// Display name for a Wi-Fi adapter. The Native Wi-Fi description is often a single letter
    /// (the driver's own interface name), so the PnP friendly name is preferred when it exists.
    /// </summary>
    public static string AdapterName(GuardianSnapshot snapshot, WifiAdapterRuntimeState adapter)
    {
        if (adapter.DeviceInstanceId is { Length: > 0 } instanceId)
        {
            foreach (var device in snapshot.WifiDevices)
            {
                if (!string.Equals(device.Record.DeviceInstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = device.Record.FriendlyName ?? device.Record.DeviceDescription;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return adapter.Description;
    }
}
