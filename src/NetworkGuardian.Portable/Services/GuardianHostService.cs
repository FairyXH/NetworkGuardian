using Microsoft.Extensions.Logging;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Logging;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Infrastructure.Configuration;
using NetworkGuardian.Infrastructure.Logging;
using NetworkGuardian.Infrastructure.Processes;
using NetworkGuardian.Windows.Connectivity;
using NetworkGuardian.Windows.Devices;
using NetworkGuardian.Windows.Location;
using NetworkGuardian.Windows.Network;
using NetworkGuardian.Windows.Radio;
using NetworkGuardian.Windows.Wlan;

namespace NetworkGuardian.Portable.Services;

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
    private readonly ConnectivityProbe _probe;
    private readonly PhysicalDeviceManager _devices;
    private readonly ExternalCommandRunner _commands;
    private readonly LocationPermissionService _location;
    private readonly DeviceNotificationWatcher _deviceNotifications;
    private readonly NetworkDeviceClassifier _classifier = new();
    private readonly GuardianDecisionEngine _engine;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly Random _jitter = new();

    /// <summary>Decision notes that are already in the log, so a persistent condition is logged once.</summary>
    private readonly HashSet<string> _loggedNotes = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private GuardianConfig _config;
    private GuardianSnapshot _snapshot;
    private DateTimeOffset _lastEnumerationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProbeUtc = DateTimeOffset.MinValue;
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
    private Dictionary<Guid, (DateTimeOffset AtUtc, string Profile)> _lastConnectAttempt = new();
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
        _probe = new ConnectivityProbe(loggerFactory.CreateLogger<ConnectivityProbe>());
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

        _engine = new GuardianDecisionEngine(_config, loggerFactory.CreateLogger<GuardianDecisionEngine>());

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
            _loggedNotes.Clear();
            return;
        }

        foreach (var note in notes)
        {
            if (_loggedNotes.Add(note))
            {
                _logger.LogInformation("决策提示: {Note}", note);
            }
        }

        // Forget notes that no longer apply so they are reported again if the condition returns.
        _loggedNotes.IntersectWith(notes);
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

        _logger.LogInformation("Guardian host started (config {Path})", _configStore.ConfigPath);
    }

    /// <summary>Signals the monitor loop to re-evaluate immediately.</summary>
    public void RequestImmediateCycle()
    {
        _forceProbe = true;
        _forceEnumeration = true;
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
        _config = config;

        await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        ApplyConfigToComponents(config);
        RequestImmediateCycle();
        _logger.LogInformation("Configuration updated and saved to {Path}", _configStore.ConfigPath);
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
                var wifiWait = _wifi.WaitForNotificationAsync(heartbeat, cancellationToken);
                var deviceWait = _deviceNotifications.WaitAsync(heartbeat, cancellationToken);
                await Task.WhenAny(wifiWait, deviceWait, Task.Delay(heartbeat, cancellationToken))
                    .ConfigureAwait(false);
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
            _deviceNotifications.Drain(out var deviceChanges);
            if (deviceChanges.Count > 0)
            {
                foreach (var change in deviceChanges)
                {
                    _logger.LogDebug("PnP device change: {Change}", change);
                }

                _forceEnumeration = true;
            }

            var enumerationInterval = TimeSpan.FromSeconds(Math.Max(30, _config.General.EnumerationRefreshSeconds));
            if (_forceEnumeration || now - _lastEnumerationUtc >= enumerationInterval)
            {
                await RefreshEnumerationAsync(cancellationToken).ConfigureAwait(false);
                _lastEnumerationUtc = DateTimeOffset.UtcNow;
                _forceEnumeration = false;
            }

            var probeInterval = TimeSpan.FromSeconds(Math.Max(3, _config.Probe.IntervalSeconds));
            var probeDue = _forceProbe || now - _lastProbeUtc >= probeInterval;
            if (probeDue && now >= _resumeQuietUntilUtc)
            {
                await RefreshProbesAsync(cancellationToken).ConfigureAwait(false);
                _lastProbeUtc = DateTimeOffset.UtcNow;
                _forceProbe = false;
            }

            var adapters = BuildAdapterStates();
            var interfaces = BuildInterfaceStates();

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
            };

            _manualScanRequested = false;

            var decision = _engine.Evaluate(input);

            LogDecisionNotes(decision.Notes);

            if (!IsPaused && _config.General.AutomaticRecovery)
            {
                foreach (var action in decision.Actions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ExecuteActionAsync(action, cancellationToken).ConfigureAwait(false);
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
            "{PhysicalCount} classified physical",
            adapterCount, _devicesSnapshot.Count, physical);
    }

    private async Task RefreshProbesAsync(CancellationToken cancellationToken)
    {
        var request = new ProbeRequest
        {
            Endpoints = _config.ProbeEndpoints,
            Settings = _config.Probe,
        };

        _globalProbe = await _probe.ProbeAsync(request, cancellationToken).ConfigureAwait(false);

        var byAdapter = new Dictionary<Guid, ConnectivityProbeReport>();
        var byInterfaceId = new Dictionary<string, ConnectivityProbeReport>(StringComparer.Ordinal);
        var interfaces = _interfaces.GetInterfaces();

        if (_config.Probe.PerInterfaceProbing)
        {
            var candidates = interfaces
                .Where(i => i.Kind is InterfaceKind.Wifi or InterfaceKind.Ethernet)
                .Where(i => i.IsUp && i.HasUsableIpv4 && i.PrimaryIpv4Address is not null)
                .ToList();

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var interfaceRequest = request with
                {
                    SourceAddress = candidate.PrimaryIpv4Address,
                    InterfaceId = candidate.Id,
                    GatewayAddress = candidate.PrimaryGateway,
                };

                var report = await _probe.ProbeAsync(interfaceRequest, cancellationToken).ConfigureAwait(false);
                if (candidate.WlanInterfaceGuid is { } guid)
                {
                    byAdapter[guid] = report;
                }

                byInterfaceId[candidate.Id] = report;
            }
        }

        _wifiProbeByAdapter = byAdapter;
        _probeByInterfaceId = byInterfaceId;
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
        var routes = _interfaces.GetDefaultRoutes();
        _defaultRoutes = routes;
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

            var isDefault = routes.Any(r => r.InterfaceLuid is { } luid &&
                                            state.Id == $"luid:{luid}");
            var route = routes.FirstOrDefault(r => r.InterfaceLuid is { } luid && state.Id == $"luid:{luid}");

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
                        _logger.LogInformation("Enabled physical Wi-Fi device {Device}: {Detail}",
                            enable.DeviceInstanceId, result.Detail);
                        Notification?.Invoke(this, $"已启用网卡 {enable.FriendlyName}");
                        await Task.Delay(
                                TimeSpan.FromSeconds(Math.Max(1, _config.Recovery.DeviceEnableSettleSeconds)),
                                cancellationToken)
                            .ConfigureAwait(false);
                        _forceEnumeration = true;
                        _wifi.RefreshAdapters();
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

                    _engine.NotifyScanFinished(scan.InterfaceGuid, DateTimeOffset.UtcNow, scan.Force);

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

                    var result = await _wifi.ConnectAsync(
                            connect.InterfaceGuid, connect.ProfileName, connect.Bssid, cancellationToken)
                        .ConfigureAwait(false);

                    _engine.NotifyConnectResult(
                        connect.InterfaceGuid, connect.ProfileName, result.Success, result.Failure, DateTimeOffset.UtcNow);

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

                    if (result.Success)
                    {
                        _logger.LogInformation("Command {Name} completed ({Result})", definition.Name, result);
                    }
                    else
                    {
                        _logger.LogWarning("Command {Name} did not succeed: {Result} {Failure}",
                            definition.Name, result, result.Failure);
                    }

                    Notification?.Invoke(this, run.IsCampusAuth
                        ? $"已执行校园网认证：{definition.Name}"
                        : $"已执行命令：{definition.Name}");

                    if (definition.WaitAfterRunSeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(definition.WaitAfterRunSeconds), cancellationToken)
                            .ConfigureAwait(false);
                    }

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

                case ApplyInterfaceMetricsAction:
                {
                    var notes = await _interfaces.ApplyInterfaceMetricsAsync(_config, cancellationToken)
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
            _engine.NotifyScanFinished(interfaceGuid, DateTimeOffset.UtcNow, forcedByUser: true);
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
            var report = await _probe.ProbeAsync(
                    new ProbeRequest { Endpoints = _config.ProbeEndpoints, Settings = _config.Probe },
                    cancellationToken)
                .ConfigureAwait(false);

            _globalProbe = report;
            _lastProbeUtc = DateTimeOffset.UtcNow;
            _forceEnumeration = true;
            return report;
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
        _cycleGate.Dispose();
        _cts?.Dispose();

        _logger.LogInformation("Guardian host disposed");

        // The file writer and the log sink are owned by the application host, not by this service:
        // disposing them here would (a) lose every line the host writes afterwards - the provider
        // silently reopens a new file - and (b) hide the shutdown sequence from the log.
    }
}
