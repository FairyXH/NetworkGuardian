namespace NetworkGuardian.Core.Models;

/// <summary>
/// An intent produced by the decision engine. Actions are pure data; the host is responsible for
/// scheduling and executing them. This keeps the recovery policy fully unit testable.
/// </summary>
public abstract record GuardianAction
{
    public required string Reason { get; init; }

    /// <summary>Earliest moment at which this action may run.</summary>
    public DateTimeOffset? NotBeforeUtc { get; init; }

    public abstract string Describe();
}

public sealed record NoAction : GuardianAction
{
    public override string Describe() => $"NoAction({Reason})";
}

public sealed record WaitAction : GuardianAction
{
    public required TimeSpan Delay { get; init; }

    public override string Describe() => $"Wait({Delay.TotalSeconds:F0}s, {Reason})";
}

public sealed record EnableWifiRadioAction : GuardianAction
{
    public required bool Force { get; init; }

    public override string Describe() => $"EnableWifiRadio(force={Force}, {Reason})";
}

public sealed record EnableWifiDeviceAction : GuardianAction
{
    public required string DeviceInstanceId { get; init; }

    public required string FriendlyName { get; init; }

    public override string Describe() => $"EnableWifiDevice({DeviceInstanceId}, {Reason})";
}

/// <summary>
/// Restarts a physically present Wi-Fi device that is not running because its driver failed to start
/// (problem code 10/43/...). This is the software equivalent of "Disable device" + "Enable device" in
/// Device Manager and is the only PnP action that has a chance of reviving such a device.
/// </summary>
public sealed record RestartWifiDeviceAction : GuardianAction
{
    public required string DeviceInstanceId { get; init; }

    public required string FriendlyName { get; init; }

    public required uint ProblemCode { get; init; }

    public override string Describe() =>
        $"RestartWifiDevice({DeviceInstanceId}, problemCode={ProblemCode}, {Reason})";
}

public sealed record ScanAdapterAction : GuardianAction
{
    public required Guid InterfaceGuid { get; init; }

    public required bool Force { get; init; }

    public override string Describe() => $"Scan({InterfaceGuid:N}, force={Force}, {Reason})";
}

public sealed record ConnectWifiAction : GuardianAction
{
    public required Guid InterfaceGuid { get; init; }

    public required string ProfileName { get; init; }

    public required string Ssid { get; init; }

    public string? Bssid { get; init; }

    /// <summary>True when the network requires 802.1X/EAP authentication.</summary>
    public bool RequiresEap { get; init; }

    /// <summary>
    /// True when the account comes from the built-in wireless network library, i.e. the host has to write
    /// the profile and the credentials to the adapter before connecting.
    /// </summary>
    public bool UsesLibraryCredential { get; init; }

    public override string Describe() => $"Connect({InterfaceGuid:N} -> {ProfileName}/{Ssid}, {Reason})";
}

public sealed record DisconnectWifiAction : GuardianAction
{
    public required Guid InterfaceGuid { get; init; }

    public required string Ssid { get; init; }

    public override string Describe() => $"Disconnect({InterfaceGuid:N} <- {Ssid}, {Reason})";
}

public sealed record ProbeConnectivityAction : GuardianAction
{
    public string? InterfaceId { get; init; }

    public bool Full { get; init; } = true;

    public override string Describe() =>
        $"ProbeConnectivity({(InterfaceId is null ? "global" : InterfaceId)}, {Reason})";
}

/// <summary>
/// Requests the host to run an external program: either the configured campus authenticator or a
/// user defined "offline" command. NetworkGuardian itself never supplies credentials.
/// </summary>
public sealed record RunExternalCommandAction : GuardianAction
{
    public required string CommandId { get; init; }

    public required string CommandName { get; init; }

    public required string ExecutablePath { get; init; }

    public required Configuration.CommandKind Kind { get; init; }

    public bool IsCampusAuth { get; init; }

    public override string Describe() =>
        $"RunCommand({(IsCampusAuth ? "campusAuth" : "offline")}:{CommandId} {Kind} {ExecutablePath}, {Reason})";
}

public sealed record RefreshAdaptersAction : GuardianAction
{
    public override string Describe() => $"RefreshAdapters({Reason})";
}

/// <summary>Requests the host to re-apply interface metrics (disabled by default).</summary>
public sealed record ApplyInterfaceMetricsAction : GuardianAction
{
    public override string Describe() => $"ApplyInterfaceMetrics({Reason})";
}

/// <summary>Full plan returned by <c>GuardianDecisionEngine.Evaluate</c>.</summary>
public sealed record GuardianDecision
{
    public required RecoveryState State { get; init; }

    public required IReadOnlyList<GuardianAction> Actions { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    public required ConnectivityLevel Connectivity { get; init; }

    public required DateTimeOffset EvaluatedAtUtc { get; init; }

    public static GuardianDecision Empty(DateTimeOffset now) => new()
    {
        State = RecoveryState.Initializing,
        Actions = Array.Empty<GuardianAction>(),
        Notes = Array.Empty<string>(),
        Connectivity = ConnectivityLevel.Unknown,
        EvaluatedAtUtc = now,
    };
}
