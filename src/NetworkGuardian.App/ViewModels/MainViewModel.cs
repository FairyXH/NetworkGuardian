using Microsoft.UI.Dispatching;
using NetworkGuardian.App.Services;
using NetworkGuardian.Core.Logging;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.App.ViewModels;

/// <summary>Root view model: owns the host service and the per-page view models.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly InMemoryLogSink _logSink;
    private DateTimeOffset _lastUiUpdate = DateTimeOffset.MinValue;
    private bool _updatePending;
    private int _droppedUpdates;
    private bool _disposed;

    public MainViewModel(GuardianHostService host, InMemoryLogSink logSink, DispatcherQueue dispatcher)
    {
        Host = host;
        _logSink = logSink;
        _dispatcher = dispatcher;

        Dashboard = new DashboardViewModel();
        Wireless = new WirelessViewModel(host, dispatcher);
        Ethernet = new EthernetViewModel();
        Settings = new SettingsViewModel();
        Logs = new LogsViewModel(logSink, dispatcher);

        Settings.Load(host.Config, host.ConfigPath);

        Host.SnapshotUpdated += OnSnapshotUpdated;
        Host.Notification += OnNotification;

        HeaderStatus = "正在启动…";
    }

    public GuardianHostService Host { get; }

    public DashboardViewModel Dashboard { get; }

    public WirelessViewModel Wireless { get; }

    public EthernetViewModel Ethernet { get; }

    public SettingsViewModel Settings { get; }

    public LogsViewModel Logs { get; }

    public event EventHandler<string>? ToastRequested;

    private string _headerStatus = "初始化";
    private string _headerDetail = string.Empty;
    private bool _isPaused;
    private string _pauseButtonText = "暂停自动恢复";
    private DateTimeOffset _lastConfigWriteUtc;

    public string HeaderStatus { get => _headerStatus; private set => Set(ref _headerStatus, value); }

    public string HeaderDetail { get => _headerDetail; private set => Set(ref _headerDetail, value); }

    public bool IsPaused { get => _isPaused; private set => Set(ref _isPaused, value); }

    public string PauseButtonText { get => _pauseButtonText; private set => Set(ref _pauseButtonText, value); }

    public void TogglePause()
    {
        var paused = !Host.IsPaused;
        Host.SetPaused(paused);
        IsPaused = paused;
        PauseButtonText = paused ? "恢复自动恢复" : "暂停自动恢复";
        RaiseToast(paused ? "已暂停自动恢复" : "已恢复自动恢复");
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            var config = Settings.Build();
            await Host.ApplyConfigAsync(config, CancellationToken.None);
            Settings.ApplyStartupRegistration();
            _lastConfigWriteUtc = DateTimeOffset.UtcNow;
            Settings.ReportStatus($"已保存到 {Host.ConfigPath}（{_lastConfigWriteUtc.ToLocalTime():HH:mm:ss}）");
            RaiseToast("配置已保存并生效");
        }
        catch (Exception ex)
        {
            Settings.ReportStatus($"保存失败：{ex.Message}");
            RaiseToast($"保存失败：{ex.Message}");
        }
    }

    public void ReloadSettings()
    {
        Settings.Load(Host.Config, Host.ConfigPath);
        RaiseToast("已重新加载配置");
    }

    public async Task RunConnectivityTestAsync()
    {
        try
        {
            var report = await Host.RunConnectivityTestAsync(CancellationToken.None);
            RaiseToast($"连通性测试：{report.Summary}");
        }
        catch (Exception ex)
        {
            RaiseToast($"连通性测试失败：{ex.Message}");
        }
    }

    public async Task RescanWifiAsync()
    {
        await Wireless.ScanAsync(Wireless.Adapters.FirstOrDefault()!, rescanAll: true);
        RaiseToast("已重新扫描所有无线网卡");
    }

    public void RaiseToast(string message) => ToastRequested?.Invoke(this, message);

    private void OnNotification(object? sender, string message)
    {
        try
        {
            _dispatcher.TryEnqueue(() => RaiseToast(message));
        }
        catch (Exception)
        {
            // The dispatcher may be gone during shutdown.
        }
    }

    private void OnSnapshotUpdated(object? sender, GuardianSnapshot snapshot)
    {
        // Coalesce bursts: the monitor loop can publish faster than the UI needs to repaint.
        if (DateTimeOffset.UtcNow - _lastUiUpdate < TimeSpan.FromMilliseconds(400))
        {
            _droppedUpdates++;
            if (!_updatePending)
            {
                _updatePending = true;
                _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    _updatePending = false;
                    _lastUiUpdate = DateTimeOffset.UtcNow;
                    Apply(snapshot);
                });
            }

            return;
        }

        _lastUiUpdate = DateTimeOffset.UtcNow;
        _ = _droppedUpdates;

        if (_dispatcher.HasThreadAccess)
        {
            Apply(snapshot);
        }
        else
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => Apply(snapshot));
        }
    }

    private void Apply(GuardianSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Dashboard.Update(snapshot);
            Wireless.Update(snapshot);
            Ethernet.Update(snapshot);

            HeaderStatus = snapshot.Health switch
            {
                GuardianHealth.Healthy => "网络正常",
                GuardianHealth.Recovering => "正在恢复网络",
                GuardianHealth.Degraded => "网络降级",
                GuardianHealth.Error => "发生错误",
                GuardianHealth.Paused => "已暂停",
                _ => "状态未知",
            };

            HeaderDetail =
                $"{Dashboard.RecoveryState}｜外网 {Dashboard.InternetState}｜" +
                $"无线网卡 {snapshot.WifiAdapters.Count} 个｜" +
                $"{snapshot.TimestampUtc.ToLocalTime():HH:mm:ss}";

            IsPaused = snapshot.IsPaused;
            PauseButtonText = snapshot.IsPaused ? "恢复自动恢复" : "暂停自动恢复";
        }
        catch (Exception ex)
        {
            HeaderDetail = $"界面更新失败：{ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Host.SnapshotUpdated -= OnSnapshotUpdated;
        Host.Notification -= OnNotification;
        Logs.Dispose();
    }
}
