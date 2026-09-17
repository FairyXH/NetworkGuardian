using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

/// <summary>Engine level counters surfaced in the UI and logs.</summary>
public sealed record EngineDiagnostics
{
    public required RecoveryState State { get; init; }

    public required int ConsecutiveInternetFailures { get; init; }

    public required int ConsecutiveInternetSuccesses { get; init; }

    public required bool InternetFailing { get; init; }

    public required int ConsecutiveEthernetFailures { get; init; }

    public required bool EthernetFailing { get; init; }

    public required int CampusAuthRunsLastHour { get; init; }

    public required int CampusAuthConsecutiveRuns { get; init; }

    public required DateTimeOffset? LastCampusAuthUtc { get; init; }

    public required TimeSpan NextRecoveryBackoff { get; init; }

    public required IReadOnlyList<string> BannedProfiles { get; init; }

    public required IReadOnlyDictionary<string, string> AdapterStates { get; init; }
}

/// <summary>
/// The recovery brain. It is a pure function of (runtime state, configuration, history) and returns
/// a list of intents; the host executes them. Nothing here touches the operating system, which is
/// what makes the sticky-connection guarantees unit testable.
/// </summary>
public sealed class GuardianDecisionEngine
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly CandidateSelector _selector;
    private readonly ConnectFailureBlacklist _blacklist;
    private readonly NetworkDeviceClassifier _classifier;
    private readonly RecoveryStateMachine _stateMachine;
    private readonly Dictionary<Guid, AdapterPolicyState> _adapters = new();
    private readonly Dictionary<string, SlidingWindowRateLimiter> _commandLimiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly FailureTracker _internetTracker;
    private readonly FailureTracker _ethernetTracker;
    private readonly SlidingWindowRateLimiter _radioLimiter;
    private readonly SlidingWindowRateLimiter _deviceEnableLimiter;
    private readonly ExponentialBackoff _recoveryBackoff;

    private DateTimeOffset? _lastCampusAuthUtc;
    private DateTimeOffset? _lastRecoveryActionUtc;
    private string? _lastRecoveryAction;
    private DateTimeOffset? _lastInternetSuccessUtc;
    private bool _internetWasOnline;

    public GuardianDecisionEngine(
        GuardianConfig config,
        ILogger<GuardianDecisionEngine>? logger = null,
        IClock? clock = null)
    {
        _logger = logger ?? NullLogger<GuardianDecisionEngine>.Instance;
        var now = (clock ?? SystemClock.Instance).UtcNow;

        _blacklist = new ConnectFailureBlacklist(TimeSpan.FromSeconds(config.Recovery.ConnectFailureBlacklistSeconds));
        _selector = new CandidateSelector(_blacklist);
        _classifier = new NetworkDeviceClassifier();
        _stateMachine = new RecoveryStateMachine(now);

        _internetTracker = new FailureTracker("internet", config.Recovery.InternetFailureThreshold, config.Recovery.InternetRecoveryThreshold);
        _ethernetTracker = new FailureTracker("ethernet", config.Ethernet.FailureThreshold, config.Recovery.InternetRecoveryThreshold);

        _radioLimiter = new SlidingWindowRateLimiter(
            "wifi-radio", maxRunsPerHour: 12, minInterval: TimeSpan.FromSeconds(30), maxConsecutiveRuns: 6);
        _deviceEnableLimiter = new SlidingWindowRateLimiter(
            "wifi-device-enable",
            maxRunsPerHour: config.Recovery.MaxDeviceEnablePerHour,
            minInterval: TimeSpan.FromSeconds(Math.Max(20, config.Recovery.DeviceEnableSettleSeconds * 3)),
            maxConsecutiveRuns: 4);

        _recoveryBackoff = new ExponentialBackoff(
            "recovery", config.Recovery.BaseBackoffSeconds, config.Recovery.MaxBackoffSeconds);

        ApplyConfig(config);
    }

    public RecoveryStateMachine StateMachine => _stateMachine;

    public ConnectFailureBlacklist Blacklist => _blacklist;

    public NetworkDeviceClassifier Classifier => _classifier;

    public string? LastRecoveryAction => _lastRecoveryAction;

    public DateTimeOffset? LastRecoveryActionUtc => _lastRecoveryActionUtc;

    public DateTimeOffset? LastCampusAuthUtc => _lastCampusAuthUtc;

    public DateTimeOffset? LastInternetSuccessUtc => _lastInternetSuccessUtc;

    public event EventHandler<string>? RecoveryActionPerformed;

    /// <summary>Applies configuration changes without losing accumulated history.</summary>
    public void ApplyConfig(GuardianConfig config)
    {
        lock (_gate)
        {
            _internetTracker.FailureThreshold = config.Recovery.InternetFailureThreshold;
            _internetTracker.RecoveryThreshold = config.Recovery.InternetRecoveryThreshold;
            _ethernetTracker.FailureThreshold = config.Ethernet.FailureThreshold;
            _ethernetTracker.RecoveryThreshold = config.Recovery.InternetRecoveryThreshold;
            _blacklist.BanDuration = TimeSpan.FromSeconds(config.Recovery.ConnectFailureBlacklistSeconds);

            _deviceEnableLimiter.MaxRunsPerHour = config.Recovery.MaxDeviceEnablePerHour;
            _deviceEnableLimiter.MinInterval = TimeSpan.FromSeconds(Math.Max(20, config.Recovery.DeviceEnableSettleSeconds * 3));

            foreach (var adapter in _adapters.Values)
            {
                adapter.ApplyConfig(config.Recovery, config.General);
            }
        }
    }

    /// <summary>Called when the user manually triggers a recovery round: clears derating state.</summary>
    public void ResetDerating(DateTimeOffset now)
    {
        lock (_gate)
        {
            _blacklist.Reset();
            _internetTracker.Reset(now);
            _ethernetTracker.Reset(now);
            _recoveryBackoff.Reset();
            _radioLimiter.Reset();
            _deviceEnableLimiter.Reset();
            foreach (var limiter in _commandLimiters.Values)
            {
                limiter.Reset();
            }

            foreach (var adapter in _adapters.Values)
            {
                adapter.ConnectLimiter.Reset();
                adapter.ScanLimiter.Reset();
            }
        }
    }

    public void NotifyConnectResult(Guid interfaceGuid, string profileName, bool success, string? failure, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = GetAdapterState(interfaceGuid, null);
            state.PendingConnectUtc = null;
            state.PendingConnectProfile = null;

            if (success)
            {
                _blacklist.RecordSuccess(interfaceGuid, profileName, now);
                state.LastSuccessfulConnectionUtc = now;
                state.LastConnectedProfile = profileName;
                state.ConnectLimiter.NotifySuccess();
                _logger.LogInformation("Connect succeeded on {Adapter} using {Profile}", interfaceGuid, profileName);
            }
            else
            {
                _blacklist.RecordFailure(interfaceGuid, profileName, now, failure);
                _logger.LogWarning("Connect failed on {Adapter} using {Profile}: {Failure}",
                    interfaceGuid, profileName, failure);
            }
        }
    }

    public void NotifyScanFinished(Guid interfaceGuid, DateTimeOffset now, bool forcedByUser)
    {
        lock (_gate)
        {
            var state = GetAdapterState(interfaceGuid, null);
            state.LastScanRequestUtc = now;
            if (forcedByUser)
            {
                state.ScanLimiter.Reset();
            }
        }
    }

    public void NotifyDeviceOperation(string operation, bool success, DateTimeOffset now)
    {
        _ = now;
        _logger.LogDebug("Device operation {Operation} success={Success}", operation, success);
    }

    public GuardianDecision Evaluate(GuardianInput input)
    {
        lock (_gate)
        {
            return EvaluateCore(input);
        }
    }

    private GuardianDecision EvaluateCore(GuardianInput input)
    {
        var now = input.Now;
        var config = input.Config;
        ApplyConfig(config);

        var actions = new List<GuardianAction>();
        var notes = new List<string>();

        if (input.IsPaused)
        {
            _stateMachine.Transition(RecoveryState.Paused, now, "automatic recovery is paused");
            notes.Add("Automatic recovery is paused.");
            return new GuardianDecision
            {
                State = RecoveryState.Paused,
                Actions = actions,
                Notes = notes,
                Connectivity = ClassifyConnectivity(input, internetOnline: input.GlobalProbe.IsOnline),
                EvaluatedAtUtc = now,
            };
        }

        var probe = input.GlobalProbe;
        var internetOnline = probe.IsOnline;

        // ---------- Internet debounce ----------
        if (internetOnline)
        {
            _lastInternetSuccessUtc = now;
            if (!_internetWasOnline)
            {
                _logger.LogInformation("Internet restored (probe {Summary})", probe.Summary);
                _internetWasOnline = true;
            }

            if (_internetTracker.RecordSuccess(now))
            {
                _stateMachine.Transition(RecoveryState.Healthy, now, "connectivity probe recovered");
            }

            _recoveryBackoff.Reset();
            _blacklist.ClearExpired(now);
            foreach (var limiter in _commandLimiters.Values)
            {
                limiter.NotifySuccess();
            }
        }
        else
        {
            if (_internetWasOnline)
            {
                _logger.LogWarning("Internet lost (probe {Summary})", probe.Summary);
                _internetWasOnline = false;
            }

            _internetTracker.RecordFailure(now);
        }

        var connectivity = ClassifyConnectivity(input, internetOnline);

        if (input.ManualScanRequested)
        {
            notes.Add("Manual scan requested from the UI.");
        }

        // ---------- Wi-Fi radio ----------
        var radioOn = input.Radio.State == RadioState.On;
        var radioUsable = radioOn || input.Radio.State == RadioState.Unknown;

        if (input.Radio.State == RadioState.Off)
        {
            if (config.General.AutoEnableWifiRadio)
            {
                if (_radioLimiter.TryAcquire(now, out var radioRetry, out var radioReason))
                {
                    _radioLimiter.RecordRun(now);
                    actions.Add(new EnableWifiRadioAction
                    {
                        Reason = "Wi-Fi radio is off and auto-enable is configured",
                        Force = true,
                    });
                    _stateMachine.Transition(RecoveryState.EnablingWifiRadio, now, "Wi-Fi radio is off");
                }
                else
                {
                    notes.Add(radioReason);
                    _stateMachine.Transition(RecoveryState.WifiRadioOff, now, radioReason);
                }
            }
            else
            {
                notes.Add("Wi-Fi radio is off and autoEnableWifiRadio is disabled.");
                _stateMachine.Transition(RecoveryState.WifiRadioOff, now, "Wi-Fi radio off (auto-enable disabled)");
            }
        }

        // ---------- Disabled physical Wi-Fi devices ----------
        var wifiDevices = input.Devices
            .Where(d => d.Record.IsPresent && d.Classification.IsPhysical &&
                        d.Classification.Category == DeviceCategory.PhysicalWifi)
            .ToList();
        var disabledDevices = wifiDevices.Where(d => !d.IsEnabled).ToList();

        if (disabledDevices.Count > 0 && config.General.AutoEnableWifiDevices)
        {
            foreach (var device in disabledDevices)
            {
                var key = $"enable-device:{device.Record.DeviceInstanceId}";
                var limiter = GetCommandLimiter(key, config.Recovery.MaxDeviceEnablePerHour,
                    TimeSpan.FromSeconds(Math.Max(20, config.Recovery.DeviceEnableSettleSeconds * 3)), 3);

                if (limiter.TryAcquire(now, out var retry, out var reason))
                {
                    limiter.RecordRun(now);
                    actions.Add(new EnableWifiDeviceAction
                    {
                        DeviceInstanceId = device.Record.DeviceInstanceId,
                        FriendlyName = device.Record.FriendlyName ?? device.Record.DeviceDescription ?? "Wi-Fi adapter",
                        Reason = $"physical Wi-Fi device is present but disabled (problemCode={device.Record.ProblemCode})",
                    });
                    _stateMachine.Transition(RecoveryState.EnablingWifiDevices, now, "disabled physical Wi-Fi device detected");
                }
                else
                {
                    notes.Add(reason);
                }
            }

            if (actions.OfType<EnableWifiDeviceAction>().Any())
            {
                actions.Add(new WaitAction
                {
                    Delay = TimeSpan.FromSeconds(config.Recovery.DeviceEnableSettleSeconds),
                    Reason = "waiting for the re-enabled Wi-Fi device to register its WLAN interface",
                });
                return BuildDecision(now, actions, notes, connectivity);
            }
        }
        else if (disabledDevices.Count > 0)
        {
            notes.Add($"{disabledDevices.Count} physical Wi-Fi device(s) are disabled; autoEnableWifiDevices is off.");
        }

        // ---------- Ethernet / campus authentication ----------
        var ethernetInterfaces = input.Interfaces
            .Where(i => i.Kind == InterfaceKind.Ethernet)
            .ToList();
        var ethernetEligible = ethernetInterfaces.Any(i => i.IsUp || i.HasUsableIpv4);
        var ethernetWithInternet = ethernetInterfaces.Any(i => i.Probe?.IsOnline == true);

        if (ethernetEligible && !internetOnline)
        {
            _ethernetTracker.RecordFailure(now);
        }
        else
        {
            _ethernetTracker.RecordSuccess(now);
        }

        var campusAuthConfigured = config.CampusAuth.Enabled &&
                                   !string.IsNullOrWhiteSpace(config.CampusAuth.ExecutablePath);
        var captivePortal = probe.CaptivePortalSuspected && internetOnline == false;
        var internetFailures = _internetTracker.ConsecutiveFailures;

        var shouldAuth = campusAuthConfigured &&
                         config.Ethernet.Enabled &&
                         config.Ethernet.AuthenticateWhenLinkUpButOffline &&
                         (!internetOnline || captivePortal) &&
                         internetFailures >= config.CampusAuth.TriggerAfterConsecutiveFailures &&
                         (ethernetEligible || !config.CampusAuth.RequireEthernetLink) &&
                         (!captivePortal || config.CampusAuth.RunOnCaptivePortal);

        if (shouldAuth)
        {
            var command = config.CampusAuth.ToCommandDefinition();
            var limiter = GetCommandLimiter("campus-auth", config.CampusAuth.MaxRunsPerHour,
                TimeSpan.FromSeconds(config.CampusAuth.MinIntervalSeconds), config.CampusAuth.MaxConsecutiveRuns);

            if (limiter.TryAcquire(now, out var retry, out var reason))
            {
                limiter.RecordRun(now);
                _lastCampusAuthUtc = now;
                actions.Add(new RunExternalCommandAction
                {
                    CommandId = "campus-auth",
                    CommandName = command.Name,
                    ExecutablePath = command.ExecutablePath,
                    Kind = command.Kind,
                    IsCampusAuth = true,
                    Reason = captivePortal
                        ? "captive portal detected while Ethernet link is up"
                        : $"{internetFailures} consecutive Internet probe failures with Ethernet link up",
                });

                _stateMachine.Transition(RecoveryState.Authenticating, now, "starting campus authenticator");
                actions.Add(new WaitAction
                {
                    Delay = TimeSpan.FromSeconds(Math.Max(1, config.CampusAuth.WaitAfterRunSeconds)),
                    Reason = "waiting for the campus authenticator before re-probing",
                });
                _stateMachine.Transition(RecoveryState.WaitingForAuthentication, now, "campus authenticator launched");
                RecordRecoveryAction($"Campus auth ({command.Name}) launched");
                return BuildDecision(now, actions, notes, connectivity);
            }

            notes.Add(reason);
        }

        // ---------- User defined offline commands ----------
        if (!internetOnline)
        {
            foreach (var command in config.OfflineCommands.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.ExecutablePath)))
            {
                if (internetFailures < 1 && !captivePortal)
                {
                    continue;
                }

                var limiter = GetCommandLimiter(command.Id, command.MaxRunsPerHour,
                    TimeSpan.FromSeconds(command.MinIntervalSeconds), command.MaxConsecutiveRuns);

                if (!limiter.TryAcquire(now, out var retry, out var reason))
                {
                    notes.Add($"{command.Name}: {reason} (retry in {retry.TotalSeconds:F0}s)");
                    continue;
                }

                limiter.RecordRun(now);
                actions.Add(new RunExternalCommandAction
                {
                    CommandId = command.Id,
                    CommandName = command.Name,
                    ExecutablePath = command.ExecutablePath,
                    Kind = command.Kind,
                    IsCampusAuth = false,
                    Reason = $"Internet is down ({internetFailures} consecutive failures)",
                });

                if (command.WaitAfterRunSeconds > 0)
                {
                    actions.Add(new WaitAction
                    {
                        Delay = TimeSpan.FromSeconds(command.WaitAfterRunSeconds),
                        Reason = $"waiting for offline command '{command.Name}' to take effect",
                    });
                }
            }
        }

        // ---------- Per-adapter Wi-Fi handling ----------
        var reconnectNeeded = new List<AdapterCandidateSet>();
        var wifiNotes = new List<string>();

        var physicalWifiGuids = new HashSet<Guid>(
            wifiDevices
                .Select(d => d.Record.NetCfgInstanceId)
                .Where(s => Guid.TryParse(s, out _))
                .Select(s => Guid.Parse(s!)));

        var adapters = input.WifiAdapters
            .Where(a => physicalWifiGuids.Count == 0 || physicalWifiGuids.Contains(a.InterfaceGuid))
            .ToList();

        foreach (var staleGuid in _adapters.Keys.Where(g => adapters.All(a => a.InterfaceGuid != g)).ToList())
        {
            // Adapter disappeared (USB unplugged): forget its history so a later re-plug starts clean.
            _adapters.Remove(staleGuid);
            _logger.LogInformation("Adapter {Adapter} no longer present; policy state cleared", staleGuid);
        }

        foreach (var adapter in adapters)
        {
            var state = GetAdapterState(adapter.InterfaceGuid, config);
            state.ApplyConfig(config.Recovery, config.General);

            var iface = input.Interfaces.FirstOrDefault(i => i.WlanInterfaceGuid == adapter.InterfaceGuid);
            var usable = DetermineAdapterUsable(input, adapter, iface, internetOnline);

            if (adapter.IsConnected)
            {
                state.DisconnectedSinceUtc = null;
                state.LastKnownSsid = adapter.CurrentSsid;
                if (!string.IsNullOrWhiteSpace(adapter.Connection?.ProfileName))
                {
                    state.LastConnectedProfile = adapter.Connection!.ProfileName;
                }

                if (usable)
                {
                    state.Connectivity.RecordSuccess(now);
                    if (!string.IsNullOrWhiteSpace(adapter.Connection?.ProfileName))
                    {
                        _blacklist.RecordSuccess(adapter.InterfaceGuid, adapter.Connection!.ProfileName, now);
                    }

                    continue;
                }

                // Connected but not passing traffic: only now may we consider a change.
                state.Connectivity.RecordFailure(now);
                wifiNotes.Add($"{adapter.Description}: connected to '{adapter.CurrentSsid}' but traffic is failing " +
                              $"({state.Connectivity.ConsecutiveFailures}/{config.Recovery.WifiFailureThreshold})");

                if (!config.Wifi.StickyConnection || !config.Wifi.RecoverStaleConnections)
                {
                    continue;
                }

                if (!state.Connectivity.IsFailing)
                {
                    continue;
                }

                if (!state.ConnectLimiter.TryAcquire(now, out var retry, out var reason))
                {
                    wifiNotes.Add($"{adapter.Description}: {reason}");
                    continue;
                }

                state.ConnectLimiter.RecordRun(now);
                state.LastDisconnectActionUtc = now;
                state.LastKnownSsid = adapter.CurrentSsid;
                if (!string.IsNullOrWhiteSpace(adapter.Connection?.ProfileName))
                {
                    _blacklist.RecordFailure(adapter.InterfaceGuid, adapter.Connection!.ProfileName, now,
                        "connection stopped passing traffic");
                }

                actions.Add(new DisconnectWifiAction
                {
                    InterfaceGuid = adapter.InterfaceGuid,
                    Ssid = adapter.CurrentSsid ?? string.Empty,
                    Reason = $"connected but {state.Connectivity.ConsecutiveFailures} consecutive probes failed",
                });

                _stateMachine.Transition(RecoveryState.Recovering, now, "stale Wi-Fi connection detected");
                RecordRecoveryAction($"{adapter.Description}: disconnected stale connection to {adapter.CurrentSsid}");
                actions.Add(new ScanAdapterAction
                {
                    InterfaceGuid = adapter.InterfaceGuid,
                    Force = true,
                    Reason = "re-scan after dropping a stale connection",
                });
                state.ScanLimiter.Reset();
                state.ScanLimiter.RecordRun(now);
                state.LastScanRequestUtc = now;
                continue;
            }

            // Not connected.
            state.Connectivity.Reset(now);
            state.DisconnectedSinceUtc ??= now;
            var disconnectedFor = now - state.DisconnectedSinceUtc.Value;

            if (!radioUsable)
            {
                wifiNotes.Add($"{adapter.Description}: radio is off; Wi-Fi recovery is suspended");
                continue;
            }

            if (disconnectedFor < TimeSpan.FromSeconds(config.Wifi.DisconnectGraceSeconds))
            {
                wifiNotes.Add($"{adapter.Description}: disconnected for {disconnectedFor.TotalSeconds:F0}s, " +
                              $"within the {config.Wifi.DisconnectGraceSeconds}s grace period");
                continue;
            }

            var scan = adapter.LastScan;
            var scanAge = scan?.CompletedAtUtc is { } completed ? now - completed : (TimeSpan?)null;
            var scanIsFresh = scan is { Completed: true } && scanAge is { } age &&
                              age < TimeSpan.FromSeconds(Math.Max(30, config.Wifi.DisconnectGraceSeconds * 4));

            var wantScan = !scanIsFresh ||
                           adapter.IsScanInProgress == false && state.LastScanRequestUtc is null ||
                           input.ManualScanRequested;

            if (wantScan)
            {
                if (state.ScanLimiter.TryAcquire(now, out var scanRetry, out var scanReason))
                {
                    state.ScanLimiter.RecordRun(now);
                    state.LastScanRequestUtc = now;
                    actions.Add(new ScanAdapterAction
                    {
                        InterfaceGuid = adapter.InterfaceGuid,
                        Force = input.ManualScanRequested,
                        Reason = scanIsFresh ? "manual rescan requested" : "adapter has no usable connection",
                    });
                    _stateMachine.Transition(RecoveryState.WifiScanning, now, "scanning a disconnected adapter");
                }
                else
                {
                    wifiNotes.Add($"{adapter.Description}: {scanReason} (retry in {scanRetry.TotalSeconds:F0}s)");
                }

                // Even if the scan is throttled we may still connect from the previous results.
            }

            if (!scanIsFresh && !adapter.ProfileListKnown && scan is null)
            {
                continue;
            }

            var candidates = _selector.SelectCandidates(
                adapter.InterfaceGuid,
                scan,
                adapter.SavedProfiles,
                config.Wifi,
                now,
                out var rejections,
                state.LastConnectedProfile ?? state.LastKnownSsid,
                excludeSsid: null);

            if (config.Logging.VerboseNetwork && rejections.Count > 0)
            {
                wifiNotes.Add($"{adapter.Description}: rejected {rejections.Count} network(s): " +
                              string.Join("; ", rejections.Take(5)));
            }

            if (candidates.Count == 0)
            {
                wifiNotes.Add($"{adapter.Description}: no saved and visible candidate networks");
                continue;
            }

            reconnectNeeded.Add(new AdapterCandidateSet(adapter.InterfaceGuid, candidates));
        }

        if (reconnectNeeded.Count > 0)
        {
            var assignments = AdapterAssignmentPlanner.Plan(reconnectNeeded, config.Wifi.AllowSameSsidOnMultipleAdapters);

            foreach (var assignment in assignments)
            {
                var adapter = adapters.First(a => a.InterfaceGuid == assignment.InterfaceGuid);
                var state = GetAdapterState(assignment.InterfaceGuid, config);

                if (!state.ConnectLimiter.TryAcquire(now, out var retry, out var reason))
                {
                    wifiNotes.Add($"{adapter.Description}: connect throttled - {reason} (retry in {retry.TotalSeconds:F0}s)");
                    continue;
                }

                state.ConnectLimiter.RecordRun(now);
                state.PendingConnectUtc = now;
                state.PendingConnectProfile = assignment.Candidate.ProfileName;

                actions.Add(new ConnectWifiAction
                {
                    InterfaceGuid = assignment.InterfaceGuid,
                    ProfileName = assignment.Candidate.ProfileName,
                    Ssid = assignment.Candidate.Ssid,
                    Bssid = assignment.Candidate.PreferredBssid,
                    Reason = $"best candidate: {assignment.Candidate.ScoreReason}",
                });

                _stateMachine.Transition(RecoveryState.WifiConnecting, now, "connecting a disconnected adapter");
                RecordRecoveryAction($"{adapter.Description}: connecting to {assignment.Candidate.Ssid} " +
                                     $"({assignment.Candidate.SignalQuality}%, {assignment.Candidate.ScoreReason})");

                if (config.General.DhcpWaitSeconds > 0)
                {
                    actions.Add(new WaitAction
                    {
                        Delay = TimeSpan.FromSeconds(config.General.DhcpWaitSeconds),
                        Reason = "waiting for association and DHCP",
                    });
                    _stateMachine.Transition(RecoveryState.WaitingForDhcp, now, "waiting for DHCP after connect");
                }
            }
        }

        notes.AddRange(wifiNotes);

        // ---------- Overall state ----------
        if (actions.Count == 0)
        {
            if (internetOnline)
            {
                _stateMachine.Transition(RecoveryState.Healthy, now, "Internet is reachable");
            }
            else if (ethernetEligible)
            {
                _stateMachine.Transition(RecoveryState.EthernetNoInternet, now, "Ethernet link is up but the Internet is unreachable");
            }
            else if (connectivity == ConnectivityLevel.NoLink)
            {
                _stateMachine.Transition(RecoveryState.Degraded, now, "no managed interface has a link");
            }
            else
            {
                _stateMachine.Transition(RecoveryState.Degraded, now, "Internet is unreachable");
            }

            // Ensure the periodic probe keeps running when offline.
            if (!internetOnline)
            {
                var delay = _recoveryBackoff.DelayFor(Math.Min(_internetTracker.TotalFailures, 8));
                if (delay > TimeSpan.FromSeconds(config.General.HealthSweepSeconds))
                {
                    actions.Add(new WaitAction
                    {
                        Delay = delay,
                        Reason = $"Internet still down; backoff after {_internetTracker.TotalFailures} failures",
                    });
                    _stateMachine.Transition(RecoveryState.Cooldown, now, "recovery backoff");
                }
            }
        }

        return BuildDecision(now, actions, notes, connectivity);
    }

    private static bool DetermineAdapterUsable(
        GuardianInput input,
        WifiAdapterRuntimeState adapter,
        InterfaceRuntimeState? iface,
        bool internetOnline)
    {
        if (!adapter.IsConnected)
        {
            return false;
        }

        if (iface?.Probe is { } perInterface)
        {
            return perInterface.IsOnline;
        }

        // No per-interface probe available: only trust the global state when this adapter is actually
        // carrying traffic (it has a gateway / it is the default route).
        if (iface is null)
        {
            return internetOnline;
        }

        if (!iface.HasUsableIpv4)
        {
            return false;
        }

        if (iface.IsDefaultRoute || iface.HasDefaultGateway)
        {
            return internetOnline;
        }

        // Connected with an address but no default route and no probe: leave it alone. It is not
        // proven broken, and the sticky policy forbids speculative switching.
        return true;
    }

    private ConnectivityLevel ClassifyConnectivity(GuardianInput input, bool internetOnline)
    {
        if (internetOnline)
        {
            return ConnectivityLevel.Online;
        }

        if (input.GlobalProbe.CaptivePortalSuspected)
        {
            return ConnectivityLevel.CaptivePortal;
        }

        var managed = input.Interfaces
            .Where(i => i.Kind is InterfaceKind.Ethernet or InterfaceKind.Wifi)
            .ToList();

        if (managed.Count == 0)
        {
            return ConnectivityLevel.NoLink;
        }

        var up = managed.Where(i => i.IsUp).ToList();
        if (up.Count == 0)
        {
            return ConnectivityLevel.NoLink;
        }

        if (up.All(i => !i.HasUsableIpv4))
        {
            return ConnectivityLevel.NoIpConfiguration;
        }

        if (up.All(i => !i.HasDefaultGateway))
        {
            return ConnectivityLevel.NoDefaultRoute;
        }

        return input.GlobalProbe.AttemptCount > 0
            ? ConnectivityLevel.NoInternet
            : ConnectivityLevel.ProbeFailed;
    }

    private AdapterPolicyState GetAdapterState(Guid interfaceGuid, GuardianConfig? config)
    {
        if (_adapters.TryGetValue(interfaceGuid, out var existing))
        {
            return existing;
        }

        var effective = config ?? new GuardianConfig();
        var created = new AdapterPolicyState(interfaceGuid, effective.Recovery, effective.General);
        _adapters[interfaceGuid] = created;
        return created;
    }

    private SlidingWindowRateLimiter GetCommandLimiter(string key, int maxPerHour, TimeSpan minInterval, int maxConsecutive)
    {
        if (_commandLimiters.TryGetValue(key, out var limiter))
        {
            limiter.MaxRunsPerHour = Math.Max(1, maxPerHour);
            limiter.MinInterval = minInterval;
            limiter.MaxConsecutiveRuns = Math.Max(1, maxConsecutive);
            return limiter;
        }

        var created = new SlidingWindowRateLimiter(key, maxPerHour, minInterval, maxConsecutive);
        _commandLimiters[key] = created;
        return created;
    }

    private void RecordRecoveryAction(string description)
    {
        _lastRecoveryAction = description;
        _lastRecoveryActionUtc = SystemClock.Instance.UtcNow;
        RecoveryActionPerformed?.Invoke(this, description);
    }

    private GuardianDecision BuildDecision(
        DateTimeOffset now,
        List<GuardianAction> actions,
        List<string> notes,
        ConnectivityLevel connectivity)
    {
        return new GuardianDecision
        {
            State = _stateMachine.Current,
            Actions = actions,
            Notes = notes,
            Connectivity = connectivity,
            EvaluatedAtUtc = now,
        };
    }

    public EngineDiagnostics GetDiagnostics(DateTimeOffset now)
    {
        lock (_gate)
        {
            _commandLimiters.TryGetValue("campus-auth", out var campusLimiter);
            return new EngineDiagnostics
            {
                State = _stateMachine.Current,
                ConsecutiveInternetFailures = _internetTracker.ConsecutiveFailures,
                ConsecutiveInternetSuccesses = _internetTracker.ConsecutiveSuccesses,
                InternetFailing = _internetTracker.IsFailing,
                ConsecutiveEthernetFailures = _ethernetTracker.ConsecutiveFailures,
                EthernetFailing = _ethernetTracker.IsFailing,
                CampusAuthRunsLastHour = campusLimiter?.RunsInWindow(now) ?? 0,
                CampusAuthConsecutiveRuns = campusLimiter?.ConsecutiveRuns ?? 0,
                LastCampusAuthUtc = _lastCampusAuthUtc,
                NextRecoveryBackoff = _recoveryBackoff.DelayFor(Math.Min(_internetTracker.TotalFailures, 8)),
                BannedProfiles = _blacklist.Snapshot(now),
                AdapterStates = _adapters.ToDictionary(
                    kv => kv.Key.ToString("N"),
                    kv => kv.Value.Connectivity.ToString()),
            };
        }
    }

    /// <summary>Records that the host could not execute an action (permission, missing file, ...).</summary>
    public void NotifyActionFailed(GuardianAction action, string reason, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (action is ConnectWifiAction connect)
            {
                NotifyConnectResult(connect.InterfaceGuid, connect.ProfileName, false, reason, now);
            }

            _logger.LogWarning("Action {Action} failed: {Reason}", action.Describe(), reason);
        }
    }
}
