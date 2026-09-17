using NetworkGuardian.Core.Configuration;

namespace NetworkGuardian.Core.Policies;

/// <summary>Per-adapter bookkeeping owned by the decision engine.</summary>
internal sealed class AdapterPolicyState
{
    public AdapterPolicyState(Guid interfaceGuid, RecoverySettings recovery, GeneralSettings general)
    {
        InterfaceGuid = interfaceGuid;
        Connectivity = new FailureTracker($"wifi[{interfaceGuid:N}]", recovery.WifiFailureThreshold, recovery.InternetRecoveryThreshold);
        ScanLimiter = new SlidingWindowRateLimiter(
            $"scan[{interfaceGuid:N}]",
            maxRunsPerHour: 120,
            minInterval: TimeSpan.FromSeconds(general.MinimumScanIntervalSeconds),
            maxConsecutiveRuns: 40);
        ConnectLimiter = new SlidingWindowRateLimiter(
            $"connect[{interfaceGuid:N}]",
            maxRunsPerHour: 40,
            minInterval: TimeSpan.FromSeconds(Math.Max(10, recovery.CooldownSeconds / 3)),
            maxConsecutiveRuns: recovery.MaxConnectAttemptsPerRound);
    }

    public Guid InterfaceGuid { get; }

    public FailureTracker Connectivity { get; }

    public SlidingWindowRateLimiter ScanLimiter { get; }

    public SlidingWindowRateLimiter ConnectLimiter { get; }

    public DateTimeOffset? DisconnectedSinceUtc { get; set; }

    public DateTimeOffset? LastSuccessfulConnectionUtc { get; set; }

    public DateTimeOffset? LastDisconnectActionUtc { get; set; }

    public DateTimeOffset? PendingConnectUtc { get; set; }

    public string? PendingConnectProfile { get; set; }

    public string? LastConnectedProfile { get; set; }

    public string? LastKnownSsid { get; set; }

    public DateTimeOffset? LastScanRequestUtc { get; set; }

    public void ApplyConfig(RecoverySettings recovery, GeneralSettings general)
    {
        Connectivity.FailureThreshold = Math.Max(1, recovery.WifiFailureThreshold);
        Connectivity.RecoveryThreshold = Math.Max(1, recovery.InternetRecoveryThreshold);
        ScanLimiter.MinInterval = TimeSpan.FromSeconds(Math.Max(5, general.MinimumScanIntervalSeconds));
        ConnectLimiter.MinInterval = TimeSpan.FromSeconds(Math.Max(10, recovery.CooldownSeconds / 3));
        ConnectLimiter.MaxConsecutiveRuns = Math.Max(1, recovery.MaxConnectAttemptsPerRound);
    }
}
