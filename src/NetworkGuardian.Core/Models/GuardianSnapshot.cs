using NetworkGuardian.Core.Configuration;

namespace NetworkGuardian.Core.Models;

/// <summary>Coarse operational status used by the tray icon and the dashboard header.</summary>
public enum GuardianHealth
{
    Unknown = 0,
    Healthy,
    Recovering,
    Degraded,
    Error,
    Paused,
}

/// <summary>Snapshot of everything the UI renders plus the context the engine just evaluated.</summary>
public sealed record GuardianSnapshot
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required RecoveryState State { get; init; }

    public required GuardianHealth Health { get; init; }

    public required ConnectivityLevel Connectivity { get; init; }

    public required bool IsPaused { get; init; }

    public required bool IsAutomaticRecoveryEnabled { get; init; }

    public required ConnectivityProbeReport GlobalProbe { get; init; }

    public required WifiRadioSnapshot Radio { get; init; }

    public required LocationPermissionSnapshot Location { get; init; }

    public required IReadOnlyList<InterfaceRuntimeState> Interfaces { get; init; }

    public required IReadOnlyList<WifiAdapterRuntimeState> WifiAdapters { get; init; }

    public required IReadOnlyList<ManagedDevice> WifiDevices { get; init; }

    public required IReadOnlyList<ManagedDevice> EthernetDevices { get; init; }

    public required IReadOnlyList<DefaultRouteInfo> DefaultRoutes { get; init; }

    public string? ExpectedOutletInterfaceId { get; init; }

    public bool? OutletMatchesPolicy { get; init; }

    public DateTimeOffset? RouteObservedAtUtc { get; init; }

    public required IReadOnlyList<GuardianAction> PendingActions { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    public string? LastRecoveryAction { get; init; }

    public DateTimeOffset? LastRecoveryActionUtc { get; init; }

    public int ConsecutiveInternetFailures { get; init; }

    public DateTimeOffset? LastInternetSuccessUtc { get; init; }

    public DateTimeOffset? LastCampusAuthUtc { get; init; }

    public int CampusAuthRunCount { get; init; }

    public GuardianHealth ComputeHealth()
    {
        if (IsPaused)
        {
            return GuardianHealth.Paused;
        }

        if (GlobalProbe.IsOnline)
        {
            return State == RecoveryState.Healthy ? GuardianHealth.Healthy : GuardianHealth.Recovering;
        }

        return Connectivity switch
        {
            ConnectivityLevel.NoLink => GuardianHealth.Degraded,
            ConnectivityLevel.Unknown => GuardianHealth.Unknown,
            ConnectivityLevel.Online => GuardianHealth.Healthy,
            _ => GuardianHealth.Recovering,
        };
    }

    public static GuardianSnapshot Initial(GuardianConfig config, DateTimeOffset now) => new()
    {
        TimestampUtc = now,
        State = RecoveryState.Initializing,
        Health = GuardianHealth.Unknown,
        Connectivity = ConnectivityLevel.Unknown,
        IsPaused = false,
        IsAutomaticRecoveryEnabled = config.General.AutomaticRecovery,
        GlobalProbe = ConnectivityProbeReport.NotAttempted(now, "not-yet-run"),
        Radio = new WifiRadioSnapshot { ObservedAtUtc = now },
        Location = new LocationPermissionSnapshot { ObservedAtUtc = now },
        Interfaces = Array.Empty<InterfaceRuntimeState>(),
        WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
        WifiDevices = Array.Empty<ManagedDevice>(),
        EthernetDevices = Array.Empty<ManagedDevice>(),
        DefaultRoutes = Array.Empty<DefaultRouteInfo>(),
        PendingActions = Array.Empty<GuardianAction>(),
        Notes = Array.Empty<string>(),
    };
}
