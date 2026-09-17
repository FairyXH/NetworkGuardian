namespace NetworkGuardian.Core.Policies;

/// <summary>
/// Sliding window rate limiter that answers "may this operation run right now?".
/// It is the single mechanism preventing scan storms, reconnect storms and campus-auth storms.
/// </summary>
public sealed class SlidingWindowRateLimiter
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _runs = new();

    public SlidingWindowRateLimiter(
        string name,
        int maxRunsPerHour,
        TimeSpan minInterval,
        int maxConsecutiveRuns = int.MaxValue)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "operation" : name;
        MaxRunsPerHour = Math.Max(1, maxRunsPerHour);
        MinInterval = minInterval < TimeSpan.Zero ? TimeSpan.Zero : minInterval;
        MaxConsecutiveRuns = Math.Max(1, maxConsecutiveRuns);
    }

    public string Name { get; }

    public int MaxRunsPerHour { get; set; }

    public TimeSpan MinInterval { get; set; }

    /// <summary>Runs permitted without an intervening success signal.</summary>
    public int MaxConsecutiveRuns { get; set; }

    public int ConsecutiveRuns { get; private set; }

    public DateTimeOffset? LastRunUtc { get; private set; }

    /// <summary>Number of runs recorded inside the trailing one hour window.</summary>
    public int RunsInWindow(DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            return _runs.Count;
        }
    }

    public bool TryAcquire(DateTimeOffset now, out TimeSpan retryAfter, out string reason)
    {
        lock (_gate)
        {
            Prune(now);

            if (LastRunUtc is { } last)
            {
                var elapsed = now - last;
                if (elapsed < MinInterval)
                {
                    retryAfter = MinInterval - elapsed;
                    reason = $"{Name}: minimum interval {MinInterval.TotalSeconds:F0}s not elapsed " +
                             $"(last run {elapsed.TotalSeconds:F0}s ago)";
                    return false;
                }
            }

            if (_runs.Count >= MaxRunsPerHour)
            {
                var oldest = _runs.Peek();
                retryAfter = TimeSpan.FromHours(1) - (now - oldest);
                reason = $"{Name}: hourly budget {MaxRunsPerHour} exhausted ({_runs.Count} runs)";
                return false;
            }

            if (ConsecutiveRuns >= MaxConsecutiveRuns)
            {
                retryAfter = TimeSpan.FromMinutes(5);
                reason = $"{Name}: {ConsecutiveRuns} consecutive runs without success " +
                         $"(limit {MaxConsecutiveRuns}); backing off";
                return false;
            }

            retryAfter = TimeSpan.Zero;
            reason = string.Empty;
            return true;
        }
    }

    /// <summary>Records that the operation actually ran.</summary>
    public void RecordRun(DateTimeOffset now)
    {
        lock (_gate)
        {
            _runs.Enqueue(now);
            LastRunUtc = now;
            ConsecutiveRuns++;
        }
    }

    /// <summary>Clears the consecutive counter after a proven success (e.g. Internet restored).</summary>
    public void NotifySuccess()
    {
        lock (_gate)
        {
            ConsecutiveRuns = 0;
        }
    }

    /// <summary>Full reset, used when the user changes the configuration or resumes from pause.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _runs.Clear();
            LastRunUtc = null;
            ConsecutiveRuns = 0;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        while (_runs.Count > 0 && _runs.Peek() < cutoff)
        {
            _runs.Dequeue();
        }
    }

    public override string ToString() =>
        $"{Name}: last={LastRunUtc:HH:mm:ss} consecutive={ConsecutiveRuns}/{MaxConsecutiveRuns}";
}
