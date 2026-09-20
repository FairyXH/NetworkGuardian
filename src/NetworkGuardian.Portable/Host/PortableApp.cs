using Microsoft.Extensions.Logging;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Logging;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Infrastructure.Configuration;
using NetworkGuardian.Infrastructure.Logging;
using NetworkGuardian.Portable.Services;
using NetworkGuardian.Portable.Ui;
using NetworkGuardian.Windows.Startup;
using NetworkGuardian.Windows.Tray;

namespace NetworkGuardian.Portable.Host;

/// <summary>
/// Owns the lifetime of the portable application: configuration, logging, the monitor host, the
/// self-drawn window and the tray icon. Everything is wired by hand because Native AOT rules out a
/// reflection based dependency injection container.
/// </summary>
internal sealed class PortableApp
{
    private readonly JsonConfigStore _configStore;
    private readonly InMemoryLogSink _logSink;
    private readonly RollingFileLoggerProvider _fileLogger;
    private readonly SimpleLoggerFactory _loggerFactory;
    private readonly ILogger<PortableApp> _logger;
    private readonly StartupRegistration _startup = new();

    private GuardianHostService? _host;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _shuttingDown;

    private PortableApp(JsonConfigStore configStore, InMemoryLogSink logSink, RollingFileLoggerProvider fileLogger)
    {
        _configStore = configStore;
        _logSink = logSink;
        _fileLogger = fileLogger;
        _loggerFactory = new SimpleLoggerFactory(fileLogger);
        _logger = _loggerFactory.CreateLogger<PortableApp>();
    }

    public bool StartMinimized { get; private set; }

    public string StartPage { get; private set; } = "dashboard";

    /// <summary>
    /// Creates the window, pumps its messages and finally shuts everything down on the calling thread.
    /// </summary>
    /// <remarks>
    /// This must not be an <c>async</c> method that returns to the thread pool: a Win32 message loop
    /// has to run on the thread that owns the window (and this is the process main thread). Waiting
    /// for the asynchronous setup steps here is safe because none of them needs this thread.
    /// </remarks>
    public static int Run(string[] args)
    {
        // The in-memory sink has to be created first: the file logger mirrors every record into it,
        // and the Logs page reads it.
        var logSink = new InMemoryLogSink(2000);
        var fileLogger = new RollingFileLoggerProvider(
            GuardianPaths.LogDirectory, "networkguardian", logSink, settings: null);

        var app = new PortableApp(
            new JsonConfigStore(GuardianPaths.ConfigFile),
            logSink,
            fileLogger);

        app.RunCore(args);
        return 0;
    }

