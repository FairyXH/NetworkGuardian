using Microsoft.Extensions.Logging;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Logging;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Wlan;
using NetworkGuardian.Infrastructure.Configuration;
using NetworkGuardian.Infrastructure.Logging;
using NetworkGuardian.Infrastructure.Processes;
using NetworkGuardian.Windows.Connectivity;
using NetworkGuardian.Windows.Devices;
using NetworkGuardian.Windows.Location;
using NetworkGuardian.Windows.Network;
using NetworkGuardian.Windows.Radio;
using NetworkGuardian.Windows.Security;
using NetworkGuardian.Windows.Wlan;

namespace NetworkGuardian.Portable.Services;

public sealed record WifiCredentialSuggestion(
    string Ssid,
    string ProfileName,
    WifiEnterpriseAuth Auth,
    WifiEapMethod Eap,
    WifiSecurity Security,
    int SignalQuality,
    bool HasSavedProfile,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> TrustedRootCaThumbprints);

/// <summary>
/// Orchestrates the whole product: it owns the background monitor loop, executes the intents
/// produced by <see cref="GuardianDecisionEngine"/> and publishes a
/// <see cref="GuardianSnapshot"/> for the UI.
/// </summary>
public sealed class GuardianHostService : IAsyncDisposable
{
    private readonly ILogger<GuardianHostService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly InMemoryLogSink _logSink;
    private readonly JsonConfigStore _configStore;
    private readonly RollingFileLoggerProvider _fileLogger;
    private readonly NativeWifiManager _wifi;
    private readonly WifiRadioController _radio;
    private readonly NetworkInterfaceProvider _interfaces;
    private readonly TcpConnectionMigrator _tcpConnections;
    private readonly ConnectivityProbe _probe;
    private readonly NpcapProbeVerifier _npcap;
    private readonly PhysicalDeviceManager _devices;
    private readonly ExternalCommandRunner _commands;
    private readonly LocationPermissionService _location;
    private readonly DeviceNotificationWatcher _deviceNotifications;
    private readonly NetworkDeviceClassifier _classifier = new();
    private readonly GuardianDecisionEngine _engine;
    private readonly WifiNetworkVault _vault;
    private readonly WifiProfileApplier _profiles;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly SemaphoreSlim _cycleSignal = new(0, 1);
    private readonly Random _jitter = new();

    /// <summary>
    /// When each class of decision note was last written, so a persistent condition (a faulted adapter,
    /// a disabled device, a throttled limiter) does not repeat every cycle.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _noteLogTimes = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _radioWatchdog;
    private Task? _routeWatchdog;
    private GuardianConfig _config;
    private GuardianSnapshot _snapshot;
    private DateTimeOffset _lastEnumerationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProbeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextProbeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPerInterfaceProbeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _resumeQuietUntilUtc = DateTimeOffset.MinValue;
    private bool _forceEnumeration = true;
    private bool _forceProbe = true;
    private bool _manualScanRequested;
    private IReadOnlyDictionary<Guid, PnpDeviceRecord> _deviceByNetCfgGuid = new Dictionary<Guid, PnpDeviceRecord>();

    /// <summary>WLAN interfaces whose PnP record could not be correlated, warned about only once.</summary>
    private readonly HashSet<Guid> _unmatchedWlanInterfaces = new();
    private IReadOnlyList<ManagedDevice> _devicesSnapshot = Array.Empty<ManagedDevice>();

    /// <summary>Routes captured while the interface states were built, reused by the snapshot.</summary>
    private IReadOnlyList<DefaultRouteInfo> _defaultRoutes = Array.Empty<DefaultRouteInfo>();
    private Dictionary<Guid, List<string>> _profilesByAdapter = new();
    private ConnectivityProbeReport _globalProbe;
    private Dictionary<Guid, ConnectivityProbeReport> _wifiProbeByAdapter = new();
    private Dictionary<string, ConnectivityProbeReport> _probeByInterfaceId = new();
    private string? _observedOutletInterfaceId;
    private IReadOnlyList<string> _observedOutletAddresses = Array.Empty<string>();
    private string? _observedOutletDeviceInstanceId;
    private string? _observedOutletName;
    private bool _observedOutletWasPhysical;
    private DateTimeOffset _lastOldAdapterRestartUtc = DateTimeOffset.MinValue;
    private readonly Dictionary<string, InterfaceProbeStability> _probeStability = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, (DateTimeOffset AtUtc, string Profile)> _lastConnectAttempt = new();
    private WifiEapCatalog _eapCatalog = WifiEapCatalog.Empty;
    private bool _disposed;

    public GuardianHostService(
        JsonConfigStore configStore,
        ILoggerFactory loggerFactory,
        RollingFileLoggerProvider fileLogger,
        InMemoryLogSink logSink)
    {
        _configStore = configStore;
        _loggerFactory = loggerFactory;
        _fileLogger = fileLogger;
        _logSink = logSink;
        _logger = loggerFactory.CreateLogger<GuardianHostService>();
        _config = configStore.Current;

        _wifi = new NativeWifiManager(loggerFactory.CreateLogger<NativeWifiManager>());
        _radio = new WifiRadioController(
            loggerFactory.CreateLogger<WifiRadioController>(),
            access: new NativeRadioAccess(loggerFactory.CreateLogger<NativeRadioAccess>()));
        _npcap = new NpcapProbeVerifier(loggerFactory.CreateLogger<NpcapProbeVerifier>());
        _logger.Log(
            _npcap.Status.IsAvailable ? LogLevel.Information : LogLevel.Warning,
            "Npcap status: {Available}; {Detail}; version={Version}",
            _npcap.Status.IsAvailable,
            _npcap.Status.Detail,
            _npcap.Status.Version ?? "unknown");
        _probe = new ConnectivityProbe(loggerFactory.CreateLogger<ConnectivityProbe>(), _npcap);
        _commands = new ExternalCommandRunner(loggerFactory.CreateLogger<ExternalCommandRunner>());
        _location = new LocationPermissionService(
            () => _wifi.LocationPermission,
            loggerFactory.CreateLogger<LocationPermissionService>());
        _deviceNotifications = new DeviceNotificationWatcher(loggerFactory.CreateLogger<DeviceNotificationWatcher>());

        _devices = new PhysicalDeviceManager(
            _classifier,
            () => _wifi.GetAdapters().Select(a => a.InterfaceGuid).ToList(),
            loggerFactory.CreateLogger<PhysicalDeviceManager>())
        {
            DenyList = _config.InterfaceDenyList,
        };

        _interfaces = new NetworkInterfaceProvider(
            guid => _deviceByNetCfgGuid.TryGetValue(guid, out var record) ? record : null,
            () => _wifi.GetAdapters().Select(a => a.InterfaceGuid).ToList(),
            loggerFactory.CreateLogger<NetworkInterfaceProvider>());
        _tcpConnections = new TcpConnectionMigrator(loggerFactory.CreateLogger<TcpConnectionMigrator>());

        _engine = new GuardianDecisionEngine(_config, loggerFactory.CreateLogger<GuardianDecisionEngine>());

        // The application's own wireless network library: the account for every 802.1X network the user
        // maintains here, independent of what Windows stored. The password is DPAPI protected on disk.
        _vault = new WifiNetworkVault(
            GuardianPaths.WifiCredentialFile,
            new DpapiSecretProtector(loggerFactory.CreateLogger<DpapiSecretProtector>()),
            loggerFactory.CreateLogger<WifiNetworkVault>());

        _profiles = new WifiProfileApplier(_wifi, loggerFactory.CreateLogger<WifiProfileApplier>());

        _globalProbe = ConnectivityProbeReport.NotAttempted(DateTimeOffset.UtcNow, "not-yet-run");
        _snapshot = GuardianSnapshot.Initial(_config, DateTimeOffset.UtcNow);

        _wifi.NotificationReceived += OnWlanNotification;
        _radio.StateChanged += OnRadioStateChanged;
        _engine.RecoveryActionPerformed += OnRecoveryActionPerformed;
    }

    public event EventHandler<GuardianSnapshot>? SnapshotUpdated;

    public event EventHandler<string>? Notification;

    public GuardianSnapshot Snapshot => _snapshot;

    public GuardianConfig Config => _config;

    public GuardianDecisionEngine Engine => _engine;

    public bool IsPaused { get; private set; }

    public InMemoryLogSink LogSink => _logSink;

    public string ConfigPath => _configStore.ConfigPath;

    public string LogDirectory => GuardianPaths.LogDirectory;

    public NpcapRuntimeStatus NpcapStatus => _npcap.Status;

    /// <summary>Logs a failure that happened in a UI action so it is visible in the log as well as the UI.</summary>
    public void LogUiFailure(string message) =>
        _logger.LogError("UI action failed: {Message}", message);

    /// <summary>
    /// Records a UI interaction detail (hit testing, layout) at debug level. Self-drawn controls
    /// fail silently by nature - a click that hits nothing looks exactly like a click that hits a
    /// dead region - so the hit path has to be traceable from the log.
    /// </summary>
    public void LogUiDebug(string message) =>
        _logger.LogDebug("UI: {Message}", message);

