using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Configuration;

namespace NetworkGuardian.Core.Policies;

/// <summary>Asymmetric debounce: fail over quickly, but require sustained proof before failback.</summary>
public sealed class InterfaceProbeStability
{
    private DateTimeOffset? _recoveryStartedUtc;

    public bool HasVerdict { get; private set; }

    public bool StableOnline { get; private set; }

    public int ConsecutiveSuccesses { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public ConnectivityProbeReport Apply(
        ConnectivityProbeReport report, RecoverySettings settings, DateTimeOffset now,
        bool failImmediately = false) => Apply(report,
            failureThreshold: report.IsAuthoritative || failImmediately ? 1 : 2,
            recoveryThreshold: settings.InternetRecoveryThreshold,
            recoveryHold: TimeSpan.FromSeconds(settings.InterfaceRecoveryHoldSeconds), now: now);

    public ConnectivityProbeReport Apply(
        ConnectivityProbeReport report,
        int failureThreshold,
        int recoveryThreshold,
        TimeSpan recoveryHold,
        DateTimeOffset now)
    {
        if (report.IsOnline)
        {
            ConsecutiveSuccesses++;
            ConsecutiveFailures = 0;

            if (!HasVerdict)
            {
                // Do not delay initial startup when there is no previous failure to recover from.
                StableOnline = true;
            }
            else if (!StableOnline)
            {
                _recoveryStartedUtc ??= now;
                if (ConsecutiveSuccesses >= Math.Max(1, recoveryThreshold) &&
                    now - _recoveryStartedUtc.Value >= recoveryHold)
                {
                    StableOnline = true;
                    _recoveryStartedUtc = null;
                }
            }
        }
        else
        {
            ConsecutiveFailures++;
            ConsecutiveSuccesses = 0;
            _recoveryStartedUtc = null;
            if (report.Reachability == InternetReachability.CaptivePortal ||
                ConsecutiveFailures >= Math.Max(1, failureThreshold) || !HasVerdict)
            {
                StableOnline = false;
            }
        }

        HasVerdict = true;
        return report with
        {
            StableOnline = StableOnline,
            ConsecutiveSuccesses = ConsecutiveSuccesses,
            ConsecutiveFailures = ConsecutiveFailures,
        };
    }
}
