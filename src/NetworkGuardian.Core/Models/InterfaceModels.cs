namespace NetworkGuardian.Core.Models;

/// <summary>
/// Per-interface runtime state assembled from IP Helper API (GetAdaptersAddresses /
/// GetIpForwardTable2 / GetIfEntry2) and NetworkInterface.
/// </summary>
public sealed record InterfaceRuntimeState
{
    /// <summary>Stable identifier used for logging and probe correlation (adapter LUID or GUID).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required InterfaceKind Kind { get; init; }

    public Guid? WlanInterfaceGuid { get; init; }

    public string? DeviceInstanceId { get; init; }

    public bool IsUp { get; init; }

    /// <summary>
    /// PnP verdict for this interface: true = physical adapter, false = virtual/software adapter,
    /// null = no PnP record could be correlated. Only physical interfaces may drive recovery.
    /// </summary>
    public bool? IsPhysicalDevice { get; init; }

    public bool IsPresent { get; init; }

    public bool HasUsableIpv4 { get; init; }

    public bool HasIpv6 { get; init; }

    public bool HasDefaultGateway { get; init; }

    public int? InterfaceMetric { get; init; }

    public int? RouteMetric { get; init; }

    public bool IsDefaultRoute { get; init; }

    public IReadOnlyList<string> Ipv4Addresses { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Ipv6Addresses { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Ipv4Gateways { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();

    public string? PrimaryIpv4Address => Ipv4Addresses.Count > 0 ? Ipv4Addresses[0] : null;

    public string? PrimaryGateway => Ipv4Gateways.Count > 0 ? Ipv4Gateways[0] : null;

    public string? MacAddress { get; init; }

    public ulong SpeedBitsPerSecond { get; init; }

    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Per-interface connectivity probe bound to this interface's source address.</summary>
    public ConnectivityProbeReport? Probe { get; init; }

    public string MetricDescription =>
        InterfaceMetric is null && RouteMetric is null
            ? "n/a"
            : $"if={InterfaceMetric?.ToString() ?? "-"} route={RouteMetric?.ToString() ?? "-"}";

    public override string ToString() => $"{Name} [{Kind}] up={IsUp} ip={PrimaryIpv4Address ?? "-"}";
}

/// <summary>Wi-Fi Radio (the Windows-wide Wi-Fi switch) snapshot.</summary>
public sealed record WifiRadioSnapshot
{
    public RadioState State { get; init; } = RadioState.Unknown;

    public bool IsAccessAllowed { get; init; }

    public string? Name { get; init; }

    public bool? UserDenied { get; init; }

    public string? FailureReason { get; init; }

    public DateTimeOffset ObservedAtUtc { get; init; }

    public bool IsOn => State == RadioState.On;
}

/// <summary>Per-adapter state used by the decision engine.</summary>
public sealed record WifiAdapterRuntimeState
{
    public required Guid InterfaceGuid { get; init; }

    public required string Description { get; init; }

    public string? DeviceInstanceId { get; init; }

    public string? MacAddress { get; init; }

    public bool IsConnected { get; init; }

    public WifiConnectionInfo? Connection { get; init; }

    public AdapterScanSnapshot? LastScan { get; init; }

    public DateTimeOffset? LastConnectAttemptUtc { get; init; }

    public string? LastAttemptedProfile { get; init; }

    public string? LastFailure { get; init; }

    public int RecentConnectFailures { get; init; }

    public bool IsScanInProgress { get; init; }

    public DateTimeOffset? LastScanAttemptUtc { get; init; }

    public bool ProfileListKnown { get; init; }

    public IReadOnlyList<string> SavedProfiles { get; init; } = Array.Empty<string>();

    public string? CurrentSsid => Connection?.IsConnected == true ? Connection.Ssid : null;

    public int SignalQuality => Connection?.SignalQuality ?? 0;

    public override string ToString() =>
        $"{Description} {InterfaceGuid:N} connected={IsConnected} ssid={CurrentSsid ?? "-"}";
}

/// <summary>Windows Location permission situation. Native Wi-Fi scans can be blocked without it.</summary>
public sealed record LocationPermissionSnapshot
{
    public bool? AppLocationAllowed { get; init; }

    public bool? LocationServiceEnabled { get; init; }

    /// <summary>True when a Native Wi-Fi call returned ERROR_ACCESS_DENIED / wlan error 5.</summary>
    public bool ScanBlockedByPolicy { get; init; }

    public string? BlockedOperation { get; init; }

    public string? Detail { get; init; }

    public DateTimeOffset ObservedAtUtc { get; init; }

    public bool HasProblem => ScanBlockedByPolicy || AppLocationAllowed == false;
}