    /// <summary>
    /// Writes the decision engine's notes to the log - but only the ones not logged before, because a
    /// persistent condition (a faulted adapter, a disabled device, a throttled limiter) repeats every
    /// cycle and would otherwise drown everything else. The notes are what the engine decided *not* to
    /// act on, so without them a user sees "the adapter was never enabled" with nothing in the log.
    /// </summary>
    private void LogDecisionNotes(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
        {
            _noteLogTimes.Clear();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var active = new HashSet<string>(StringComparer.Ordinal);

        foreach (var note in notes)
        {
            var key = NoteKey(note);
            active.Add(key);

            // Limiter throttles report a fresh counter every cycle ("last run 22s ago", then "23s ago"),
            // so notes are grouped by their text with the digits removed and logged at most once a
            // minute; a single stuck condition used to fill the log with hundreds of identical lines.
            if (_noteLogTimes.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(60))
            {
                continue;
            }

            _noteLogTimes[key] = now;
            _logger.LogInformation("决策提示: {Note}", note);
        }

        // Forget classes that no longer apply, so a returning condition is reported again.
        foreach (var key in _noteLogTimes.Keys.Where(k => !active.Contains(k)).ToList())
        {
            _noteLogTimes.Remove(key);
        }
    }

    /// <summary>Identity of a note ignoring counters, so "last run 22s ago" and "23s ago" match.</summary>
    private static string NoteKey(string note)
    {
        var length = Math.Min(note.Length, 64);
        var chars = note[..length].ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsDigit(chars[i]))
            {
                chars[i] = '#';
            }
        }

