using System.Text;
using Microsoft.Extensions.Logging;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Logging;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Infrastructure.Logging;

/// <summary>
/// Rolling file logger. Writes plain text lines, rotates on size, prunes by count and age and
/// mirrors every record into an <see cref="ILogSink"/> for the in-app log view.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly ILogSink? _sink;
    private readonly string _directory;
    private readonly string _baseName;
    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private int _fileIndex;
    private long _currentSize;
    private bool _disposed;

    public RollingFileLoggerProvider(
        string directory,
        string baseName = "networkguardian",
        ILogSink? sink = null,
        LoggingSettings? settings = null)
    {
        _directory = directory;
        _baseName = baseName;
        _sink = sink;
        Settings = settings ?? new LoggingSettings();

        Directory.CreateDirectory(_directory);
        Prune();
    }

    public LoggingSettings Settings { get; }

    public bool Enabled { get; set; } = true;

    public GuardianLogLevel MinimumLevel { get; set; } = GuardianLogLevel.Information;

    public string CurrentFilePath
    {
        get
        {
            lock (_gate)
            {
                return BuildPath(_currentDate == default ? DateOnly.FromDateTime(DateTime.Now) : _currentDate, _fileIndex);
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    internal bool IsEnabledFor(GuardianLogLevel level) => Enabled && level >= MinimumLevel && level != GuardianLogLevel.Trace;

    internal void Write(string category, GuardianLogLevel level, string message, Exception? exception)
    {
        var record = new LogRecord(DateTimeOffset.UtcNow, level, category, message, exception?.ToString());

        _sink?.Publish(record);

        if (!Enabled || !Settings.WriteToFile || level < MinimumLevel)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                EnsureWriter();
                var line = record.ToLine();
                _writer!.WriteLine(line);
                _writer.Flush();

                _currentSize += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_currentSize >= Settings.MaxFileSizeKb * 1024L)
                {
                    Roll();
                }
            }
            catch (Exception ex)
            {
                // Logging must never take the application down; the failure is recorded instead of
                // thrown, and published to the sink so it is still visible in the UI.
                if (LastWriteError != ex.Message)
                {
                    LastWriteError = ex.Message;
                    _sink?.Publish(new LogRecord(
                        DateTimeOffset.UtcNow,
                        GuardianLogLevel.Error,
                        "NetworkGuardian.Infrastructure.Logging.RollingFileLoggerProvider",
                        $"日志文件写入失败，已跳过该条记录：{ex.Message}",
                        ex.ToString()));
                }
            }
        }
    }

    /// <summary>
    /// The most recent file write failure. A failing log file is invisible by definition, so the
    /// failure is also published to the in-app sink where the logs page can show it.
    /// </summary>
    public string? LastWriteError { get; private set; }

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        OpenWriter();
    }

    private void OpenWriter()
    {
        _currentDate = DateOnly.FromDateTime(DateTime.Now);
        _fileIndex = 0;

        var path = BuildPath(_currentDate, _fileIndex);

        // The directory may not exist yet on the very first run, which would otherwise silently drop
        // every record written before the configuration store creates it.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        while (File.Exists(path))
        {
            _fileIndex++;
            path = BuildPath(_currentDate, _fileIndex);
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };

        _currentSize = 0;
    }

    private void Roll()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch (Exception)
        {
            // Ignore: a new file is opened next.
        }

        _writer = null;
        OpenWriter();
        Prune();
    }

    private string BuildPath(DateOnly date, int index) =>
        Path.Combine(_directory, $"{_baseName}-{date:yyyyMMdd}-{index:D3}.log");

    private void Prune()
    {
        try
        {
            var files = Directory.EnumerateFiles(_directory, $"{_baseName}-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, Settings.RetentionDays));

            for (var i = 0; i < files.Count; i++)
            {
                var expired = files[i].LastWriteTimeUtc < cutoff;
                var tooMany = i >= Math.Max(1, Settings.MaxFiles);

                if (expired || tooMany)
                {
                    try
                    {
                        files[i].Delete();
                    }
                    catch (Exception)
                    {
                        // A file may be held open by another instance.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Pruning is best effort.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch (Exception)
            {
                // Nothing further to do while disposing.
            }

            _writer = null;
        }
    }

    private sealed class RollingFileLogger : ILogger
    {
        private readonly RollingFileLoggerProvider _provider;
        private readonly string _category;

        public RollingFileLogger(RollingFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = Short(category);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabledFor(Map(logLevel));

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = eventId;

            var level = Map(logLevel);
            if (!_provider.IsEnabledFor(level))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
            {
                return;
            }

            _provider.Write(_category, level, message, exception);
        }

        private static GuardianLogLevel Map(LogLevel level) => level switch
        {
            LogLevel.Trace => GuardianLogLevel.Trace,
            LogLevel.Debug => GuardianLogLevel.Debug,
            LogLevel.Information => GuardianLogLevel.Information,
            LogLevel.Warning => GuardianLogLevel.Warning,
            LogLevel.Error => GuardianLogLevel.Error,
            LogLevel.Critical => GuardianLogLevel.Critical,
            _ => GuardianLogLevel.Information,
        };

        private static string Short(string category)
        {
            var index = category.LastIndexOf('.');
            return index >= 0 && index < category.Length - 1 ? category[(index + 1)..] : category;
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

/// <summary>Builds the application logger factory from <see cref="LoggingSettings"/>.</summary>
public static class GuardianLoggerFactory
{
    public static ILoggerFactory Create(
        LoggingSettings settings,
        ILogSink sink,
        string logDirectory,
        out RollingFileLoggerProvider provider)
    {
        var created = new RollingFileLoggerProvider(logDirectory, "networkguardian", sink, settings)
        {
            MinimumLevel = settings.MinimumLevel,
            Enabled = true,
        };

        provider = created;

        return LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(created);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
    }
}
