using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using NetworkGuardian.Core.Logging;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.App.ViewModels;

/// <summary>Backing view model for the Logs page.</summary>
public sealed class LogsViewModel : ObservableObject, IDisposable
{
    private readonly InMemoryLogSink _sink;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly object _pendingGate = new();
    private readonly List<LogRecord> _pending = new();
    private bool _dirty = true;

    private string _selectedChannel = "全部";
    private string _selectedLevel = "Information";
    private string _searchText = string.Empty;
    private bool _autoScroll = true;
    private string _statusText = string.Empty;

    public LogsViewModel(InMemoryLogSink sink, DispatcherQueue dispatcher)
    {
        _sink = sink;
        _dispatcher = dispatcher;

        _sink.RecordPublished += OnRecordPublished;

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(750);
        _timer.Tick += (_, _) => Flush();
        _timer.Start();

        Reload();
    }

    public ObservableCollection<LogRecord> Entries { get; } = new();

    public ObservableCollection<string> Channels { get; } = new(
        new[] { "全部", "Network", "WiFi", "Ethernet", "Authentication", "Device", "Configuration", "Application" });

    public ObservableCollection<string> Levels { get; } = new(
        new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical" });

    public string SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (Set(ref _selectedChannel, value))
            {
                Reload();
            }
        }
    }

    public string SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (Set(ref _selectedLevel, value))
            {
                Reload();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                Reload();
            }
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => Set(ref _autoScroll, value);
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Raised when the list was rebuilt so the view can scroll to the newest entry.</summary>
    public event EventHandler? EntriesReloaded;

    public void Reload()
    {
        var level = Enum.TryParse<GuardianLogLevel>(SelectedLevel, ignoreCase: true, out var parsed)
            ? parsed
            : GuardianLogLevel.Information;

        var records = _sink.SnapshotFiltered(level, SelectedChannel, SearchText, 1500);

        Entries.Clear();
        foreach (var record in records)
        {
            Entries.Add(record);
        }

        StatusText = $"显示 {Entries.Count} 条（共 {_sink.Snapshot().Count} 条缓冲）";
        EntriesReloaded?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _sink.Clear();
        Entries.Clear();
        StatusText = "已清空内存日志缓冲（文件日志不受影响）";
    }

    private void OnRecordPublished(object? sender, LogRecord record)
    {
        lock (_pendingGate)
        {
            _pending.Add(record);
            if (_pending.Count > 4000)
            {
                _pending.RemoveRange(0, _pending.Count - 4000);
            }

            _dirty = true;
        }
    }

    private void Flush()
    {
        lock (_pendingGate)
        {
            if (!_dirty && _pending.Count == 0)
            {
                return;
            }

            _dirty = false;
            _pending.Clear();
        }

        try
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, Reload);
        }
        catch (Exception)
        {
            // The dispatcher may already be shutting down.
        }
    }

    public void Dispose()
    {
        _sink.RecordPublished -= OnRecordPublished;
        _timer.Stop();
    }
}
