namespace NetworkGuardian.Core.Policies;

/// <summary>Abstracts the wall clock so policies and tests can share the exact same code paths.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>An <see cref="IClock"/> whose value can be advanced by tests.</summary>
public sealed class ManualClock : IClock
{
    private DateTimeOffset _now;

    public ManualClock(DateTimeOffset start)
    {
        _now = start;
    }

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);

    public void Set(DateTimeOffset value) => _now = value;
}
