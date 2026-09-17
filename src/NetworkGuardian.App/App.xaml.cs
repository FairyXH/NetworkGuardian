using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using NetworkGuardian.App.Services;
using NetworkGuardian.App.ViewModels;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Logging;
using NetworkGuardian.Infrastructure.Configuration;
using NetworkGuardian.Infrastructure.Logging;
using NetworkGuardian.Windows.Tray;

namespace NetworkGuardian.App;

/// <summary>
/// Application entry point. Runs unprivileged; every privileged operation is delegated to the
/// on-demand helper process.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private bool _isFirstInstance;
    private Window? _window;
    private TrayIcon? _tray;
    private InMemoryLogSink? _logSink;
    private ILoggerFactory? _loggerFactory;
    private RollingFileLoggerProvider? _fileLogger;
    private JsonConfigStore? _configStore;
    private ILogger<App>? _logger;
    private PowerModeChangedEventHandler? _powerModeHandler;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\NetworkGuardian.SingleInstance", out var createdNew);
        _isFirstInstance = createdNew;

        StartMinimized = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
    }

    public static MainViewModel? MainViewModel { get; private set; }

    public static GuardianHostService? Host { get; private set; }

    /// <summary>Strongly typed accessor for the running application instance.</summary>
    public static App? Instance => Microsoft.UI.Xaml.Application.Current as App;

    public bool StartMinimized { get; }

    public IntPtr WindowHandle { get; private set; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_isFirstInstance)
        {
            // Another instance owns the tray icon and the monitor loop; do not fight over it.
            Exit();
            return;
        }

        GuardianPaths.EnsureCreated();

        _logSink = new InMemoryLogSink(2000);

        var bootstrapConfig = GuardianConfig.CreateDefault();
        _configStore = new JsonConfigStore(GuardianPaths.ConfigFile);
        var loaded = await _configStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        bootstrapConfig = loaded;

        _logSink.Capacity = bootstrapConfig.Logging.UiBufferSize;

        _loggerFactory = GuardianLoggerFactory.Create(
            bootstrapConfig.Logging, _logSink, GuardianPaths.LogDirectory, out var provider);
        _fileLogger = provider;
        _logger = _loggerFactory.CreateLogger<App>();

        _logger.LogInformation("NetworkGuardian starting (log directory {Directory})", GuardianPaths.LogDirectory);
        if (_configStore.LastLoadIssues.Count > 0)
        {
            foreach (var issue in _configStore.LastLoadIssues)
            {
                _logger.LogWarning("Configuration note: {Issue}", issue);
            }
        }

        Host = new GuardianHostService(_configStore, _loggerFactory, provider, _logSink);

        MainViewModel = new MainViewModel(Host, _logSink, DispatcherQueue.GetForCurrentThread());

        var mainWindow = new MainWindow();
        _window = mainWindow;
        mainWindow.AttachViewModel(MainViewModel);
        _window.Activate();

        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        SetupTray();
        SetupPowerNotifications();

        try
        {
            await Host.StartAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the guardian host");
        }

        if (StartMinimized || bootstrapConfig.Startup.StartMinimized)
        {
            HideMainWindow();
        }

        _logger.LogInformation("NetworkGuardian started (startMinimized={StartMinimized})", StartMinimized);
    }

    private void SetupTray()
    {
        if (_logger is null)
        {
            return;
        }

        try
        {
            _tray = new TrayIcon("NetworkGuardian - 网络保活", _loggerFactory!.CreateLogger<TrayIcon>());

            _tray.MenuProvider = BuildTrayMenu;

            _tray.MenuCommandSelected = command =>
            {
                switch (command)
                {
                    case "open":
                        ShowMainWindow();
                        break;
                    case "pause":
                        MainViewModel?.TogglePause();
                        break;
                    case "test":
                        _ = MainViewModel?.RunConnectivityTestAsync();
                        break;
                    case "rescan":
                        _ = MainViewModel?.RescanWifiAsync();
                        break;
                    case "reset":
                        Host?.ResetDerating();
                        MainViewModel?.RaiseToast("已清除连接失败记录与冷却状态");
                        break;
                    case "logs":
                        Host?.OpenLogFolder();
                        break;
                    case "exit":
                        ExitApplication();
                        break;
                }
            };

            _tray.Clicked += (_, kind) =>
            {
                if (kind == TrayClickKind.LeftDoubleClick || kind == TrayClickKind.LeftClick)
                {
                    ShowMainWindow();
                }
            };

            _tray.Show();

            if (Host is not null)
            {
                Host.SnapshotUpdated += (_, snapshot) =>
                {
                    _tray?.SetTooltip(
                        $"NetworkGuardian - {(snapshot.GlobalProbe.IsOnline ? "外网正常" : "外网中断")}" +
                        $"，无线网卡 {snapshot.WifiAdapters.Count} 个");
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create the tray icon; the app keeps running without it");
        }
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var paused = Host?.IsPaused ?? false;

        return new List<TrayMenuItem>
        {
            new("open", "打开 NetworkGuardian"),
            TrayMenuItem.Separator,
            paused
                ? new TrayMenuItem("pause", "恢复自动恢复")
                : new TrayMenuItem("pause", "暂停自动恢复"),
            new("test", "运行连通性测试"),
            new("rescan", "重新扫描 Wi-Fi"),
            new("reset", "清除失败记录 / 冷却"),
            TrayMenuItem.Separator,
            new("logs", "打开日志目录"),
            new("exit", "退出"),
        };
    }

    private void SetupPowerNotifications()
    {
        try
        {
            _powerModeHandler = (_, e) =>
            {
                if (e.Mode == PowerModes.Resume)
                {
                    _logger?.LogInformation("PowerModeChanged: resume");
                    Host?.NotifySystemResumed();
                }
                else if (e.Mode == PowerModes.StatusChange)
                {
                    _logger?.LogDebug("PowerModeChanged: status change");
                }
            };

            SystemEvents.PowerModeChanged += _powerModeHandler;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Power mode notifications are unavailable");
        }
    }

    /// <summary>
    /// Called when the user closes the main window. Honours the "close to tray" setting and only
    /// shuts the process down when the user really asked for it.
    /// </summary>
    public bool HandleWindowClosing()
    {
        var closeToTray = Host?.Config.Startup.CloseToTray ?? false;

        if (closeToTray && _tray is { IsVisible: true })
        {
            _logger?.LogInformation("Main window closed; hiding to tray");
            HideMainWindow();
            return true; // cancelled the close
        }

        _logger?.LogInformation("Main window closed; shutting down");
        _ = ShutdownAsync().ContinueWith(_ => DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => Exit()));
        return true;
    }

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            _window.AppWindow.Show();
            _window.Activate();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to show the main window");
        }
    }

    public void HideMainWindow()
    {
        try
        {
            _window?.AppWindow.Hide();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to hide the main window");
        }
    }

    public void ExitApplication()
    {
        _logger?.LogInformation("Exit requested from the tray menu");
        _ = ShutdownAsync().ContinueWith(_ => DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => Exit()));
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled XAML exception");
        e.Handled = true;
    }

    public async Task ShutdownAsync()
    {
        try
        {
            if (_powerModeHandler is not null)
            {
                SystemEvents.PowerModeChanged -= _powerModeHandler;
                _powerModeHandler = null;
            }

            _tray?.Dispose();
            _tray = null;

            MainViewModel?.Dispose();
            MainViewModel = null;

            if (Host is not null)
            {
                await Host.DisposeAsync().ConfigureAwait(false);
                Host = null;
            }

        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during shutdown");
        }

        try
        {
            _logger?.LogInformation("NetworkGuardian stopped");
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch (Exception)
        {
            // Nothing useful to do while exiting.
        }
    }
}
