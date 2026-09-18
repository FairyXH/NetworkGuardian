namespace NetworkGuardian.Core.Policies;

/// <summary>Per-adapter EAP retry status, used for logging and the UI.</summary>
public sealed record EapRetryStatus(
    Guid InterfaceGuid,
    string Ssid,
    int Failures,
    bool Abandoned,
    DateTimeOffset? LastFailureUtc,
    string? LastReason);

/// <summary>Result of recording one failed EAP connect attempt.</summary>
public readonly record struct EapRetryOutcome(
    int Failures,
    int AttemptsRemaining,
    bool AbandonedNow,
    bool AlreadyAbandoned)
{
    public bool Success => false;
}

/// <summary>
/// Tracks how often an 802.1X/EAP network failed to authenticate on one adapter.
/// </summary>
/// <remarks>
/// The behaviour requested for campus networks: use the credentials from the built-in library, retry a
/// bounded number of times (default 5), and then give that SSID up <b>for this run only</b>, so the
/// recovery engine stops hammering an access point that rejects the account. Nothing is persisted:
/// the next program start tries again, which is exactly what a user expects after fixing a password
/// or after the account was unlocked.
/// </remarks>
public sealed class EapConnectRetryPolicy
{
    public const int DefaultMaxAttempts = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _maxAttempts;

    public EapConnectRetryPolicy(int maxAttempts = DefaultMaxAttempts)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    /// <summary>Run attempts allowed per (adapter, SSID) before the network is dropped for this session.</summary>
    public int MaxAttempts
    {
        get => _maxAttempts;
        set => _maxAttempts = Math.Max(1, value);
    }

    public int FailureCount(Guid interfaceGuid, string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return 0;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(Key(interfaceGuid, ssid), out var entry) ? entry.Failures : 0;
        }
    }

    /// <summary>True when this adapter must not try that SSID again until the program restarts.</summary>
    public bool IsAbandoned(Guid interfaceGuid, string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return false;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(Key(interfaceGuid, ssid), out var entry) && entry.Abandoned;
        }
    }

    /// <summary>Records a failed attempt and reports whether that failure exhausted the budget.</summary>
    public EapRetryOutcome RecordFailure(Guid interfaceGuid, string ssid, DateTimeOffset now, string? reason)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return new EapRetryOutcome(0, MaxAttempts, false, false);
        }

        var key = Key(interfaceGuid, ssid);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
            }

            var alreadyAbandoned = entry.Abandoned;

            if (!alreadyAbandoned)
            {
                entry.Failures++;
                entry.LastFailureUtc = now;
                entry.LastReason = reason;
                entry.Abandoned = entry.Failures >= _maxAttempts;
            }

            _entries[key] = entry;

            var remaining = Math.Max(0, _maxAttempts - entry.Failures);
            return new EapRetryOutcome(entry.Failures, remaining, entry.Abandoned && !alreadyAbandoned, alreadyAbandoned);
        }
    }

    /// <summary>A successful authentication clears the counter for that network.</summary>
    public void RecordSuccess(Guid interfaceGuid, string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return;
        }

        lock (_gate)
        {
            _entries.Remove(Key(interfaceGuid, ssid));
        }
    }

    /// <summary>Clears one entry, e.g. after the user corrected the password in the library.</summary>
    public bool Forget(Guid interfaceGuid, string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return false;
        }

        lock (_gate)
        {
            return _entries.Remove(Key(interfaceGuid, ssid));
        }
    }

    /// <summary>Clears every counter, e.g. when the user asks for a manual retry round.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public IReadOnlyList<EapRetryStatus> Snapshot()
    {
        lock (_gate)
        {
            return _entries
                .Select(kv =>
                {
                    var separator = kv.Key.IndexOf('|');
                    var guid = Guid.TryParseExact(kv.Key[..separator], "N", out var parsed) ? parsed : Guid.Empty;
                    return new EapRetryStatus(
                        guid,
                        kv.Key[(separator + 1)..],
                        kv.Value.Failures,
                        kv.Value.Abandoned,
                        kv.Value.LastFailureUtc,
                        kv.Value.LastReason);
                })
                .OrderByDescending(s => s.Abandoned)
                .ThenByDescending(s => s.Failures)
                .ToList();
        }
    }

    /// <summary>Number of networks currently given up for this run.</summary>
    public int AbandonedCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Count(e => e.Abandoned);
            }
        }
    }

    private static string Key(Guid guid, string ssid) => $"{guid:N}|{ssid}";

    private struct Entry
    {
        public int Failures;
        public bool Abandoned;
        public DateTimeOffset? LastFailureUtc;
        public string? LastReason;
    }
}
