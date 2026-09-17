namespace NetworkGuardian.Core.Policies;

/// <summary>Short term ban applied to profiles that just failed to connect.</summary>
public sealed class ConnectFailureBlacklist
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _banDuration;

    public ConnectFailureBlacklist(TimeSpan banDuration)
    {
        _banDuration = banDuration;
    }

    public TimeSpan BanDuration
    {
        get => _banDuration;
        set => _banDuration = value;
    }

    public void RecordFailure(Guid interfaceGuid, string profileName, DateTimeOffset now, string? reason)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        var key = Key(interfaceGuid, profileName);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
            }

            entry.Failures++;
            entry.Until = now + _banDuration;
            entry.LastReason = reason;
            entry.LastFailureUtc = now;
            _entries[key] = entry;
        }
    }

    public void RecordSuccess(Guid interfaceGuid, string profileName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        var key = Key(interfaceGuid, profileName);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Failures = 0;
                entry.Until = null;
                entry.LastSuccessUtc = now;
                _entries[key] = entry;
            }
            else
            {
                _entries[key] = new Entry { LastSuccessUtc = now };
            }
        }
    }

    public bool IsBlacklisted(Guid interfaceGuid, string profileName, DateTimeOffset now, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(Key(interfaceGuid, profileName), out var entry) || entry.Until is not { } until)
            {
                return false;
            }

            if (now >= until)
            {
                entry.Until = null;
                _entries[Key(interfaceGuid, profileName)] = entry;
                return false;
            }

            remaining = until - now;
            return true;
        }
    }

    public int FailureCount(Guid interfaceGuid, string profileName)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(Key(interfaceGuid, profileName), out var entry) ? entry.Failures : 0;
        }
    }

    public int ClearExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            var removed = 0;
            foreach (var key in _entries.Keys.ToList())
            {
                var entry = _entries[key];
                if (entry.Until is { } until && now >= until)
                {
                    entry.Until = null;
                    _entries[key] = entry;
                }

                if (entry.Until is null && entry.Failures == 0)
                {
                    _entries.Remove(key);
                    removed++;
                }
            }

            return removed;
        }
    }

    /// <summary>Clears every ban, e.g. after the user manually triggers a recovery round.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public IReadOnlyList<string> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _entries
                .Where(kv => kv.Value.Until is { } until && until > now)
                .Select(kv => $"{kv.Key} banned {kv.Value.Until - now} ({kv.Value.LastReason})")
                .ToList();
        }
    }

    private static string Key(Guid guid, string profile) => $"{guid:N}|{profile}";

    private struct Entry
    {
        public int Failures;
        public DateTimeOffset? Until;
        public string? LastReason;
        public DateTimeOffset? LastFailureUtc;
        public DateTimeOffset? LastSuccessUtc;
    }
}
