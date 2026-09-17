namespace NetworkGuardian.Core.Models;

/// <summary>Result of one probe attempt against one endpoint.</summary>
public sealed record ProbeAttemptResult
{
    public required string EndpointName { get; init; }

    public required ProbeKind Kind { get; init; }

    public required string Target { get; init; }

    /// <summary>Local source address the socket was bound to, when the probe was interface bound.</summary>
    public string? SourceAddress { get; init; }

    public required ProbeOutcome Outcome { get; init; }

    public int? HttpStatusCode { get; init; }

    public string? RedirectLocation { get; init; }

    public string? Detail { get; init; }

    public TimeSpan Duration { get; init; }

    public bool IsSuccess => Outcome == ProbeOutcome.Success;

    public override string ToString() =>
        $"{EndpointName} [{Kind}] {(IsSuccess ? "OK" : Outcome.ToString())} {Duration.TotalMilliseconds:F0}ms";
}

/// <summary>
/// Aggregated probe result. A single failing endpoint must never mark the whole Internet as down,
/// so success is decided by a threshold over the configured endpoint set.
/// </summary>
public sealed record ConnectivityProbeReport
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public string? SourceAddress { get; init; }

    public string? SourceInterfaceId { get; init; }

    public bool IsOnline { get; init; }

    public bool CaptivePortalSuspected { get; init; }

    public string? CaptivePortalInterceptedBy { get; init; }

    public int SuccessCount { get; init; }

    public int AttemptCount { get; init; }

    public int RequiredSuccessCount { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<ProbeAttemptResult> Attempts { get; init; } = Array.Empty<ProbeAttemptResult>();

    public string? FirstFailureDetail =>
        Attempts.FirstOrDefault(a => !a.IsSuccess)?.Detail;

    public static ConnectivityProbeReport NotAttempted(DateTimeOffset now, string reason) => new()
    {
        TimestampUtc = now,
        IsOnline = false,
        Attempts = Array.Empty<ProbeAttemptResult>(),
        RequiredSuccessCount = 1,
        CaptivePortalInterceptedBy = reason,
    };

    public string Summary =>
        $"{(IsOnline ? "online" : "offline")} {SuccessCount}/{AttemptCount}" +
        (CaptivePortalSuspected ? $" captive-portal via {CaptivePortalInterceptedBy}" : string.Empty);
}

/// <summary>Route information read through IP Helper API.</summary>
public sealed record DefaultRouteInfo
{
    public required int AddressFamily { get; init; }

    public required uint InterfaceIndex { get; init; }

    public uint? InterfaceLuid { get; init; }

    public required string NextHop { get; init; }

    public int? RouteMetric { get; init; }

    public int? InterfaceMetric { get; init; }

    public int? EffectiveMetric => RouteMetric is null && InterfaceMetric is null
        ? null
        : (RouteMetric ?? 0) + (InterfaceMetric ?? 0);

    public string? InterfaceAlias { get; init; }

    public override string ToString() =>
        $"af={AddressFamily} if={InterfaceLuid ?? InterfaceIndex} nh={NextHop} metric={EffectiveMetric}";
}

/// <summary>Everything the UI needs for the Ethernet view.</summary>
public sealed record EthernetStatus
{
    public IReadOnlyList<InterfaceRuntimeState> Interfaces { get; init; } = Array.Empty<InterfaceRuntimeState>();

    public bool AnyUp { get; init; }

    public bool AnyWithInternet { get; init; }

    public bool LinkUpButNoInternet { get; init; }
}
