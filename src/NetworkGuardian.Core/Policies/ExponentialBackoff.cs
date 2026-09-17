namespace NetworkGuardian.Core.Policies;

/// <summary>
/// Exponential backoff with an upper bound and optional deterministic jitter. Used for every
/// retryable operation so that a broken driver can never produce a hot retry loop.
/// </summary>
public sealed class ExponentialBackoff
{
    private readonly object _gate = new();
    private readonly Random _random;
    private readonly bool _deterministic;

    public ExponentialBackoff(
        string name,
        int baseSeconds,
        int maxSeconds,
        double factor = 2.0,
        double jitterRatio = 0.2,
        int? seed = null)
    {
        Name = name;
        BaseSeconds = Math.Max(1, baseSeconds);
        MaxSeconds = Math.Max(BaseSeconds, maxSeconds);
        Factor = factor <= 1.0 ? 2.0 : factor;
        JitterRatio = Math.Clamp(jitterRatio, 0, 0.9);
        _deterministic = seed.HasValue;
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public string Name { get; }

    public int BaseSeconds { get; }

    public int MaxSeconds { get; }

    public double Factor { get; }

    public double JitterRatio { get; }

    public int Attempts { get; private set; }

    /// <summary>Returns the delay to apply before attempt number <paramref name="attempt"/> (0 based).</summary>
    public TimeSpan DelayFor(int attempt)
    {
        var exponent = Math.Clamp(attempt, 0, 16);
        var raw = BaseSeconds * Math.Pow(Factor, exponent);
        var capped = Math.Min(raw, MaxSeconds);

        if (JitterRatio > 0)
        {
            var jitter = capped * JitterRatio * (_random.NextDouble() * 2 - 1);
            capped = Math.Max(1, capped + jitter);
        }

        return TimeSpan.FromSeconds(capped);
    }

    /// <summary>Advances the internal counter and returns the delay for the upcoming retry.</summary>
    public TimeSpan Next()
    {
        lock (_gate)
        {
            var delay = DelayFor(Attempts);
            Attempts++;
            return delay;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Attempts = 0;
        }
    }

    public bool IsDeterministic => _deterministic;
}
