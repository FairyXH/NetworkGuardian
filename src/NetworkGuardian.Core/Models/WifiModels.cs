namespace NetworkGuardian.Core.Models;

/// <summary>
/// A single BSS (access point radio) observed by a scan. One SSID may produce several entries
/// when the AP is a multi-band mesh; each BSSID is kept separately.
/// </summary>
public sealed record WifiBssEntry
{
    public required string Ssid { get; init; }

    public required string Bssid { get; init; }

    /// <summary>Vendor reported link quality in percent (0-100).</summary>
    public int SignalQuality { get; init; }

    /// <summary>RSSI in dBm when the driver reports it; 0 when unavailable.</summary>
    public int Rssi { get; init; }

    /// <summary>Channel centre frequency in kHz as reported by Native Wi-Fi.</summary>
    public int FrequencyKhz { get; init; }

    public int Channel { get; init; }

    public NetworkBand Band { get; init; }

    public WifiBssType BssType { get; init; } = WifiBssType.Infrastructure;

    public DateTimeOffset ObservedAtUtc { get; init; }

    public override string ToString() => $"{Ssid} ({Bssid}) {SignalQuality}%";
}

/// <summary>
/// Aggregated view of one visible SSID on one adapter. Built from <see cref="WifiBssEntry"/>
/// plus the profile list so that policy code never has to re-query the OS.
/// </summary>
public sealed record ScannedNetwork
{
    public required Guid InterfaceGuid { get; init; }

    public required string Ssid { get; init; }

    /// <summary>Best signal quality across all BSSIDs of this SSID.</summary>
    public int SignalQuality { get; init; }

    public int Rssi { get; init; }

    public int FrequencyKhz { get; init; }

    public int Channel { get; init; }

    public NetworkBand Band { get; init; }

    public WifiSecurity Security { get; init; }

    public WifiBssType BssType { get; init; }

    /// <summary>Name of the saved profile that matches this SSID, if any.</summary>
    public string? ProfileName { get; init; }

    /// <summary>True when a saved profile exists; only such networks may be auto-connected.</summary>
    public bool HasProfile { get; init; }

    public bool Connectable { get; init; }

    /// <summary>True when this adapter is currently associated with exactly this SSID.</summary>
    public bool IsCurrentConnection { get; init; }

    public DateTimeOffset ObservedAtUtc { get; init; }

    public IReadOnlyList<WifiBssEntry> BssEntries { get; init; } = Array.Empty<WifiBssEntry>();

    public string Key => SnapshotKey(InterfaceGuid, Ssid);

    public static string SnapshotKey(Guid interfaceGuid, string ssid) => $"{interfaceGuid:N}|{ssid}";
}

/// <summary>Result of one scan request issued against one physical adapter.</summary>
public sealed record AdapterScanSnapshot
{
    public required Guid InterfaceGuid { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>True when a scan-complete notification (or an accepted synchronous result) arrived.</summary>
    public bool Completed { get; init; }

    public bool Failed { get; init; }

    public string? FailureReason { get; init; }

    public IReadOnlyList<ScannedNetwork> Networks { get; init; } = Array.Empty<ScannedNetwork>();

    public TimeSpan Duration => CompletedAtUtc is { } c ? c - StartedAtUtc : TimeSpan.Zero;

    public static AdapterScanSnapshot Empty(Guid guid, DateTimeOffset now) => new()
    {
        InterfaceGuid = guid,
        StartedAtUtc = now,
        CompletedAtUtc = now,
        Completed = false,
    };
}

/// <summary>Live connection information for one adapter.</summary>
public sealed record WifiConnectionInfo
{
    public required Guid InterfaceGuid { get; init; }

    public required WifiConnectionState State { get; init; }

    public string Ssid { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public string Bssid { get; init; } = string.Empty;

    public int SignalQuality { get; init; }

    public int Rssi { get; init; }

    public int FrequencyKhz { get; init; }

    public int Channel { get; init; }

    public NetworkBand Band { get; init; }

    public WifiSecurity Security { get; init; }

    public DateTimeOffset ObservedAtUtc { get; init; }

    public bool IsConnected => State == WifiConnectionState.Connected;
}

/// <summary>A candidate the connector may pick when an adapter has no working connection.</summary>
public sealed record WifiCandidate
{
    public required Guid InterfaceGuid { get; init; }

    public required string Ssid { get; init; }

    public required string ProfileName { get; init; }

    public int SignalQuality { get; init; }

    public int Rssi { get; init; }

    public NetworkBand Band { get; init; }

    public WifiSecurity Security { get; init; }

    public string? PreferredBssid { get; init; }

    /// <summary>Ranking score; higher is better. Computed by <c>CandidateSelector</c>.</summary>
    public double Score { get; init; }

    /// <summary>Human readable explanation of how <see cref="Score"/> was produced (for logs/UI).</summary>
    public string ScoreReason { get; init; } = string.Empty;

    public DateTimeOffset? LastConnectedUtc { get; init; }

    public int RecentConnectFailures { get; init; }

    /// <summary>True when the network requires 802.1X/EAP authentication.</summary>
    public bool RequiresEap { get; init; }

    /// <summary>
    /// True when the candidate was selected because the built-in library holds the account for it; the
    /// host then writes the profile and the credentials from the library before connecting.
    /// </summary>
    public bool UsesLibraryCredential { get; init; }
}
