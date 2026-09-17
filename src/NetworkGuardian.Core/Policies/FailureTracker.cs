namespace NetworkGuardian.Core.Policies;

/// <summary>
/// Debounces flapping signals. A single dropped probe must never trigger a recovery action, and a
/// single good probe must not immediately declare victory, so both directions need hysteresis.
/// </summary>
public sealed class FailureTracker
{
    private readonly object _gate = new();

    public FailureTracker(string name, int failureThreshold, int recoveryThreshold)
    {
        Name = name;
        FailureThreshold = Math.Max(1, failureThreshold);
        RecoveryThreshold = Math.Max(1, recoveryThreshold);
    }

    public string Name { get; }

    public int FailureThreshold { get; set; }

    public int RecoveryThreshold { get; set; }

    public int ConsecutiveFailures { get; private set; }

    public int ConsecutiveSuccesses { get; private set; }

    public bool IsFailing { get; private set; }

    public DateTimeOffset? FirstFailureUtc { get; private set; }

    public DateTimeOffset? LastSuccessUtc { get; private set; }

    public DateTimeOffset? LastChangeUtc { get; private set; }

    /// <summary>Total failures observed since the last full reset (used for backoff escalation).</summary>
    public int TotalFailures { get; private set; }

    /// <summary>Records a successful observation. Returns true when the tracker transitions to healthy.</summary>
    public bool RecordSuccess(DateTimeOffset now)
    {
        lock (_gate)
        {
            LastSuccessUtc = now;
            ConsecutiveSuccesses++;
            ConsecutiveFailures = 0;

            if (ConsecutiveSuccesses >= RecoveryThreshold && IsFailing)
            {
                IsFailing = false;
                FirstFailureUtc = null;
                LastChangeUtc = now;
                TotalFailures = 0;
                return true;
            }

            if (!IsFailing && ConsecutiveSuccesses >= RecoveryThreshold)
            {
                TotalFailures = 0;
            }

            return false;
        }
    }

    /// <summary>
    /// Records a failed observation. Returns true exactly once, when the failure threshold is
    /// reached and the tracker transitions into the failing state.
    /// </summary>
    public bool RecordFailure(DateTimeOffset now)
    {
        lock (_gate)
        {
            ConsecutiveFailures++;
            ConsecutiveSuccesses = 0;
            TotalFailures++;
            FirstFailureUtc ??= now;

            if (!IsFailing && ConsecutiveFailures >= FailureThreshold)
            {
                IsFailing = true;
                LastChangeUtc = now;
                return true;
            }

            return false;
        }
    }

    /// <summary>Forces the tracker back to the healthy state without counting a success.</summary>
    public void Reset(DateTimeOffset now)
    {
        lock (_gate)
        {
            IsFailing = false;
            ConsecutiveFailures = 0;
            ConsecutiveSuccesses = 0;
            FirstFailureUtc = null;
            TotalFailures = 0;
            LastChangeUtc = now;
        }
    }

    public TimeSpan? FailureDuration(DateTimeOffset now) =>
        IsFailing && FirstFailureUtc is { } start ? now - start : null;

    public override string ToString() =>
        $"{Name}: fail={ConsecutiveFailures}/{FailureThreshold} ok={ConsecutiveSuccesses}/{RecoveryThreshold} failing={IsFailing}";
}
