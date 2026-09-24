using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Abstractions;

/// <summary>Adapter identity plus its current WLAN state, as reported by <c>WlanEnumInterfaces</c>.</summary>
public sealed record WifiAdapterInfo
{
    public required Guid InterfaceGuid { get; init; }

    public required string Description { get; init; }

    public WifiConnectionState State { get; init; }

    public string? DeviceInstanceId { get; init; }

    public string? MacAddress { get; init; }
}

/// <summary>Uniform result for Native Wi-Fi operations.</summary>
public sealed record WlanOperationResult
{
    public required bool Success { get; init; }

    public string? Failure { get; init; }

    public int? ErrorCode { get; init; }

    /// <summary>True when the call failed with ERROR_ACCESS_DENIED / WLAN error 5.</summary>
    public bool AccessDenied { get; init; }

    /// <summary>True when the failure looks like a Windows Location privacy restriction.</summary>
    public bool LocationRestricted { get; init; }

    public static WlanOperationResult Ok() => new() { Success = true };

    public static WlanOperationResult Fail(string failure, int? code = null, bool accessDenied = false, bool locationRestricted = false) =>
        new()
        {
            Success = false,
            Failure = failure,
            ErrorCode = code,
            AccessDenied = accessDenied,
            LocationRestricted = locationRestricted,
        };
}

public sealed record WlanNotificationEvent
{
    public Guid? InterfaceGuid { get; init; }

    public required string Code { get; init; }

    public required string Description { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    public override string ToString() => $"{Code} ({InterfaceGuid?.ToString("N") ?? "global"}) {Description}";
}

public sealed record RadioOperationResult
{
    public required bool Success { get; init; }

    public bool StateChanged { get; init; }

    public bool AccessDenied { get; init; }

    public string? Failure { get; init; }
}

/// <summary>
/// Controls the Windows-wide Wi-Fi switch through Native Wi-Fi
/// (<c>wlan_intf_opcode_radio_state</c>).
/// </summary>
public interface IWifiRadioController
{
    Task<WifiRadioSnapshot> GetAsync(CancellationToken cancellationToken);

    Task<RadioOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);

    event EventHandler<WifiRadioSnapshot>? StateChanged;
}

/// <summary>Native Wi-Fi (wlanapi.dll) surface used by the guardian.</summary>
public interface INativeWifiService : IDisposable
{
    IReadOnlyList<WifiAdapterInfo> GetAdapters();

    /// <summary>Re-runs <c>WlanEnumInterfaces</c>. Returns the number of adapters found.</summary>
    int RefreshAdapters();

    AdapterScanSnapshot? GetLastScan(Guid interfaceGuid);

    bool IsScanInProgress(Guid interfaceGuid);

    Task<AdapterScanSnapshot> RequestScanAsync(Guid interfaceGuid, bool force, TimeSpan timeout, CancellationToken cancellationToken);

    WifiConnectionInfo? GetConnection(Guid interfaceGuid);

    IReadOnlyList<string> GetProfileNames(Guid interfaceGuid);

    Task<WlanOperationResult> ConnectAsync(Guid interfaceGuid, string profileName, string? bssid, CancellationToken cancellationToken);

    Task<WlanOperationResult> DisconnectAsync(Guid interfaceGuid, CancellationToken cancellationToken);

    event EventHandler<WlanNotificationEvent>? NotificationReceived;

    LocationPermissionSnapshot LocationPermission { get; }
}

public sealed record ProbeRequest
{
    public required IReadOnlyList<ProbeEndpointSettings> Endpoints { get; init; }

    public required ProbeSettings Settings { get; init; }

    /// <summary>Local IPv4 address to bind to, enabling per-interface probing.</summary>
    public string? SourceAddress { get; init; }

    /// <summary>Windows IPv4 interface index used in addition to source-address binding.</summary>
    public uint? InterfaceIndex { get; init; }

    public string? InterfaceId { get; init; }

    public Guid? AdapterGuid { get; init; }

    /// <summary>DNS servers assigned to the selected interface, used for interface-bound queries.</summary>
    public IReadOnlyList<string> DnsServerAddresses { get; init; } = Array.Empty<string>();

    /// <summary>Optional per-request cap used to keep multi-interface checks lightweight.</summary>
    public int? MaxConcurrencyOverride { get; init; }

    /// <summary>Gateway used for the optional ICMP probe.</summary>
    public string? GatewayAddress { get; init; }
}

public interface IConnectivityProbe
{
    Task<ConnectivityProbeReport> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken);
}

/// <summary>IP Helper based interface and route inventory.</summary>
public interface INetworkInterfaceProvider
{
    IReadOnlyList<InterfaceRuntimeState> GetInterfaces();

    IReadOnlyList<DefaultRouteInfo> GetDefaultRoutes();

    /// <summary>Applies the configured interface metrics. Disabled by default in configuration.</summary>
    Task<IReadOnlyList<string>> ApplyInterfaceMetricsAsync(GuardianConfig config, CancellationToken cancellationToken);
}

/// <summary>PnP device inventory and privileged device control.</summary>
public interface IDeviceManager
{
    Task<IReadOnlyList<ManagedDevice>> EnumerateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Enables a disabled device. Implementations must run the elevated helper when required and
    /// must refuse devices that the classifier does not consider physical.
    /// </summary>
    Task<DeviceOperationResult> EnableAsync(string deviceInstanceId, CancellationToken cancellationToken);

    Task<DeviceOperationResult> DisableAsync(string deviceInstanceId, CancellationToken cancellationToken);
}

public sealed record CommandExecutionResult
{
    public required bool Started { get; init; }

    public bool AlreadyRunning { get; init; }

    public bool TimedOut { get; init; }

    public bool Killed { get; init; }

    public bool Elevated { get; init; }

    public int? ExitCode { get; init; }

    public string? ExecutablePath { get; init; }

    public string? Failure { get; init; }

    public TimeSpan Duration { get; init; }

    public bool Success => Started && !TimedOut && (ExitCode is null or 0 or 3010);

    public override string ToString() =>
        $"started={Started} alreadyRunning={AlreadyRunning} exit={ExitCode?.ToString() ?? "-"} " +
        $"timedOut={TimedOut} elevated={Elevated} {Failure}";
}

public interface ICommandRunner
{
    Task<CommandExecutionResult> RunAsync(
        CommandDefinition definition,
        string reason,
        CancellationToken cancellationToken);

    bool IsRunning(string executablePath);

    IReadOnlyList<string> RunningInstances { get; }
}

public interface ILocationPermissionService
{
    Task<LocationPermissionSnapshot> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Persisted configuration access used by services that need to re-read settings.</summary>
public interface IConfigStore
{
    GuardianConfig Current { get; }

    string ConfigPath { get; }

    Task<GuardianConfig> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(GuardianConfig config, CancellationToken cancellationToken);
}