    private void RunCore(string[] args)
    {
        var config = _configStore.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        // The sink and the file logger are created before the configuration is known, so they are
        // re-created with the real settings once it has been loaded.
        _logSink.Capacity = config.Logging.UiBufferSize;
        _fileLogger.MinimumLevel = config.Logging.MinimumLevel;
        ApplyLoggingSettings(config);

        _logger.LogInformation("NetworkGuardian starting (log directory {Directory})", GuardianPaths.LogDirectory);
        foreach (var issue in _configStore.LastLoadIssues)
        {
            _logger.LogWarning("Configuration note: {Issue}", issue);
        }

        StartMinimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)) ||
                         config.Startup.StartMinimized;

        // QA switch: force the window on screen even when the configuration says "start minimized",
        // so the screenshot script does not have to overwrite the user's settings.
        if (args.Any(a => string.Equals(a, "--visible", StringComparison.OrdinalIgnoreCase)))
        {
            StartMinimized = false;
        }

        StartPage = ReadOption(args, "--page") ?? "dashboard";

        _logger.LogInformation("UI: creating the guardian host");
        _host = new GuardianHostService(_configStore, _loggerFactory, _fileLogger, _logSink);
        _host.Notification += (_, message) => _window?.ShowToast(message);
        _host.SnapshotUpdated += (_, snapshot) =>
        {
            _tray?.SetTooltip(
                $"NetworkGuardian - {(snapshot.GlobalProbe.IsOnline ? "外网正常" : "外网中断")}，" +
                $"无线网卡 {snapshot.WifiAdapters.Count} 个");
            _window?.RequestRefresh();
        };

        _logger.LogInformation("UI: creating the main window");
        _window = new MainWindow(this, _host, Pages.Build(), StartPage);
        _logger.LogInformation("UI: window created");
        SetupTray();
        _logger.LogInformation("UI: tray setup finished");

        if (!StartMinimized)
        {
            _window.Show();
            _logger.LogInformation("UI: window shown");
        }

        try
        {
            _host.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the guardian host");
            _window.ShowToast("监测循环启动失败，详见日志");
        }

        _logger.LogInformation("NetworkGuardian started (startMinimized={StartMinimized}, page={Page})", StartMinimized, StartPage);
        _logger.LogInformation("UI: entering the message loop");

        RunMessageLoop();
        _logger.LogInformation("UI: message loop finished");

        // The window is destroyed by WM_APP_QUIT on its owning thread; this only releases its GDI
        // objects (and never runs while a paint could still be in flight).
        _window?.Dispose();
        _window = null;

        ShutdownAsync().GetAwaiter().GetResult();

        // Last write has happened (the log line above and "NetworkGuardian stopped" are in), so the
        // writer can be closed. Disposing it any earlier makes the provider silently reopen a new
        // file for the next line.
        _fileLogger.Dispose();
    }

    /// <summary>Pump until WM_QUIT. The window and the tray icon live on this thread.</summary>
    private static void RunMessageLoop()
    {
        while (true)
        {
            var result = Interop.NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0);
            if (result <= 0)
            {
                return;
            }

            Interop.NativeMethods.TranslateMessage(ref message);
            Interop.NativeMethods.DispatchMessageW(ref message);
        }
    }

    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon("NetworkGuardian - 网络保活", _loggerFactory.CreateLogger<TrayIcon>())
            {
                MenuProvider = BuildTrayMenu,
                MenuCommandSelected = OnTrayCommand,
            };

            _tray.Clicked += (_, kind) =>
            {
                if (kind is TrayClickKind.LeftClick or TrayClickKind.LeftDoubleClick)
                {
                    _window?.Show();
                }
            };

            _tray.Show();
            _logger.LogInformation("UI: tray icon shown");
        }
        catch (Exception ex)
        {
            // The tray icon is a convenience: its failure must not stop the program.
            _logger.LogWarning(ex, "Failed to create the tray icon; the app keeps running without it");
            _tray = null;
        }
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu() => new List<TrayMenuItem>
    {
        new("open", "打开 NetworkGuardian"),
        TrayMenuItem.Separator,
        _host?.IsPaused == true
            ? new TrayMenuItem("pause", "恢复自动恢复")
            : new TrayMenuItem("pause", "暂停自动恢复"),
        new("test", "运行连通性测试"),
        new("rescan", "重新扫描 Wi-Fi"),
        new("reset", "清除失败记录 / 冷却"),
        TrayMenuItem.Separator,
        new("logs", "打开日志目录"),
        new("exit", "退出"),
    };

    private void OnTrayCommand(string command)
    {
        if (_host is null || _window is null)
        {
            return;
        }

        switch (command)
        {
            case "open":
                _window.Show();
                break;
            case "pause":
                _host.SetPaused(!_host.IsPaused);
                _window.ShowToast(_host.IsPaused ? "已暂停自动恢复" : "已恢复自动恢复");
                break;
            case "test":
                _window.RunBackground("connectivity-test", "正在执行连通性测试",
                    () => _host.RunConnectivityTestAsync(CancellationToken.None), "连通性测试完成");
                break;
            case "rescan":
                _window.RunBackground("wireless-scan-all", "正在扫描全部无线网卡",
                    () => _host.RescanAllAsync(CancellationToken.None), "已重新扫描全部网卡");
                break;
            case "reset":
                _host.ResetDerating();
                _window.ShowToast("已清除连接失败记录与冷却状态");
                break;
            case "logs":
                _host.OpenLogFolder();
                break;
            case "exit":
                _ = ShutdownAndQuitAsync();
                break;
        }
    }

    /// <summary>WM_CLOSE: honour "close to tray" and otherwise shut the process down cleanly.</summary>
    public void RequestClose()
    {
        if (_shuttingDown)
        {
            return;
        }

        var closeToTray = _host?.Config.Startup.CloseToTray ?? false;
        if (closeToTray && _tray is { IsVisible: true })
        {
            _logger.LogInformation("Main window closed; hiding to tray");
            _window?.Hide();
            return;
        }

        _logger.LogInformation("Main window closed; shutting down");
        _ = ShutdownAndQuitAsync();
    }

    private async Task ShutdownAndQuitAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        // Keep the window reference: ShutdownAsync clears the field before the quit is posted.
        var window = _window;
        await ShutdownAsync().ConfigureAwait(false);

        // PostQuitMessage is thread local, so the UI thread has to run it.
        window?.PostQuit();
    }

    /// <summary>Saves the configuration and applies everything the UI changed.</summary>
    public async Task ApplyConfigAsync(GuardianConfig config)
    {
        if (_host is null)
        {
            return;
        }

        await _host.ApplyConfigAsync(config, CancellationToken.None).ConfigureAwait(false);
        ApplyLoggingSettings(config);

        if (!_startup.SetEnabled(config.Startup.RunAtLogon))
        {
            _logger.LogWarning("The run-at-logon registration could not be updated");
        }
    }

    /// <summary>Re-reads the configuration document from disk.</summary>
    public async Task<GuardianConfig> ReloadConfigAsync()
    {
        var config = await _configStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        if (_host is not null)
        {
            await _host.ApplyConfigAsync(config, CancellationToken.None).ConfigureAwait(false);
        }

        ApplyLoggingSettings(config);
        return config;
    }

    public bool IsRunAtLogonEnabled()
    {
        try
        {
            return _startup.IsEnabled();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ApplyLoggingSettings(GuardianConfig config)
    {
        _logSink.Capacity = config.Logging.UiBufferSize;
        _fileLogger.MinimumLevel = config.Logging.MinimumLevel;
        _fileLogger.Settings.RetentionDays = config.Logging.RetentionDays;
        _fileLogger.Settings.MaxFileSizeKb = config.Logging.MaxFileSizeKb;
        _fileLogger.Settings.MaxFiles = config.Logging.MaxFiles;
        _fileLogger.Settings.WriteToFile = config.Logging.WriteToFile;
    }

    public async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        try
        {
            _tray?.Dispose();
            _tray = null;

            if (_host is not null)
            {
                await _host.DisposeAsync().ConfigureAwait(false);
                _host = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during shutdown");
        }

        _logger.LogInformation("NetworkGuardian stopped");
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public GuardianSnapshot Snapshot => _host?.Snapshot ?? GuardianSnapshot.Initial(_configStore.Current, DateTimeOffset.UtcNow);
}