        return new string(chars);
    }

    public LocationPermissionSnapshot LocationPermission => _location.Read();

    public IReadOnlyList<ManagedDevice> Devices => _devicesSnapshot;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ApplyConfigToComponents(_config);

        HelperClient.PruneStaleExchangeFiles(TimeSpan.FromHours(6));

        var layoutIssues = _wifi.ValidateStructLayouts();
        if (layoutIssues.Count > 0)
        {
            _logger.LogError("Native Wi-Fi struct layout self check reported {Count} problem(s); " +
                             "Wi-Fi features may misbehave. {Problems}", layoutIssues.Count, string.Join("; ", layoutIssues));
        }

        _deviceNotifications.TryStart();

        // The self-maintained wireless network library is loaded before the first cycle: the engine needs
        // to know which 802.1X networks it can authenticate for.
        await _vault.LoadAsync(cancellationToken).ConfigureAwait(false);
        _eapCatalog = _vault.BuildCatalog();
        _logger.LogInformation(
            "自维护无线网络库已加载：{Count} 个网络（密码保护：{Protection}），文件 {Path}",
            _vault.Count, _vault.ProtectionName, _vault.Path);
        foreach (var issue in _vault.LastLoadIssues)
        {
            _logger.LogWarning("无线网络库提示：{Issue}", issue);
        }

        // Disabled adapters are absent from WlanEnumInterfaces. Enumerate PnP before writing profiles so
        // exclusive auto-connect ownership stays stable while one of the adapters is disabled.
        await RefreshEnumerationAsync(cancellationToken).ConfigureAwait(false);
        _lastEnumerationUtc = DateTimeOffset.UtcNow;
        _forceEnumeration = false;

        // Windows Settings can initiate an enterprise connection before the guardian selects a candidate.
        // Seed only real 802.1X entries. Personal/open credentials deliberately stay out of the persistent
        // profile store and are supplied to WlanConnect as a temporary profile when a connection is needed.
        foreach (var entry in _vault.Entries.Where(e => e.Enabled))
        {
            var results = await ApplyWifiLibraryEntryAsync(entry.Id, null, cancellationToken).ConfigureAwait(false);
            foreach (var result in results)
            {
                _logger.LogInformation("启动时同步 802.1X 凭据：{Ssid} - {Result}", entry.Ssid, result);
            }
        }

        if (_config.General.EnsureRadioOnAtStartup && _config.General.AutoEnableWifiRadio)
        {
            var radio = await _radio.GetAsync(cancellationToken).ConfigureAwait(false);
            if (radio.State == RadioState.Off)
            {
                _logger.LogInformation("Wi-Fi radio is off at startup; requesting it to be turned on");
                await _radio.SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => MonitorLoopAsync(_cts.Token), CancellationToken.None);
        _radioWatchdog = Task.Run(() => RadioWatchdogLoopAsync(_cts.Token), CancellationToken.None);
        _routeWatchdog = Task.Run(() => RouteWatchdogLoopAsync(_cts.Token), CancellationToken.None);

        _logger.LogInformation("Guardian host started (config {Path})", _configStore.ConfigPath);
    }

    /// <summary>Signals the monitor loop to re-evaluate immediately.</summary>
    public void RequestImmediateCycle()
    {
        _forceProbe = true;
        _forceEnumeration = true;
        try
        {
            _cycleSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake-up is enough; the flags above preserve all requested work.
        }
    }

    private void SignalMonitorCycle(bool forceProbe)
    {
        _forceProbe |= forceProbe;
        try
        {
            _cycleSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake-up is sufficient.
        }
    }

    /// <summary>Locks metrics and publishes the actual default outlet once per second.</summary>
    private async Task RouteWatchdogLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        do
        {
            try
            {
                var adapters = BuildAdapterStates();
                var interfaces = BuildInterfaceStates();
                var desired = InterfaceMetricPlanner.Plan(interfaces, adapters);
                var drifted = interfaces.Any(i =>
                    desired.TryGetValue(i.Id, out var metric) &&
                    (i.InterfaceMetric != metric || i.RouteMetric is not null && i.RouteMetric != 1));

                if (drifted && !IsPaused && _config.General.AutomaticRecovery &&
                    _config.General.ManageInterfaceMetrics)
                {
                    var notes = await _interfaces.ApplyInterfaceMetricsAsync(desired, cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var note in notes)
                    {
                        _logger.LogInformation("跃点锁定: {Note}", note);
                    }

                    interfaces = BuildInterfaceStates();
                }

                await PublishLiveRouteSnapshotAsync(interfaces, adapters, desired, cancellationToken)
                    .ConfigureAwait(false);

                // Probes run outside this watchdog so network timeouts never delay the one-second
                // metric check. Three seconds is the freshness target for outlet health.
                if (DateTimeOffset.UtcNow >= _resumeQuietUntilUtc && DateTimeOffset.UtcNow >= _nextProbeUtc)
                {
                    SignalMonitorCycle(forceProbe: true);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "实时出口/跃点监视器执行失败");
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));

        _logger.LogInformation("实时出口/跃点监视器已停止");
    }

    private async Task PublishLiveRouteSnapshotAsync(
        IReadOnlyList<InterfaceRuntimeState> interfaces,
        IReadOnlyList<WifiAdapterRuntimeState> adapters,
        IReadOnlyDictionary<string, int> desiredMetrics,
        CancellationToken cancellationToken)
    {
        var expectedId = InterfaceMetricPlanner.ExpectedOutletId(interfaces, desiredMetrics);
        var actualRoute = _defaultRoutes.FirstOrDefault();
        var actualInterface = DefaultRouteSelector.FindInterface(actualRoute, interfaces);
        var actualId = actualInterface?.Id;
        var now = DateTimeOffset.UtcNow;

        if (actualId is not null && !string.Equals(actualId, _observedOutletInterfaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            var oldAddresses = _observedOutletInterfaceId is null
                ? interfaces
                    .Where(iface => iface.Kind is InterfaceKind.Ethernet or InterfaceKind.Wifi &&
                                    iface.IsPhysicalDevice != false &&
                                    !string.Equals(iface.Id, actualId, StringComparison.OrdinalIgnoreCase))
                    .Select(iface => iface.PrimaryIpv4Address)
                    .Where(address => !string.IsNullOrWhiteSpace(address) &&
                                      !string.Equals(address, "127.0.0.1", StringComparison.Ordinal))
                    .Select(address => address!)
                    .ToArray()
                : _observedOutletAddresses;
            var previousId = _observedOutletInterfaceId;
            var previousDeviceInstanceId = _observedOutletDeviceInstanceId;
            var previousName = _observedOutletName;
            var previousWasPhysical = _observedOutletWasPhysical;
            _observedOutletInterfaceId = actualId;
            _observedOutletAddresses = string.IsNullOrWhiteSpace(actualInterface?.PrimaryIpv4Address)
                ? Array.Empty<string>()
                : new[] { actualInterface.PrimaryIpv4Address! };
            _observedOutletDeviceInstanceId = actualInterface?.DeviceInstanceId;
            _observedOutletName = actualInterface?.Name;
            _observedOutletWasPhysical = actualInterface?.IsPhysicalDevice == true;

            if (_config.ConnectionMigration.RestartOldAdapterOnSwitch)
            {
                var sameDevice = !string.IsNullOrWhiteSpace(previousDeviceInstanceId) &&
                                 string.Equals(previousDeviceInstanceId, actualInterface?.DeviceInstanceId,
                                     StringComparison.OrdinalIgnoreCase);
                var cooldown = now - _lastOldAdapterRestartUtc < TimeSpan.FromSeconds(60);
                if (previousId is not null && previousWasPhysical &&
                    !string.IsNullOrWhiteSpace(previousDeviceInstanceId) && !sameDevice && !cooldown)
                {
                    // Record before elevation so a cancelled UAC prompt or failed driver restart
                    // cannot create a prompt storm while routes are still settling.
                    _lastOldAdapterRestartUtc = now;
                    var result = await _devices.RestartAsync(
                            previousDeviceInstanceId, cancellationToken, forceRunningDevice: true)
                        .ConfigureAwait(false);
                    _logger.Log(
                        result.Success ? LogLevel.Information : LogLevel.Warning,
                        "出口连接迁移: {Previous} -> {Current}; 重启旧物理网卡 {Device}: {Outcome} {Detail}",
                        previousId, actualId, previousName ?? previousDeviceInstanceId,
                        result.Outcome, result.Detail);
                    Notification?.Invoke(this, result.Success
                        ? $"已重启旧出口网卡“{previousName ?? previousId}”，其 TCP / UDP 连接已断开"
                        : $"旧出口网卡“{previousName ?? previousId}”重启失败：{result.Detail ?? result.Outcome.ToString()}");
                    _forceEnumeration = true;
                    _forceProbe = true;
                }
                else if (previousId is not null)
                {
                    _logger.LogDebug(
                        "未重启旧出口 {Previous}: physical={Physical}, device={Device}, sameDevice={Same}, cooldown={Cooldown}",
                        previousId, previousWasPhysical, previousDeviceInstanceId ?? "-", sameDevice, cooldown);
                }
            }
            else if (_config.ConnectionMigration.Mode != ConnectionCutMode.Disabled && oldAddresses.Count > 0)
            {
                var closed = await _tcpConnections.CloseAsync(
                        oldAddresses, _config.ConnectionMigration, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "出口连接迁移: {Previous} -> {Current}; 旧地址={Addresses}; 已关闭 {Count} 条 TCP 连接",
                    previousId ?? "startup", actualId, string.Join(",", oldAddresses), closed);
            }
        }
        else if (actualId is not null && !string.IsNullOrWhiteSpace(actualInterface?.PrimaryIpv4Address))
        {
            _observedOutletAddresses = new[] { actualInterface.PrimaryIpv4Address! };
            _observedOutletDeviceInstanceId = actualInterface.DeviceInstanceId;
            _observedOutletName = actualInterface.Name;
            _observedOutletWasPhysical = actualInterface.IsPhysicalDevice == true;
        }

        _snapshot = _snapshot with
        {
            TimestampUtc = now,
            Interfaces = interfaces,
            WifiAdapters = adapters,
            DefaultRoutes = _defaultRoutes,
            ExpectedOutletInterfaceId = expectedId,
            OutletMatchesPolicy = expectedId is null
                ? actualRoute is null
                : string.Equals(expectedId, actualId, StringComparison.OrdinalIgnoreCase),
            RouteObservedAtUtc = now,
        };
        SnapshotUpdated?.Invoke(this, _snapshot);
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        _logger.LogInformation("Automatic recovery {State}", paused ? "paused" : "resumed");
        Notification?.Invoke(this, paused
            ? "已暂停自动恢复"
            : "已恢复自动恢复");
        RequestImmediateCycle();
    }

    public void NotifySystemResumed()
    {
        var settle = TimeSpan.FromSeconds(Math.Max(0, _config.General.ResumeSettleSeconds));
        _resumeQuietUntilUtc = DateTimeOffset.UtcNow + settle;
        _forceEnumeration = true;
        _forceProbe = true;
        _engine.StateMachine.Transition(RecoveryState.Initializing, DateTimeOffset.UtcNow, "system resumed from sleep");
        _logger.LogInformation("System resume detected; settling for {Settle}s before recovery", settle.TotalSeconds);
    }

    public async Task ApplyConfigAsync(GuardianConfig config, CancellationToken cancellationToken)
    {
        config.InterfaceDenyList ??= new List<string>();
        await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        _config = config;
        ApplyConfigToComponents(config);
        RequestImmediateCycle();
        _logger.LogInformation("Configuration updated and saved to {Path}", _configStore.ConfigPath);
    }

    /// <summary>
    /// Keeps every wireless adapter's software radio switch on.
    /// </summary>
    /// <remarks>
    /// Windows keeps one switch per adapter, so an adapter switched off in Settings stays off for the whole
    /// run unless something re-reads all of them. The monitor cycle is far too slow for that (its heartbeat
    /// is the health sweep), and a Wi-Fi recovery that finds "no network" on a switched-off adapter never
    /// starts. This loop therefore only does one thing, on its own cadence: read the per-adapter switches
    /// and immediately switch a software-off radio back on.
    /// <para>
    /// Hardware-off radios are reported, never retried: no software can turn those on. While the user has
    /// paused automatic recovery the loop stays out of the way, exactly like every other automatic action.
    /// </para>
    /// </remarks>
    private async Task RadioWatchdogLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Clamp(_config.General.RadioWatchdogSeconds, 1, 600));

            try
            {
                var active = _config.General.AutomaticRecovery &&
                             _config.General.RadioWatchdogEnabled &&
                             _config.General.AutoEnableWifiRadio;

                if (!active)
                {
                    _logger.LogDebug("无线电看门狗已关闭（automaticRecovery/radioWatchdogEnabled/autoEnableWifiRadio）");
                }
                else if (IsPaused)
                {
                    _logger.LogDebug("已暂停自动恢复：无线电看门狗不干预网卡软开关");
                }
                else
                {
                    var result = await _radio.SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);

                    if (result.StateChanged)
                    {
                        _logger.LogInformation("无线电看门狗：已把软件关闭的无线网卡重新打开");
                        Notification?.Invoke(this, "已自动打开无线网卡的软开关");
                        _forceEnumeration = true;
                        _forceProbe = true;
                    }
                    else if (!result.Success && !string.IsNullOrWhiteSpace(result.Failure))
                    {
                        _logger.LogWarning("无线电看门狗未能打开软开关：{Failure}", result.Failure);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "无线电看门狗循环出错");
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("无线电看门狗循环已停止");
    }

    private void ApplyConfigToComponents(GuardianConfig config)
    {
        _engine.ApplyConfig(config);
        _devices.DenyList = config.InterfaceDenyList;
        _fileLogger.MinimumLevel = config.Logging.MinimumLevel;
        _fileLogger.Settings.RetentionDays = config.Logging.RetentionDays;
        _fileLogger.Settings.MaxFileSizeKb = config.Logging.MaxFileSizeKb;
        _fileLogger.Settings.MaxFiles = config.Logging.MaxFiles;
        _fileLogger.Settings.WriteToFile = config.Logging.WriteToFile;
        _logSink.Capacity = config.Logging.UiBufferSize;
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        var heartbeat = TimeSpan.FromSeconds(Math.Max(5, _config.General.HealthSweepSeconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in the guardian monitor cycle");
                _engine.StateMachine.Transition(RecoveryState.Error, DateTimeOffset.UtcNow, ex.Message);
            }

            heartbeat = TimeSpan.FromSeconds(Math.Max(5, _config.General.HealthSweepSeconds));

            try
            {
                // Event driven wake-up with a bounded fallback heartbeat.
                using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var wifiWait = _wifi.WaitForNotificationAsync(heartbeat, wakeCts.Token);
                var deviceWait = _deviceNotifications.WaitAsync(heartbeat, wakeCts.Token);
                var immediateWait = _cycleSignal.WaitAsync(wakeCts.Token);
                var heartbeatWait = Task.Delay(heartbeat, wakeCts.Token);
                await Task.WhenAny(wifiWait, deviceWait, immediateWait, heartbeatWait).ConfigureAwait(false);
                await wakeCts.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Guardian monitor loop stopped");
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;

            _wifi.DrainNotifications();
            var deviceTreeChanged = _deviceNotifications.Drain(out var deviceChanges);
            if (deviceChanges.Count > 0)
            {
                foreach (var change in deviceChanges)
                {
                    _logger.LogDebug("PnP device change: {Change}", change);
                }

                if (deviceTreeChanged)
                {
                    _forceEnumeration = true;
                    _forceProbe = true;
                    _logger.LogInformation(
                        "检测到网卡热插拔，立即刷新设备、Npcap、跃点和出口探测");
                }
            }

            var enumerationInterval = TimeSpan.FromSeconds(Math.Max(30, _config.General.EnumerationRefreshSeconds));
            if (_forceEnumeration || now - _lastEnumerationUtc >= enumerationInterval)
            {
                await RefreshEnumerationAsync(cancellationToken).ConfigureAwait(false);
                _lastEnumerationUtc = DateTimeOffset.UtcNow;
                _forceEnumeration = false;
            }

            var probeDue = _forceProbe || now >= _nextProbeUtc;
            if (probeDue && now >= _resumeQuietUntilUtc)
            {
                await RefreshProbesAsync(cancellationToken).ConfigureAwait(false);
                _lastProbeUtc = DateTimeOffset.UtcNow;
                _forceProbe = false;
            }

            var adapters = BuildAdapterStates();
            var interfaces = BuildInterfaceStates();

            // The library can change while the program runs (the user edits an entry), so the engine gets a
            // fresh summary of "which 802.1X networks can be authenticated" every cycle.
            _eapCatalog = _vault.BuildCatalog();

            var input = new GuardianInput
            {
                Now = DateTimeOffset.UtcNow,
                Config = _config,
                GlobalProbe = _globalProbe,
                Interfaces = interfaces,
                WifiAdapters = adapters,
                Devices = _devicesSnapshot,
                Radio = await _radio.GetAsync(cancellationToken).ConfigureAwait(false),
                IsPaused = IsPaused,
                ManualScanRequested = _manualScanRequested,
                Location = _location.Read(),
                EapCatalog = _eapCatalog,
            };

            _manualScanRequested = false;

            var decision = _engine.Evaluate(input);

            LogDecisionNotes(decision.Notes);

            if (!IsPaused && _config.General.AutomaticRecovery)
            {
                // Route failover is time-critical. Apply metrics before scans, association waits,
                // authentication commands, or any other recovery action can delay the cycle.
                var orderedActions = decision.Actions
                    .OrderByDescending(action => action is ApplyInterfaceMetricsAction)
                    .ToList();
                foreach (var action in orderedActions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ExecuteActionAsync(action, cancellationToken).ConfigureAwait(false);
                }

                // Metric changes alter the route table immediately. Re-read it before publishing so
                // the dashboard never keeps showing the pre-failover outlet for another heartbeat.
                if (orderedActions.Any(action => action is ApplyInterfaceMetricsAction))
                {
                    input = input with { Interfaces = BuildInterfaceStates() };
                }
            }

            PublishSnapshot(decision, input);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task RefreshEnumerationAsync(CancellationToken cancellationToken)
    {
        var adapterCount = _wifi.RefreshAdapters();
        _devicesSnapshot = await _devices.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var npcapStatus = _npcap.RefreshDevices();

        _deviceByNetCfgGuid = _devicesSnapshot
            .Where(d => Guid.TryParse(d.Record.NetCfgInstanceId, out _))
            .GroupBy(d => Guid.Parse(d.Record.NetCfgInstanceId!))
            .ToDictionary(g => g.Key, g => g.First().Record);

        // A fresh enumeration may resolve interfaces that were previously unmatched.
        _unmatchedWlanInterfaces.Clear();

        var profiles = new Dictionary<Guid, List<string>>();
        foreach (var adapter in _wifi.GetAdapters())
        {
            var names = _wifi.GetProfileNames(adapter.InterfaceGuid);
            profiles[adapter.InterfaceGuid] = names.ToList();

            _logger.LogDebug("Adapter {Adapter}: {ProfileCount} saved profile(s), state {State}",
                adapter.InterfaceGuid, names.Count, adapter.State);
        }

        _profilesByAdapter = profiles;

        var physical = _devicesSnapshot.Count(d => d.Classification.IsPhysical);
        _logger.LogInformation(
            "Enumeration complete: {AdapterCount} WLAN interface(s), {DeviceCount} network device(s), " +
            "{PhysicalCount} classified physical; Npcap={NpcapAvailable} ({NpcapDetail})",
            adapterCount, _devicesSnapshot.Count, physical, npcapStatus.IsAvailable, npcapStatus.Detail);
    }

    private async Task RefreshProbesAsync(CancellationToken cancellationToken)
    {
        var request = new ProbeRequest
        {
            Endpoints = _config.ProbeEndpoints,
            Settings = _config.Probe,
        };

        var byAdapter = new Dictionary<Guid, ConnectivityProbeReport>();
        var byInterfaceId = new Dictionary<string, ConnectivityProbeReport>(StringComparer.Ordinal);
        var interfaces = _interfaces.GetInterfaces();
        Task<ConnectivityProbeReport>? globalTask = null;

        if (_config.Probe.PerInterfaceProbing)
        {
            var candidates = interfaces
                .Where(i => i.Kind is InterfaceKind.Wifi or InterfaceKind.Ethernet)
                .Where(i => i.IsUp && i.HasUsableIpv4 && i.PrimaryIpv4Address is not null)
                .ToList();

            var interfaceTasks = candidates.Select(async candidate =>
            {
                var interfaceRequest = request with
                {
                    SourceAddress = candidate.PrimaryIpv4Address,
                    InterfaceIndex = candidate.InterfaceIndex,
                    InterfaceId = candidate.Id,
                    AdapterGuid = candidate.AdapterGuid,
                    // Raw Npcap injection is intentionally limited to positively identified
                    // physical adapters. TUN/TAP interfaces continue through normal probes only.
                    SourceMacAddress = candidate.IsPhysicalDevice == true ? candidate.MacAddress : null,
                    DnsServerAddresses = candidate.DnsServers,
                    GatewayAddress = candidate.PrimaryGateway,
                    MaxConcurrencyOverride = 2,
                };

                var report = await _probe.ProbeAsync(interfaceRequest, cancellationToken).ConfigureAwait(false);
                return (Candidate: candidate, Report: report);
            }).ToArray();

            var interfaceReports = await Task.WhenAll(interfaceTasks).ConfigureAwait(false);
            var hasOnlineAlternative = false;
            foreach (var item in interfaceReports)
            {
                hasOnlineAlternative |= item.Report.IsOnline;
            }
            foreach (var (candidate, report) in interfaceReports)
            {
                var stabilized = ApplyProbeStability(
                    candidate.Id,
                    report,
                    failImmediately: !report.IsOnline && hasOnlineAlternative && !report.InterfaceTrafficObserved);
                if (candidate.WlanInterfaceGuid is { } guid)
                {
                    byAdapter[guid] = stabilized;
                }

                byInterfaceId[candidate.Id] = stabilized;
            }

            _globalProbe = ConnectivityProbeReport.SelectBest(byInterfaceId.Values)
                ?? ConnectivityProbeReport.NotAttempted(DateTimeOffset.UtcNow, "no-up-interface");
        }
        else
        {
            globalTask = _probe.ProbeAsync(request, cancellationToken);
            _globalProbe = ApplyProbeStability(
                "global",
                await globalTask.ConfigureAwait(false));
        }

        _wifiProbeByAdapter = byAdapter;
        _probeByInterfaceId = byInterfaceId;
        ScheduleNextProbe(byInterfaceId.Count > 0 ? byInterfaceId.Values : new[] { _globalProbe });
    }

    private ConnectivityProbeReport ApplyProbeStability(
        string interfaceId,
        ConnectivityProbeReport report,
        bool failImmediately = false)
    {
        _probeStability.TryGetValue(interfaceId, out var state);
        state ??= new InterfaceProbeStability();
        _probeStability[interfaceId] = state;
        return state.Apply(
            report,
            failureThreshold: report.IsAuthoritative || failImmediately ? 1 : 2,
            recoveryThreshold: report.IsAuthoritative ? 1 : _config.Recovery.InternetRecoveryThreshold,
            recoveryHold: report.IsAuthoritative
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(_config.Recovery.InterfaceRecoveryHoldSeconds),
            now: DateTimeOffset.UtcNow);
    }

    private void ScheduleNextProbe(IEnumerable<ConnectivityProbeReport> reports)
    {
        var samples = reports.ToList();
        var adaptiveSeconds = samples.Count == 0 || samples.Any(report =>
                report.Reachability is InternetReachability.Unknown or
                    InternetReachability.LocalOnly or
                    InternetReachability.CaptivePortal)
            ? _config.Probe.FailureIntervalSeconds
            : samples.Any(report => report.Reachability == InternetReachability.InternetLikely)
                ? _config.Probe.IndeterminateIntervalSeconds
                : _config.Probe.IntervalSeconds;
        var seconds = _config.General.AutomaticRecovery && _config.Probe.PerInterfaceProbing
            ? Math.Min(adaptiveSeconds, _config.Probe.FastRouteIntervalSeconds)
            : adaptiveSeconds;
        _nextProbeUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private IReadOnlyList<WifiAdapterRuntimeState> BuildAdapterStates()
    {
        var states = new List<WifiAdapterRuntimeState>();

        foreach (var adapter in _wifi.GetAdapters())
        {
            _deviceByNetCfgGuid.TryGetValue(adapter.InterfaceGuid, out var record);

            if (record is not null)
            {
                var classification = _classifier.Classify(record, _wifi.GetAdapters().Select(a => a.InterfaceGuid).ToList(),
                    _config.InterfaceDenyList);

                if (!classification.IsPhysical)
                {
                    _logger.LogDebug("Ignoring WLAN interface {Guid}: {Reason}", adapter.InterfaceGuid, classification.Reason);
                    continue;
                }
            }
            else
            {
                // The adapter state is rebuilt on every cycle, so the warning is emitted once per
                // interface and only repeated when the enumeration changes.
                if (_unmatchedWlanInterfaces.Add(adapter.InterfaceGuid))
                {
                    _logger.LogWarning(
                        "WLAN interface {Guid} has no matching PnP device record; treating it as physical. " +
                        "Check the device enumeration if this is unexpected.",
                        adapter.InterfaceGuid);
                }
                else
                {
                    _logger.LogDebug("WLAN interface {Guid}: still no matching PnP record", adapter.InterfaceGuid);
                }
            }

            var connection = _wifi.GetConnection(adapter.InterfaceGuid);
            if (connection is not null && connection.IsConnected && string.IsNullOrEmpty(connection.Bssid) == false)
            {
                connection = _wifi.EnrichWithBssDetails(adapter.InterfaceGuid, connection);
            }

            var scan = _wifi.GetLastScan(adapter.InterfaceGuid);
            _profilesByAdapter.TryGetValue(adapter.InterfaceGuid, out var profiles);

            states.Add(new WifiAdapterRuntimeState
            {
                InterfaceGuid = adapter.InterfaceGuid,
                Description = adapter.Description,
                DeviceInstanceId = record?.DeviceInstanceId,
                MacAddress = record?.MacAddress,
                IsConnected = connection?.IsConnected == true,
                Connection = connection,
                LastScan = scan,
                LastConnectAttemptUtc = _lastConnectAttempt.TryGetValue(adapter.InterfaceGuid, out var attempt)
                    ? attempt.AtUtc
                    : null,
                LastAttemptedProfile = _lastConnectAttempt.TryGetValue(adapter.InterfaceGuid, out var attempted)
                    ? attempted.Profile
                    : null,
                LastFailure = scan?.FailureReason,
                IsScanInProgress = _wifi.IsScanInProgress(adapter.InterfaceGuid),
                LastScanAttemptUtc = scan?.StartedAtUtc,
                ProfileListKnown = profiles is not null,
                SavedProfiles = profiles ?? new List<string>(),
                RecentConnectFailures = 0,
            });
        }

        return states;
    }

    private IReadOnlyList<InterfaceRuntimeState> BuildInterfaceStates()
    {
        var interfaces = _interfaces.GetInterfaces();
        var routes = DefaultRouteSelector.Order(_interfaces.GetDefaultRoutes());
        _defaultRoutes = routes;
        var bestRoute = routes.FirstOrDefault();
        var result = new List<InterfaceRuntimeState>(interfaces.Count);

        foreach (var state in interfaces)
        {
            var probe = state.Kind switch
            {
                InterfaceKind.Wifi when state.WlanInterfaceGuid is { } guid &&
                                         _wifiProbeByAdapter.TryGetValue(guid, out var wifiProbe) => wifiProbe,
                _ when _probeByInterfaceId.TryGetValue(state.Id, out var byId) => byId,
                _ => null,
            };

            var isDefault = bestRoute is not null && DefaultRouteSelector.MatchesInterface(bestRoute, state);
            var route = routes.FirstOrDefault(r => DefaultRouteSelector.MatchesInterface(r, state));

            result.Add(state with
            {
                Probe = probe,
                IsDefaultRoute = isDefault,
                RouteMetric = route?.RouteMetric,
                InterfaceMetric = state.InterfaceMetric ?? route?.InterfaceMetric,
            });
        }

        return result;
    }

    private void PublishSnapshot(GuardianDecision decision, GuardianInput input)
    {
        var diagnostics = _engine.GetDiagnostics(input.Now);

        _snapshot = new GuardianSnapshot
        {
            TimestampUtc = input.Now,
            State = decision.State,
            Health = GuardianHealth.Unknown,
            Connectivity = decision.Connectivity,
            IsPaused = IsPaused,
            IsAutomaticRecoveryEnabled = _config.General.AutomaticRecovery,
            GlobalProbe = _globalProbe,
            Radio = input.Radio,
            Location = input.Location,
            Interfaces = input.Interfaces,
            WifiAdapters = input.WifiAdapters,
            WifiDevices = _devicesSnapshot
                .Where(d => d.Classification.Category == DeviceCategory.PhysicalWifi)
                .ToList(),
            EthernetDevices = _devicesSnapshot
                .Where(d => d.Classification.Category == DeviceCategory.PhysicalEthernet)
                .ToList(),
            // Reuse the enumeration performed while the interface states were built: the route table
            // does not change between those two steps inside a single cycle.
            DefaultRoutes = _defaultRoutes,
            ExpectedOutletInterfaceId = InterfaceMetricPlanner.ExpectedOutletId(
                input.Interfaces,
                InterfaceMetricPlanner.Plan(input.Interfaces, input.WifiAdapters)),
            OutletMatchesPolicy = null,
            RouteObservedAtUtc = input.Now,
            PendingActions = decision.Actions,
            Notes = decision.Notes,
            LastRecoveryAction = _engine.LastRecoveryAction,
            LastRecoveryActionUtc = _engine.LastRecoveryActionUtc,
            ConsecutiveInternetFailures = diagnostics.ConsecutiveInternetFailures,
            LastInternetSuccessUtc = _engine.LastInternetSuccessUtc,
            LastCampusAuthUtc = _engine.LastCampusAuthUtc,
            CampusAuthRunCount = diagnostics.CampusAuthRunsLastHour,
        };

        _snapshot = _snapshot with { Health = _snapshot.ComputeHealth() };
        SnapshotUpdated?.Invoke(this, _snapshot);
    }

    private async Task ExecuteActionAsync(GuardianAction action, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case NoAction:
                    break;

                case WaitAction wait:
                {
                    var delay = wait.Delay > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : wait.Delay;
                    if (delay > TimeSpan.Zero)
                    {
                        _logger.LogDebug("Waiting {Seconds:F0}s: {Reason}", delay.TotalSeconds, wait.Reason);
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }

                case EnableWifiRadioAction:
                {
                    var result = await _radio.SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);
                    if (result.Success)
                    {
                        _logger.LogInformation("Wi-Fi radio enabled (stateChanged={Changed})", result.StateChanged);
                        Notification?.Invoke(this, "已开启 Wi-Fi 无线电");
                    }
                    else
                    {
                        _logger.LogWarning("Enabling the Wi-Fi radio failed: {Failure}", result.Failure);
                    }

                    _forceEnumeration = true;
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    break;
                }

                case EnableWifiDeviceAction enable:
                {
                    var result = await _devices.EnableAsync(enable.DeviceInstanceId, cancellationToken)
                        .ConfigureAwait(false);

                    _engine.NotifyDeviceOperation("enable", result.Success, DateTimeOffset.UtcNow);

                    if (result.Success)
                    {
                        _logger.LogInformation("Enabled physical network device {Device}: {Detail}",
                            enable.DeviceInstanceId, result.Detail);
                        Notification?.Invoke(this, $"已启用网卡 {enable.FriendlyName}");
                        await Task.Delay(
                                TimeSpan.FromSeconds(Math.Max(1, _config.Recovery.DeviceEnableSettleSeconds)),
                                cancellationToken)
                            .ConfigureAwait(false);
                        _forceEnumeration = true;
                        _wifi.RefreshAdapters();
                        RequestImmediateCycle();
                    }
                    else
                    {
                        _engine.NotifyActionFailed(enable, result.Detail ?? result.Outcome.ToString(), DateTimeOffset.UtcNow);
                        _logger.LogWarning(
                            "Enabling {Device} failed: {Outcome} {Message} {Detail} (error {Error})",
                            enable.DeviceInstanceId, result.Outcome, result.Win32Message, result.Detail,
                            result.NativeErrorCode);
                    }

                    break;
                }

                case RestartWifiDeviceAction restart:
                {
                    _logger.LogInformation(
                        "Restarting physical Wi-Fi device {Device} ({Name}); problem code {Problem} means the " +
                        "driver did not start, so enabling alone would not help",
                        restart.DeviceInstanceId, restart.FriendlyName, restart.ProblemCode);

                    var result = await _devices.RestartAsync(restart.DeviceInstanceId, cancellationToken)
                        .ConfigureAwait(false);

                    _engine.NotifyDeviceOperation("restart", result.Success, DateTimeOffset.UtcNow);

                    if (result.Success)
                    {
                        _logger.LogInformation("Restarted physical Wi-Fi device {Device}: {Detail}",
                            restart.DeviceInstanceId, result.Detail);
                        Notification?.Invoke(this, $"已重启网卡 {restart.FriendlyName}");
                        await Task.Delay(
                                TimeSpan.FromSeconds(Math.Max(2, _config.Recovery.DeviceEnableSettleSeconds)),
                                cancellationToken)
                            .ConfigureAwait(false);
                        _forceEnumeration = true;
                        _wifi.RefreshAdapters();
                    }
                    else
                    {
                        _engine.NotifyActionFailed(restart, result.Detail ?? result.Outcome.ToString(), DateTimeOffset.UtcNow);
                        _logger.LogWarning(
                            "Restarting {Device} did not bring it back: {Outcome} {Message} {Detail} (error {Error})",
                            restart.DeviceInstanceId, result.Outcome, result.Win32Message, result.Detail,
                            result.NativeErrorCode);
                    }

                    break;
                }

                case ScanAdapterAction scan:
                {
                    var timeout = TimeSpan.FromSeconds(scan.Force
                        ? _config.General.ManualScanTimeoutSeconds
                        : _config.General.ScanTimeoutSeconds);

                    var snapshot = await _wifi.RequestScanAsync(scan.InterfaceGuid, scan.Force, timeout, cancellationToken)
                        .ConfigureAwait(false);

                    _engine.NotifyScanFinished(
                        scan.InterfaceGuid,
                        DateTimeOffset.UtcNow,
                        scan.Force,
                        success: !snapshot.Failed);

                    if (snapshot.Failed)
                    {
                        _logger.LogWarning("Scan on {Adapter} failed: {Reason}",
                            scan.InterfaceGuid, snapshot.FailureReason);
                    }

                    break;
                }

                case ConnectWifiAction connect:
                {
                    _lastConnectAttempt[connect.InterfaceGuid] = (DateTimeOffset.UtcNow, connect.ProfileName);

                    // 802.1X user data requires a persistent Windows profile. Personal/open networks take
                    // the same one-shot Native Wi-Fi path as a user entering a password: WlanConnect receives
                    // a temporary profile and NetworkGuardian never calls WlanSetProfile for that attempt.
                    if (connect.UsesLibraryCredential && connect.RequiresEap)
                    {
                        var prepared = await EnsureLibraryProfileAsync(connect, cancellationToken)
                            .ConfigureAwait(false);

                        if (!prepared.Success)
                        {
                            _engine.NotifyConnectResult(
                                connect.InterfaceGuid, connect.ProfileName, success: false,
                                prepared.Failure, DateTimeOffset.UtcNow, requiredEap: true);
                            _logger.LogWarning(
                                "未能为 {Ssid} 准备 802.1X 配置，连接未发起：{Failure}",
                                connect.Ssid, prepared.Failure);
                            Notification?.Invoke(this, $"{connect.Ssid}：802.1X 配置准备失败（{prepared.Failure}）");
                            break;
                        }
                    }

                    WlanOperationResult result;
                    if (connect.UsesLibraryCredential && !connect.RequiresEap)
                    {
                        var resolved = ResolveTemporaryCredential(connect);
                        if (resolved.Credential is null)
                        {
                            result = WlanOperationResult.Fail(resolved.Failure ?? "无线网络库凭据不可用");
                        }
                        else
                        {
                            result = await _wifi.ConnectTemporaryAsync(
                                    connect.InterfaceGuid, resolved.Credential, connect.Bssid, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        result = await _wifi.ConnectAsync(
                                connect.InterfaceGuid, connect.ProfileName, connect.Bssid, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    _engine.NotifyConnectResult(
                        connect.InterfaceGuid, connect.ProfileName, result.Success, result.Failure,
                        DateTimeOffset.UtcNow, connect.RequiresEap);

                    if (result.Success)
                    {
                        Notification?.Invoke(this, $"已连接 {connect.Ssid}");
                        await Task.Delay(
                                TimeSpan.FromSeconds(Math.Max(0, _config.General.DhcpWaitSeconds)),
                                cancellationToken)
                            .ConfigureAwait(false);
                        _forceProbe = true;
                    }
                    else
                    {
                        _logger.LogWarning("Connect to {Profile} on {Adapter} failed: {Failure}",
                            connect.ProfileName, connect.InterfaceGuid, result.Failure);
                    }

                    break;
                }

                case DisconnectWifiAction disconnect:
                {
                    if (disconnect.SuppressAutoReconnect)
                    {
                        var profileMode = _wifi.SetProfileAutoConnect(
                            disconnect.InterfaceGuid, disconnect.Ssid, enabled: false);
                        if (!profileMode.Success)
                        {
                            _logger.LogWarning(
                                "Could not suppress auto-connect for duplicate SSID {Ssid} on {Adapter}: {Failure}",
                                disconnect.Ssid, disconnect.InterfaceGuid, profileMode.Failure);
                        }
                    }

                    var result = await _wifi.DisconnectAsync(disconnect.InterfaceGuid, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.Success)
                    {
                        _logger.LogWarning("Disconnect on {Adapter} failed: {Failure}",
                            disconnect.InterfaceGuid, result.Failure);
                    }

                    break;
                }

                case RunExternalCommandAction run:
                {
                    var definition = ResolveCommand(run);
                    if (definition is null)
                    {
                        _logger.LogWarning("Command {Id} is no longer configured; skipping", run.CommandId);
                        break;
                    }

                    var result = await _commands.RunAsync(definition, run.Reason, cancellationToken)
                        .ConfigureAwait(false);

                    if (result.Started)
                    {
                        _engine.NotifyCommandStarted(run, DateTimeOffset.UtcNow);
                    }

                    if (result.Success)
                    {
                        _logger.LogInformation("Command {Name} completed ({Result})", definition.Name, result);
                    }
                    else
                    {
                        _logger.LogWarning("Command {Name} did not succeed: {Result} {Failure}",
                            definition.Name, result, result.Failure);
                    }

                    Notification?.Invoke(this, result.Success
                        ? run.IsCampusAuth
                            ? $"已执行校园网认证：{definition.Name}"
                            : $"已执行命令：{definition.Name}"
                        : run.IsCampusAuth
                            ? $"校园网认证失败：{definition.Name}（{result.Failure ?? result.ToString()}）"
                            : $"命令执行失败：{definition.Name}（{result.Failure ?? result.ToString()}）");

                    _forceProbe = true;
                    break;
                }

                case ProbeConnectivityAction:
                {
                    _forceProbe = true;
                    await RefreshProbesAsync(cancellationToken).ConfigureAwait(false);
                    _lastProbeUtc = DateTimeOffset.UtcNow;
                    break;
                }

                case RefreshAdaptersAction:
                {
                    _forceEnumeration = true;
                    await RefreshEnumerationAsync(cancellationToken).ConfigureAwait(false);
                    _lastEnumerationUtc = DateTimeOffset.UtcNow;
                    break;
                }

                case ApplyInterfaceMetricsAction metrics:
                {
                    var notes = await _interfaces.ApplyInterfaceMetricsAsync(
                            metrics.MetricsByInterfaceId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var note in notes)
                    {
                        _logger.LogInformation("Interface metric: {Note}", note);
                    }

                    break;
                }

                default:
                    _logger.LogDebug("Ignoring unsupported action {Action}", action.Describe());
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _engine.NotifyActionFailed(action, ex.Message, DateTimeOffset.UtcNow);
            _logger.LogError(ex, "Action {Action} threw", action.Describe());
        }
    }

    private CommandDefinition? ResolveCommand(RunExternalCommandAction action)
    {
        if (action.IsCampusAuth)
        {
            return _config.CampusAuth.Enabled && !string.IsNullOrWhiteSpace(_config.CampusAuth.ExecutablePath)
                ? _config.CampusAuth.ToCommandDefinition()
                : null;
        }

        var command = _config.OfflineCommands
            .FirstOrDefault(c => string.Equals(c.Id, action.CommandId, StringComparison.OrdinalIgnoreCase));

        return command is { Enabled: true } && !string.IsNullOrWhiteSpace(command.ExecutablePath)
            ? command
            : null;
    }

    private void OnWlanNotification(object? sender, WlanNotificationEvent notification)
    {
        switch (notification.Code)
        {
            case "acm.interface_arrival":
            case "acm.interface_removal":
                _forceEnumeration = true;
                break;
            case "msm.connected":
            case "msm.disconnected":
            case "acm.disconnected":
            case "msm.link_degraded":
            case "acm.connection_attempt_fail":
                _forceProbe = true;
                break;
        }
    }

    private void OnRadioStateChanged(object? sender, WifiRadioSnapshot snapshot)
    {
        _logger.LogInformation("Wi-Fi radio state changed to {State}", snapshot.State);
        _forceEnumeration = true;
    }

    private void OnRecoveryActionPerformed(object? sender, string description)
    {
        _logger.LogInformation("Recovery action: {Description}", description);
    }

    /// <summary>
    /// Makes sure the adapter carries the 802.1X profile and the account of <paramref name="action"/>'s
    /// network, taken from the built-in wireless network library.
    /// </summary>
    private async Task<WifiProfileApplyResult> EnsureLibraryProfileAsync(
        ConnectWifiAction action,
        CancellationToken cancellationToken)
    {
        var entry = _vault.Find(action.Ssid) ?? _vault.FindByProfile(action.ProfileName);
        if (entry is null)
        {
            return WifiProfileApplyResult.Fail(
                $"自维护无线网络库中没有「{action.Ssid}」的可用凭据");
        }

        if (!entry.Enabled)
        {
            return WifiProfileApplyResult.Fail($"「{action.Ssid}」在无线网络库中已被停用");
        }

        if (entry.PasswordDecryptionFailed)
        {
            return WifiProfileApplyResult.Fail(
                $"「{action.Ssid}」保存的密码无法解密（可能来自其他用户或其他电脑），请在“网络凭据库”中重新填写");
        }

        if (!_config.Wifi.ApplyEapProfileOnConnect)
        {
            // The user disabled automatic writing on purpose: use whatever profile the adapter already has.
            _logger.LogDebug("已关闭自动写入无线配置，直接使用网卡上的现有配置连接 {Ssid}", action.Ssid);
            return new WifiProfileApplyResult { Success = true };
        }

        var result = _profiles.Apply(
            action.InterfaceGuid,
            CredentialForAdapter(entry, action.InterfaceGuid),
            allowWrite: true);
        if (!result.Success)
        {
            return result;
        }

        if (result.Changed)
        {
            _logger.LogInformation(
                "已按自维护无线网络库写入配置：{Ssid}（类型 {Kind}，配置 {Profile}，凭证 {Credentials}）",
                entry.Ssid, entry.Kind, result.ProfileWritten ? "已写入" : "无需更新",
                result.UserDataWritten ? "已写入" : "无需更新");
            Notification?.Invoke(this, $"{entry.Ssid}：已写入无线配置与凭据");
        }

        // Remember that the entry is applied, so the next cycle does not rewrite the profile.
        await _vault
            .MarkAppliedAsync(entry.Id, result.AppliedFingerprint, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        _eapCatalog = _vault.BuildCatalog();
        return result;
    }

    private (WifiNetworkCredential? Credential, string? Failure) ResolveTemporaryCredential(
        ConnectWifiAction action)
    {
        var entry = _vault.Find(action.Ssid) ?? _vault.FindByProfile(action.ProfileName);
        if (entry is null)
        {
            return (null, $"无线网络库中没有「{action.Ssid}」的可用凭据");
        }

        if (!entry.Enabled)
        {
            return (null, $"「{action.Ssid}」在无线网络库中已被停用");
        }

        if (entry.PasswordDecryptionFailed)
        {
            return (null, $"「{action.Ssid}」保存的密码无法解密，请在“网络凭据库”中重新填写");
        }

        var kind = action.Security is WifiSecurity.Open or WifiSecurity.EnhancedOpen
            ? WifiCredentialKind.Open
            : WifiCredentialKind.Personal;
        var credential = CloneCredential(entry, connectAutomatically: false);

        // The scan is live runtime evidence and wins over a stale v1 library entry. Older libraries only
        // supported enterprise credentials, so personal hotspots could survive an upgrade as kind=0.
        credential.Kind = kind;
        credential.Security = action.Security;
        credential.ProfileName = action.Ssid;
        credential.ProfileXmlOverride = null;
        credential.Identity = string.Empty;
        credential.UseWinLogonCredentials = false;

        return (credential, null);
    }

    // ---------- Manual operations used by the UI ----------

    public async Task<AdapterScanSnapshot> ScanAdapterAsync(Guid interfaceGuid, CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Manual scan requested on {Adapter}", interfaceGuid);
            var timeout = TimeSpan.FromSeconds(Math.Max(5, _config.General.ManualScanTimeoutSeconds));
            var result = await _wifi.RequestScanAsync(interfaceGuid, force: true, timeout, cancellationToken)
                .ConfigureAwait(false);
            _engine.NotifyScanFinished(
                interfaceGuid,
                DateTimeOffset.UtcNow,
                forcedByUser: true,
                success: !result.Failed);
            return result;
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async Task<WlanOperationResult> ConnectAsync(Guid interfaceGuid, string profileName, CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Manual connect on {Adapter} to profile {Profile}", interfaceGuid, profileName);
            var result = await _wifi.ConnectAsync(interfaceGuid, profileName, null, cancellationToken)
                .ConfigureAwait(false);
            _engine.NotifyConnectResult(interfaceGuid, profileName, result.Success, result.Failure, DateTimeOffset.UtcNow);
            _forceProbe = true;
            return result;
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async Task<WlanOperationResult> DisconnectAsync(Guid interfaceGuid, CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Manual disconnect on {Adapter}", interfaceGuid);
            return await _wifi.DisconnectAsync(interfaceGuid, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async Task<DeviceOperationResult> EnableDeviceAsync(string deviceInstanceId, CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _devices.EnableAsync(deviceInstanceId, cancellationToken).ConfigureAwait(false);
            _forceEnumeration = true;
            return result;
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async Task<ConnectivityProbeReport> RunConnectivityTestAsync(CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Manual connectivity test requested");
            await RefreshEnumerationAsync(cancellationToken).ConfigureAwait(false);
            await RefreshProbesAsync(cancellationToken).ConfigureAwait(false);
            _lastProbeUtc = DateTimeOffset.UtcNow;
            _forceEnumeration = false;
            _forceProbe = false;
            return _globalProbe;
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async Task RescanAllAsync(CancellationToken cancellationToken)
    {
        foreach (var adapter in _wifi.GetAdapters())
        {
            try
            {
                await ScanAdapterAsync(adapter.InterfaceGuid, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Manual scan failed on {Adapter}", adapter.InterfaceGuid);
            }
        }
    }

    public void ResetDerating()
    {
        _engine.ResetDerating(DateTimeOffset.UtcNow);
        _logger.LogInformation("Connection derating state cleared by the user");
        RequestImmediateCycle();
    }

    // ---------- the self-maintained wireless network library (used by the UI) ----------

    public string WifiLibraryPath => _vault.Path;

    public string WifiLibraryProtection => _vault.ProtectionName;

    public IReadOnlyList<string> WifiLibraryIssues => _vault.LastLoadIssues;

    /// <summary>Entries of the library, passwords decrypted for editing. Copies, never the live objects.</summary>
    public IReadOnlyList<WifiNetworkCredential> WifiLibraryEntries => _vault.Entries;

    /// <summary>802.1X attempt counters of the current run, including the networks given up.</summary>
    public IReadOnlyList<EapRetryStatus> EapRetryStatus => _engine.EapRetries.Snapshot();

    /// <summary>
    /// Returns every visible network as a credential-library choice. Security is taken from the scan;
    /// when an enterprise profile exists its outer EAP type is inspected as well.
    /// </summary>
    public IReadOnlyList<WifiCredentialSuggestion> GetWifiCredentialSuggestions()
    {
        var suggestions = new Dictionary<string, WifiCredentialSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in _snapshot.WifiAdapters)
        {
            foreach (var network in adapter.LastScan?.Networks ?? Array.Empty<ScannedNetwork>())
            {
                if (string.IsNullOrWhiteSpace(network.Ssid))
                {
                    continue;
                }

                var profileName = string.IsNullOrWhiteSpace(network.ProfileName) ? network.Ssid : network.ProfileName!;
                var xml = network.HasProfile ? _wifi.GetProfileXml(adapter.InterfaceGuid, profileName) : null;
                var eapType = Core.Wlan.WifiProfileInspector.TryReadEapMethodType(xml);
                var eap = eapType switch
                {
                    "13" => WifiEapMethod.Tls,
                    "25" => WifiEapMethod.PeapMschapv2,
                    null or "" => WifiEapMethod.PeapMschapv2,
                    _ => WifiEapMethod.CustomXml,
                };
                var auth = network.Security switch
                {
                    WifiSecurity.WpaEnterprise => WifiEnterpriseAuth.WpaEnterprise,
                    WifiSecurity.Wpa3Enterprise => WifiEnterpriseAuth.Wpa3Enterprise,
                    _ => WifiEnterpriseAuth.Wpa2Enterprise,
                };

                var suggestion = new WifiCredentialSuggestion(
                    network.Ssid, profileName, auth, eap, network.Security,
                    network.SignalQuality, network.HasProfile,
                    Core.Wlan.WifiProfileInspector.ReadServerNames(xml),
                    Core.Wlan.WifiProfileInspector.ReadTrustedRootCaThumbprints(xml));

                if (!suggestions.TryGetValue(network.Ssid, out var existing) ||
                    suggestion.SignalQuality > existing.SignalQuality)
                {
                    suggestions[network.Ssid] = suggestion;
                }
            }
        }

        return suggestions.Values
            .OrderByDescending(s => s.SignalQuality)
            .ThenBy(s => s.Ssid, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Persists edited library entries, immediately writes enabled enterprise entries and their per-user
    /// EAP data to every current adapter, and clears their retry counters. Personal/open entries remain in
    /// the DPAPI-protected library and are only handed to WlanConnect for a temporary connection.
    /// </summary>
    public async Task SaveWifiLibraryAsync(
        IReadOnlyList<WifiNetworkCredential> entries,
        CancellationToken cancellationToken)
    {
        await _vault.SaveAsync(entries, cancellationToken).ConfigureAwait(false);

        var cleared = 0;
        foreach (var entry in entries)
        {
            cleared += _engine.ClearEapRetries(entry.Ssid);
        }

        await _vault.LoadAsync(cancellationToken).ConfigureAwait(false);
        _eapCatalog = _vault.BuildCatalog();

        var applied = 0;
        var failed = 0;
        foreach (var entry in _vault.Entries.Where(e => e.Enabled))
        {
            var results = await ApplyWifiLibraryEntryAsync(entry.Id, null, cancellationToken).ConfigureAwait(false);
            applied += results.Count(r => r.Contains("已写入", StringComparison.Ordinal) ||
                                          r.Contains("无需更新", StringComparison.Ordinal));
            failed += results.Count(r => r.Contains("失败", StringComparison.Ordinal) ||
                                         r.Contains("无法", StringComparison.Ordinal));
        }

        _logger.LogInformation(
            "自维护无线网络库已保存并应用：{Count} 个网络，清除 {Cleared} 条 802.1X 失败计数，" +
            "应用结果 {Applied} 成功 / {Failed} 失败，{Catalog}",
            _vault.Count, cleared, applied, failed, _eapCatalog.Describe());

        _forceEnumeration = true;
        RequestImmediateCycle();
    }

    /// <summary>
    /// Writes one enterprise library entry to the given adapters (or every adapter) right away. Personal
    /// and open entries never write a system profile; they are consumed by the temporary connection path.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyWifiLibraryEntryAsync(
        string entryId,
        Guid? interfaceGuid,
        CancellationToken cancellationToken)
    {
        var entry = _vault.Entries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
        if (entry is null)
        {
            return new[] { "无线网络库中没有该条目" };
        }

        if (!ShouldPersistEnterpriseProfile(entry))
        {
            return new[] { "个人/开放网络使用临时 WLAN API 连接，不写入系统配置" };
        }

        var adapters = interfaceGuid is { } guid
            ? _wifi.GetAdapters().Where(a => a.InterfaceGuid == guid).ToList()
            : _wifi.GetAdapters().ToList();

        if (adapters.Count == 0)
        {
            return new[] { "没有可用的物理无线网卡" };
        }

        var results = new List<string>();
        var autoConnectOwner = SelectAutoConnectOwner(adapters);
        foreach (var adapter in adapters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A manual apply must not be skipped by the "already applied" marker.
            entry.AppliedFingerprint = null;
            entry.LastAppliedUtc = null;

            var allowAutoConnect = entry.ConnectAutomatically &&
                                   (_config.Wifi.AllowSameSsidOnMultipleAdapters ||
                                    adapter.InterfaceGuid == autoConnectOwner);
            var result = _profiles.Apply(
                adapter.InterfaceGuid,
                CloneCredential(entry, allowAutoConnect),
                allowWrite: true);
            results.Add(result.Success
                ? $"{adapter.Description}：已写入" +
                  (entry.ConnectAutomatically && !_config.Wifi.AllowSameSsidOnMultipleAdapters
                      ? allowAutoConnect ? "（自动连接主网卡）" : "（已禁止重复自动连接）"
                      : string.Empty)
                : $"{adapter.Description}：{result.Failure}");

            if (result.Success)
            {
                await _vault.MarkAppliedAsync(entry.Id, result.AppliedFingerprint, DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        _forceEnumeration = true;
        RequestImmediateCycle();
        _logger.LogInformation("手动写入 802.1X 配置：{Results}", string.Join("；", results));
        return results;
    }

    private bool ShouldPersistEnterpriseProfile(WifiNetworkCredential entry)
    {
        if (!entry.IsEnterprise)
        {
            return false;
        }

        // A v1 library entry defaults to Enterprise. If Windows already has a non-EAP profile with the
        // same name, that is stronger evidence that this is a personal/open network and must not be
        // overwritten during startup before the first scan completes.
        foreach (var adapter in _wifi.GetAdapters())
        {
            var existing = _wifi.GetProfileXml(adapter.InterfaceGuid, entry.EffectiveProfileName);
            if (!string.IsNullOrWhiteSpace(existing) && !WifiProfileInspector.IsEnterprise(existing))
            {
                _logger.LogInformation(
                    "凭据库条目 {Ssid} 标记为企业网，但现有 Windows 配置不是 802.1X；保留系统配置并使用临时连接路径",
                    entry.Ssid);
                return false;
            }
        }

        var observed = _wifi.GetAdapters()
            .SelectMany(adapter => _wifi.GetLastScan(adapter.InterfaceGuid)?.Networks ?? Array.Empty<ScannedNetwork>())
            .FirstOrDefault(network => string.Equals(network.Ssid, entry.Ssid, StringComparison.OrdinalIgnoreCase));
        return observed is null || WifiProfileInspector.IsEnterpriseSecurity(observed.Security);
    }

    private Guid? SelectAutoConnectOwner(IReadOnlyList<WifiAdapterInfo> availableAdapters)
    {
        var physicalGuids = _devicesSnapshot
            .Where(device => device.Record.IsPresent && device.Classification.IsPhysical &&
                             device.Classification.Category == DeviceCategory.PhysicalWifi)
            .Select(device => device.Record.NetCfgInstanceId)
            .Where(value => Guid.TryParse(value, out _))
            .Select(value => Guid.Parse(value!));

        return ExclusiveAutoConnectPolicy.SelectOwner(
            physicalGuids,
            availableAdapters.Select(adapter => adapter.InterfaceGuid));
    }

    private WifiNetworkCredential CredentialForAdapter(WifiNetworkCredential entry, Guid interfaceGuid)
    {
        if (_config.Wifi.AllowSameSsidOnMultipleAdapters || !entry.ConnectAutomatically)
        {
            return entry;
        }

        var owner = SelectAutoConnectOwner(_wifi.GetAdapters());
        return CloneCredential(entry, interfaceGuid == owner);
    }

    private static WifiNetworkCredential CloneCredential(WifiNetworkCredential source, bool connectAutomatically)
    {
        var profileXml = source.ProfileXmlOverride;
        if (!string.IsNullOrWhiteSpace(profileXml))
        {
            profileXml = profileXml.Replace(
                connectAutomatically ? "<connectionMode>manual</connectionMode>" : "<connectionMode>auto</connectionMode>",
                connectAutomatically ? "<connectionMode>auto</connectionMode>" : "<connectionMode>manual</connectionMode>",
                StringComparison.OrdinalIgnoreCase);
        }

        return new WifiNetworkCredential
        {
            Id = source.Id,
            Ssid = source.Ssid,
            ProfileName = source.ProfileName,
            Kind = source.Kind,
            Security = source.Security,
            Auth = source.Auth,
            Eap = source.Eap,
            Identity = source.Identity,
            AnonymousIdentity = source.AnonymousIdentity,
            Domain = source.Domain,
            Password = source.Password,
            PasswordProtected = source.PasswordProtected,
            PasswordDecryptionFailed = source.PasswordDecryptionFailed,
            PasswordUpdatedUtc = source.PasswordUpdatedUtc,
            UseWinLogonCredentials = source.UseWinLogonCredentials,
            ServerNames = source.ServerNames.ToList(),
            TrustedRootCaThumbprints = source.TrustedRootCaThumbprints.ToList(),
            CertificateThumbprint = source.CertificateThumbprint,
            DisableUserPromptForServerValidation = source.DisableUserPromptForServerValidation,
            ConnectAutomatically = connectAutomatically,
            Hidden = source.Hidden,
            Enabled = source.Enabled,
            ProfileXmlOverride = profileXml,
            AppliedFingerprint = source.AppliedFingerprint,
            LastAppliedUtc = source.LastAppliedUtc,
            Notes = source.Notes,
        };
    }

    /// <summary>Removes the generated profile from one adapter (used when the user deletes an entry).</summary>
    public WlanOperationResult RemoveWifiProfile(Guid interfaceGuid, string profileName) =>
        _profiles.Remove(interfaceGuid, profileName);

    /// <summary>
    /// Removes the generated profile from every adapter and reports what was actually removed.
    /// </summary>
    /// <remarks>
    /// "Remove it from the system" has to be adapter-aware: a profile written to one adapter lives on that
    /// adapter, and the WLAN API is the only reliable way to ask which one still has it. The reported counts
    /// come from an API read-back, so a delete that silently did nothing cannot look like cleanup.
    /// </remarks>
    public WifiProfileRemoveResult RemoveWifiProfileEverywhere(string profileName)
    {
        var result = _profiles.RemoveEverywhere(profileName);
        _logger.LogInformation("删除系统内 802.1X 配置 {Profile}：{Result}", profileName, result.Describe());
        Notification?.Invoke(this, $"{profileName}：{result.Describe()}");
        _forceEnumeration = true;
        RequestImmediateCycle();
        return result;
    }

    public void OpenLogFolder()
    {
        try
        {
            GuardianPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = GuardianPaths.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to open the log folder");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }

            if (_loop is not null)
            {
                await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }

            if (_radioWatchdog is not null)
            {
                await Task.WhenAny(_radioWatchdog, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }

            if (_routeWatchdog is not null)
            {
                await Task.WhenAny(_routeWatchdog, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while stopping the monitor loop");
        }

        _wifi.NotificationReceived -= OnWlanNotification;
        _radio.StateChanged -= OnRadioStateChanged;
        _engine.RecoveryActionPerformed -= OnRecoveryActionPerformed;

        _deviceNotifications.Dispose();
        _wifi.Dispose();
        _radio.Dispose();
        _probe.Dispose();
        _npcap.Dispose();
        _cycleGate.Dispose();
        _cycleSignal.Dispose();
        _cts?.Dispose();

        _logger.LogInformation("Guardian host disposed");

        // The file writer and the log sink are owned by the application host, not by this service:
        // disposing them here would (a) lose every line the host writes afterwards - the provider
        // silently reopens a new file - and (b) hide the shutdown sequence from the log.
    }
}
