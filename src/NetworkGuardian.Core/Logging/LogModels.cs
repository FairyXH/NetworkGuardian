using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Logging;

/// <summary>One log entry, shared between the file logger and the in-app log view.</summary>
public sealed record LogRecord(
    DateTimeOffset TimestampUtc,
    GuardianLogLevel Level,
    string Category,
    string Message,
    string? Exception)
{
    public string LevelText => Level switch
    {
        GuardianLogLevel.Trace => "TRC",
        GuardianLogLevel.Debug => "DBG",
        GuardianLogLevel.Information => "INF",
        GuardianLogLevel.Warning => "WRN",
        GuardianLogLevel.Error => "ERR",
        GuardianLogLevel.Critical => "CRT",
        _ => "???",
    };

    /// <summary>Coarse channel used by the Logs page filter (network / wifi / ethernet / ...).</summary>
    public string Channel => ResolveChannel(Category);

    public string ToLine() =>
        $"{TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} [{LevelText}] [{Channel}] {Category}: {Message}" +
        (string.IsNullOrEmpty(Exception) ? string.Empty : Environment.NewLine + "    " + Exception.Replace(
            Environment.NewLine, Environment.NewLine + "    "));

    /// <summary>Same text as <see cref="ToLine"/>, exposed as a property for x:Bind.</summary>
    public string Line => ToLine();

    private static string ResolveChannel(string category)
    {
        var value = category ?? string.Empty;

        return value switch
        {
            _ when value.Contains("Authentication", StringComparison.OrdinalIgnoreCase) => "Authentication",
            _ when value.Contains("Wifi", StringComparison.OrdinalIgnoreCase) => "WiFi",
            _ when value.Contains("Wlan", StringComparison.OrdinalIgnoreCase) => "WiFi",
            _ when value.Contains("Radio", StringComparison.OrdinalIgnoreCase) => "WiFi",
            _ when value.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) => "Ethernet",
            _ when value.Contains("Probe", StringComparison.OrdinalIgnoreCase) => "Network",
            _ when value.Contains("Connectivity", StringComparison.OrdinalIgnoreCase) => "Network",
            _ when value.Contains("Network", StringComparison.OrdinalIgnoreCase) => "Network",
            _ when value.Contains("Device", StringComparison.OrdinalIgnoreCase) => "Device",
            _ when value.Contains("Helper", StringComparison.OrdinalIgnoreCase) => "Device",
            _ when value.Contains("Config", StringComparison.OrdinalIgnoreCase) => "Configuration",
            _ => "Application",
        };
    }
}

/// <summary>Sink for log records consumed by the UI.</summary>
public interface ILogSink
{
    event EventHandler<LogRecord>? RecordPublished;

    void Publish(LogRecord record);

    IReadOnlyList<LogRecord> Snapshot();

    void Clear();
}

/// <summary>Thread-safe bounded ring buffer used by the Logs page.</summary>
public sealed class InMemoryLogSink : ILogSink
{
    private readonly object _gate = new();
    private readonly Queue<LogRecord> _records = new();
    private int _capacity;

    public InMemoryLogSink(int capacity = 2000)
    {
        _capacity = Math.Max(100, capacity);
    }

    public event EventHandler<LogRecord>? RecordPublished;

    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _capacity;
            }
        }
        set
        {
            lock (_gate)
            {
                _capacity = Math.Max(100, value);
                Trim();
            }
        }
    }

    public void Publish(LogRecord record)
    {
        lock (_gate)
        {
            _records.Enqueue(record);
            Trim();
        }

        try
        {
            RecordPublished?.Invoke(this, record);
        }
        catch (Exception)
        {
            // A failing UI subscriber must not break logging.
        }
    }

    public IReadOnlyList<LogRecord> Snapshot()
    {
        lock (_gate)
        {
            return _records.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
        }
    }

    public IReadOnlyList<LogRecord> SnapshotFiltered(
        GuardianLogLevel minimumLevel,
        string? channel,
        string? searchText,
        int limit = 1000)
    {
        lock (_gate)
        {
            var query = _records.Where(r => r.Level >= minimumLevel);

            if (!string.IsNullOrWhiteSpace(channel) && !channel.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(r => r.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(r =>
                    r.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    r.Category.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }

            return query.Reverse().Take(limit).Reverse().ToList();
        }
    }

    private void Trim()
    {
        while (_records.Count > _capacity)
        {
            _records.Dequeue();
        }
    }
}
