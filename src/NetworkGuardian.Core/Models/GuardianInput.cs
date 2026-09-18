using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Models;

/// <summary>Everything the decision engine needs for one evaluation round.</summary>
public sealed record GuardianInput
{
    public required DateTimeOffset Now { get; init; }

    public required Configuration.GuardianConfig Config { get; init; }

    public required ConnectivityProbeReport GlobalProbe { get; init; }

    public required IReadOnlyList<InterfaceRuntimeState> Interfaces { get; init; }

    public required IReadOnlyList<WifiAdapterRuntimeState> WifiAdapters { get; init; }

    /// <summary>All managed devices (physical and virtual) so the engine can find disabled radios.</summary>
    public required IReadOnlyList<ManagedDevice> Devices { get; init; }

    public required WifiRadioSnapshot Radio { get; init; }

    public bool IsPaused { get; init; }

    public bool ManualScanRequested { get; init; }

    public LocationPermissionSnapshot Location { get; init; } = new() { ObservedAtUtc = default };

    /// <summary>
    /// Which SSIDs the built-in wireless network library can authenticate for. The engine is pure, so it
    /// sees this summary (SSIDs only) instead of the library itself, which holds the passwords.
    /// </summary>
    public WifiEapCatalog EapCatalog { get; init; } = WifiEapCatalog.Empty;
}
