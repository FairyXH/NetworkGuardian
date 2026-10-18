namespace NetworkGuardian.Core.Models;

public sealed class NetworkHistory
{
    public List<LinkQualityHistory> Links { get; set; } = new();
    public PreferenceState Preferences { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public SurveyState Survey { get; set; } = new();
    public long SurveyRevision { get; set; }
    public Policies.NetworkBrainModel? Brain { get; set; }
    public List<SuppressedWifiProfile> SuppressedProfiles { get; set; } = new();
}

public sealed record SuppressedWifiProfile(Guid Adapter, string ProfileName);

public sealed class LinkQualityHistory
{
    public InterfaceKind Kind { get; set; } = InterfaceKind.Wifi;
    public string InterfaceId { get; set; } = string.Empty;
    public string NetworkId { get; set; } = string.Empty;
    public string NetworkName { get; set; } = string.Empty;
    public Guid Adapter { get; set; }
    public string Ssid { get; set; } = string.Empty;
    public NetworkBand Band { get; set; }
    public string? DeviceInstanceId { get; set; }
    public long TotalSamples { get; set; }
    public double LongTermScore { get; set; } = 50;
    public List<LinkQualitySample> Samples { get; set; } = new();
}

public sealed record LinkQualitySample
{
    public bool? InternetOnline { get; init; }
    public DateTimeOffset AtUtc { get; init; }
    public int GatewaySent { get; init; }
    public int GatewayReplies { get; init; }
    public int InternetSent { get; init; }
    public int InternetReplies { get; init; }
    public int TcpSent { get; init; }
    public int TcpReplies { get; init; }
    public double? LatencyMs { get; init; }
    public double JitterMs { get; init; }
}

public sealed record LinkQualityEstimate
{
    public bool? InternetOnline { get; init; }
    // Setters preserve field defaults when older source-generated JSON omits this metadata.
    public InterfaceKind Kind { get; set; } = InterfaceKind.Wifi;
    public string InterfaceId { get; set; } = string.Empty;
    public string NetworkId { get; set; } = string.Empty;
    public string NetworkName { get; set; } = string.Empty;
    public Guid Adapter { get; init; }
    public string Ssid { get; init; } = string.Empty;
    public NetworkBand Band { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public int Samples { get; init; }
    public double Score { get; init; } = 50;
    public double RecentScore { get; init; } = 50;
    public double? GatewayLoss { get; init; }
    public double? InternetLoss { get; init; }
    public double? TcpFailure { get; init; }
    public double? LatencyMs { get; init; }
    public double JitterMs { get; init; }
    public double Confidence => Math.Min(1, Samples / 20d);
    public bool IsMeasured => Samples >= 5;
    public string Grade => !IsMeasured ? "待评估" : Score >= 85 ? "优秀" : Score >= 70 ? "良好" : Score >= 50 ? "一般" : "较差";
    public string Summary => $"{Grade} {Score:F0} 分 · {Samples} 组采样 · 网关丢包 {Percent(GatewayLoss)} · " +
        $"外网 ICMP {Percent(InternetLoss)} · TCP 失败 {Percent(TcpFailure)} · 延迟 {LatencyMs?.ToString("F0") ?? "—"} ms · 抖动 {JitterMs:F0} ms";
    private static string Percent(double? rate) => rate is { } value ? $"{value:P0}" : "待确认";
}
