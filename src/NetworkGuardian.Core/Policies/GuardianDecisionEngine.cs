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

    /// <summary>
    /// Networks given up for this run because their 802.1X account was rejected too often, as
    /// <c>interfaceGuid|ssid|failures</c>.
    /// </summary>
    public required IReadOnlyList<string> EapAbandonedNetworks { get; init; }

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
    private readonly EapConnectRetryPolicy _eapRetries;
    private readonly NetworkDeviceClassifier _classifier;
    private readonly RecoveryStateMachine _stateMachine;
    private readonly Dictionary<Guid, AdapterPolicyState> _adapters = new();
    private readonly Dictionary<string, Guid> _ssidOwners = new(StringComparer.OrdinalIgnoreCase);
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
    private string? _pendingMetricSignature;
    private int _pendingMetricObservations;

    public GuardianDecisionEngine(
        GuardianConfig config,
        ILogger<GuardianDecisionEngine>? logger = null,
        IClock? clock = null)
    {
        _logger = logger ?? NullLogger<GuardianDecisionEngine>.Instance;
        var now = (clock ?? SystemClock.Instance).UtcNow;

        _blacklist = new ConnectFailureBlacklist(TimeSpan.FromSeconds(config.Recovery.ConnectFailureBlacklistSeconds));
        _eapRetries = new EapConnectRetryPolicy(config.Wifi.EapConnectMaxAttempts);
        _selector = new CandidateSelector(_blacklist, _eapRetries);
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

    /// <summary>
    /// Per-adapter 802.1X attempt counters. In-memory only: after the configured number of failures the
    /// network is given up until the program restarts.
    /// </summary>
    public EapConnectRetryPolicy EapRetries => _eapRetries;

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
            _eapRetries.MaxAttempts = config.Wifi.EapConnectMaxAttempts;

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
            _eapRetries.Reset();
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
        => NotifyConnectResult(interfaceGuid, profileName, success, failure, now, requiredEap: false);

    /// <summary>
    /// Records the outcome of one connect attempt.
    /// </summary>
    /// <param name="requiredEap">
    /// True when the attempt used the built-in library's 802.1X account. Those attempts are counted per
    /// adapter and SSID, and once the budget is spent the network is given up for the rest of the run -
    /// a campus account that was rejected five times will not be rejected a sixth time.
    /// </param>
    public void NotifyConnectResult(
        Guid interfaceGuid,
        string profileName,
        bool success,
        string? failure,
        DateTimeOffset now,
        bool requiredEap)
    {
        lock (_gate)
        {
            var state = GetAdapterState(interfaceGuid, null);
            state.PendingConnectUtc = null;
            state.PendingConnectProfile = null;

            if (success)
            {
                _blacklist.RecordSuccess(interfaceGuid, profileName, now);
                _eapRetries.RecordSuccess(interfaceGuid, profileName);
                state.LastSuccessfulConnectionUtc = now;
                state.LastConnectedProfile = profileName;
                state.ConnectLimiter.NotifySuccess();
                _logger.LogInformation("Connect succeeded on {Adapter} using {Profile}", interfaceGuid, profileName);
            }
            else
            {
                _blacklist.RecordFailure(interfaceGuid, profileName, now, failure);

                if (requiredEap)
                {
                    var outcome = _eapRetries.RecordFailure(interfaceGuid, profileName, now, failure);
                    if (outcome.AbandonedNow)
                    {
                        _logger.LogWarning(
                            "802.1X connect to {Profile} on {Adapter} failed {Failures} times; giving the " +
                            "network up for this run. The next start will try again. Last failure: {Failure}",
                            profileName, interfaceGuid, outcome.Failures, failure);
                        RecordRecoveryAction(
                            $"{profileName}：已连续 {outcome.Failures} 次 802.1X 认证失败，本次运行临时放弃" +
                            "（重启程序后继续尝试）");
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Connect with the library account failed on {Adapter} using {Profile} " +
                            "({Failures}/{MaxAttempts}): {Failure}",
                            interfaceGuid, profileName, outcome.Failures, _eapRetries.MaxAttempts, failure);
                    }
                }
                else
                {
                    _logger.LogWarning("Connect failed on {Adapter} using {Profile}: {Failure}",
                        interfaceGuid, profileName, failure);
                }
            }
        }
    }

    /// <summary>Clears the 802.1X counters of one SSID on every adapter, e.g. after the account was fixed.</summary>
    public int ClearEapRetries(string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return 0;
        }

        lock (_gate)
        {
            var cleared = 0;
            foreach (var status in _eapRetries.Snapshot())
            {
                if (string.Equals(status.Ssid, ssid, StringComparison.OrdinalIgnoreCase) &&
                    _eapRetries.Forget(status.InterfaceGuid, status.Ssid))
                {
                    cleared++;
                }
            }

            if (cleared > 0)
            {
                _logger.LogInformation("Cleared the 802.1X attempt counters of {Ssid} on {Count} adapter(s)",
                    ssid, cleared);
            }

            return cleared;
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
            if (internetOnline)
            {
                notes.Add("Wi-Fi radio is off, but Internet is already available; leaving user state unchanged.");
            }
            else if (config.General.AutoEnableWifiRadio)
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

        // ---------- Disabled physical network devices ----------
        // A disabled Ethernet/Wi-Fi adapter must be restored independently of the current route. Having
        // Internet through another adapter is not evidence that the disabled hardware should stay off:
        // it only hid the device until the active route failed in the previous implementation.
        var physicalNetworkDevices = input.Devices
            .Where(d => d.Record.IsPresent && d.Classification.IsPhysical &&
                        d.Classification.Category is DeviceCategory.PhysicalWifi or DeviceCategory.PhysicalEthernet)
            .ToList();
        var wifiDevices = physicalNetworkDevices
            .Where(d => d.Classification.Category == DeviceCategory.PhysicalWifi)
            .ToList();
        // Only genuinely disabled devices (CM problem code 22/21) can simply be enabled.
        var disabledDevices = physicalNetworkDevices.Where(d => d.IsDisabled).ToList();

        // A device whose driver failed to start (10, 43, ...) is not "disabled": CM_Enable_DevNode
        // changes nothing for it. The one PnP action that has a real chance is a restart (disable +
        // enable, exactly what Device Manager offers), so it gets a bounded attempt instead of never.
        // Either way the situation is stated, because "the adapter never came back" has to be visible.
        foreach (var faulted in wifiDevices.Where(d => !d.IsEnabled && !d.IsDisabled))
        {
            var friendly = faulted.Record.FriendlyName ?? faulted.Record.DeviceDescription ?? "Wi-Fi adapter";

            if (internetOnline)
            {
                notes.Add($"物理无线网卡「{friendly}」未运行，但当前已有外网；为保持网络稳定，暂不重启。 ");
                continue;
            }

            if (!config.General.AutoRestartFaultedWifiDevices)
            {
                notes.Add($"物理无线网卡「{friendly}」存在但未运行（problemCode={faulted.Record.ProblemCode}）；" +
                          "故障设备自动重启已关闭，未处理。可在设备管理器中检查该网卡。");
                continue;
            }

            var key = $"restart-device:{faulted.Record.DeviceInstanceId}";
            var limiter = GetCommandLimiter(key,
                Math.Min(config.Recovery.MaxDeviceEnablePerHour, 3),
                TimeSpan.FromSeconds(120), 2);

            if (limiter.TryAcquire(now, out _, out var reason))
            {
                limiter.RecordRun(now);
                actions.Add(new RestartWifiDeviceAction
                {
                    DeviceInstanceId = faulted.Record.DeviceInstanceId,
                    FriendlyName = friendly,
                    ProblemCode = faulted.Record.ProblemCode,
                    Reason = $"physical Wi-Fi device is present but not running (problemCode={faulted.Record.ProblemCode})",
                });
                _stateMachine.Transition(RecoveryState.EnablingWifiDevices, now,
                    "faulted physical Wi-Fi device detected");
            }
            else
            {
                notes.Add($"物理无线网卡「{friendly}」未运行（problemCode={faulted.Record.ProblemCode}）：{reason}");
            }
        }

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
                            FriendlyName = device.Record.FriendlyName ?? device.Record.DeviceDescription ?? "network adapter",
                            Reason = $"physical network device is present but disabled (problemCode={device.Record.ProblemCode})",
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
            notes.Add($"{disabledDevices.Count} physical network device(s) are disabled; autoEnableWifiDevices is off.");
        }

        if (actions.OfType<RestartWifiDeviceAction>().Any())
        {
            // A restart is a disable + enable: give the device time to come back before the next pass
            // evaluates it (otherwise every cycle would just re-trigger the limiter).
            actions.Add(new WaitAction
            {
                Delay = TimeSpan.FromSeconds(Math.Max(6, config.Recovery.DeviceEnableSettleSeconds * 2)),
                Reason = "waiting for the restarted Wi-Fi device to come back",
            });
            return BuildDecision(now, actions, notes, connectivity);
        }

        // ---------- Ethernet / campus authentication ----------
        // Only physical Ethernet interfaces are considered: virtual adapters (VMware, Hyper-V, WSL,
        // loopback, Bluetooth PAN) are up with a gateway on most machines and would otherwise trigger
        // campus authentication that cannot possibly work.
        var ethernetInterfaces = input.Interfaces
            .Where(i => i.Kind == InterfaceKind.Ethernet && i.IsPhysicalDevice != false)
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
                         !CampusQuietPeriod.IsActive(config.Wifi, now) &&
                         config.Ethernet.Enabled &&
                         config.Ethernet.AuthenticateWhenLinkUpButOffline &&
                         (!internetOnline || captivePortal) &&
                         internetFailures >= config.CampusAuth.TriggerAfterConsecutiveFailures &&
                         (ethernetEligible || !config.CampusAuth.RequireEthernetLink) &&
                         (!captivePortal || config.CampusAuth.RunOnCaptivePortal);

        if (campusAuthConfigured && CampusQuietPeriod.IsActive(config.Wifi, now))
        {
            notes.Add("当前处于校园网关闭时段，已暂停校园网认证程序及校园 Wi-Fi 连接重试。");
        }

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

        // ---------- One SSID per adapter ----------
        // Two adapters associated with the same access point duplicate every frame and, with several
        // drivers, degrade the link for both. When the user wants one SSID per adapter, the weaker
        // adapter is released while the stronger one keeps its (sticky) connection untouched.
        if (!config.Wifi.AllowSameSsidOnMultipleAdapters)
        {
            var duplicates = adapters
                .Where(a => a.IsConnected && !string.IsNullOrWhiteSpace(a.CurrentSsid))
                .GroupBy(a => a.CurrentSsid!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicates)
            {
                // Keep the strongest link; ties are broken by description so the choice is stable
                // across cycles instead of oscillating between equally strong adapters.
                var ranked = group
                    .OrderByDescending(a => a.SignalQuality)
                    .ThenBy(a => a.Description, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var keep = _ssidOwners.TryGetValue(group.Key, out var ownerGuid)
                    ? ranked.FirstOrDefault(adapter => adapter.InterfaceGuid == ownerGuid) ?? ranked[0]
                    : ranked[0];
                _ssidOwners[group.Key] = keep.InterfaceGuid;
                foreach (var loser in ranked.Where(adapter => adapter.InterfaceGuid != keep.InterfaceGuid))
                {
                    var key = $"duplicate-ssid:{loser.InterfaceGuid}";
                    var limiter = GetCommandLimiter(key, 12, TimeSpan.FromSeconds(60), 4);

                    if (limiter.TryAcquire(now, out _, out var reason))
                    {
                        limiter.RecordRun(now);
                        actions.Add(new DisconnectWifiAction
                        {
                            InterfaceGuid = loser.InterfaceGuid,
                            Ssid = group.Key,
                            SuppressAutoReconnect = true,
                            Reason = $"'{group.Key}' is connected on more than one adapter; releasing " +
                                     $"{loser.Description} ({loser.SignalQuality}%) and keeping " +
                                     $"{keep.Description} ({keep.SignalQuality}%)",
                        });

                        _stateMachine.Transition(RecoveryState.Recovering, now, "duplicate SSID across adapters");
                        RecordRecoveryAction($"已断开 {loser.Description} 与 {keep.Description} 重复连接的 {group.Key}");
                        wifiNotes.Add($"{loser.Description}：与 {keep.Description} 连接了同一个 SSID " +
                                      $"'{group.Key}'，已按策略断开（保留信号更强的 {keep.SignalQuality}%）");
                    }
                    else
                    {
                        wifiNotes.Add($"{loser.Description}: duplicate SSID '{group.Key}' not released - {reason}");
                    }
                }
            }
        }

        foreach (var single in adapters
                     .Where(adapter => adapter.IsConnected && !string.IsNullOrWhiteSpace(adapter.CurrentSsid))
                     .GroupBy(adapter => adapter.CurrentSsid!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() == 1))
        {
            _ssidOwners[single.Key] = single.First().InterfaceGuid;
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
                              $"({state.Connectivity.ConsecutiveFailures}/{config.Recovery.WifiFailureThreshold}); " +
                              "preserving the existing connection");

                // A failed probe must never tear down an association the user or Windows already
                // established. Probe failures can be caused by captive portals, DNS/proxy software,
                // or the remote targets themselves. The only connected adapter we disconnect is a
                // duplicate SSID loser handled above; recovery work is otherwise limited to idle
                // adapters, which can scan and join an unoccupied saved network without disruption.
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

            // A network another adapter is already holding is not a candidate here (one SSID per
            // adapter), so a released duplicate stays released instead of instantly re-associating.
            var excludeSsids = config.Wifi.AllowSameSsidOnMultipleAdapters
                ? null
                : adapters
                    .Where(other => other.InterfaceGuid != adapter.InterfaceGuid &&
                                    other.IsConnected &&
                                    !string.IsNullOrWhiteSpace(other.CurrentSsid))
                    .Select(other => other.CurrentSsid!)
                    .ToArray();

            var candidates = _selector.SelectCandidates(
                adapter.InterfaceGuid,
                scan,
                adapter.SavedProfiles,
                config.Wifi,
                now,
                out var rejections,
                state.LastConnectedProfile ?? state.LastKnownSsid,
                excludeSsids,
                input.EapCatalog);

            if (config.Logging.VerboseNetwork && rejections.Count > 0)
            {
                wifiNotes.Add($"{adapter.Description}: rejected {rejections.Count} network(s): " +
                              string.Join("; ", rejections.Take(5)));
            }

            // A campus network that cannot authenticate is the reason "my Wi-Fi does not connect", so
            // those rejections are surfaced even without verbose logging.
            foreach (var rejection in rejections.Where(r =>
                         r.StartsWith(CandidateSelector.EapRejectionPrefix, StringComparison.Ordinal)))
            {
                wifiNotes.Add($"{adapter.Description}: {rejection[CandidateSelector.EapRejectionPrefix.Length..]}");
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
                    RequiresEap = assignment.Candidate.RequiresEap,
                    UsesLibraryCredential = assignment.Candidate.UsesLibraryCredential,
                    Reason = $"best candidate: {assignment.Candidate.ScoreReason}" +
                             (assignment.Candidate.UsesLibraryCredential
                                 ? "；使用自维护无线网络库的 802.1X 账号"
                                 : string.Empty),
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
            // A user-controlled radio that we are not allowed to touch stays the headline state: it
            // explains why nothing else is happening.
            if (input.Radio.State == RadioState.Off && !config.General.AutoEnableWifiRadio)
            {
                _stateMachine.Transition(RecoveryState.WifiRadioOff, now, "Wi-Fi radio is off and must be enabled by the user");
            }
            else if (internetOnline)
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

        // Rank every physical interface independently. A dead Ethernet link must never receive the
        // same metric as a healthy Ethernet merely because another wired adapter passed its probe.
        var metricInterfaces = input.Interfaces
            .Where(i => i.IsPhysicalDevice != false && i.Kind is InterfaceKind.Ethernet or InterfaceKind.Wifi)
            .ToList();
        var usableEthernet = ethernetInterfaces
            .Where(i => i.Probe?.IsOnline == true ||
                        (!config.Probe.PerInterfaceProbing &&
                         i.Probe is null && internetOnline && i.IsDefaultRoute && i.IsUp))
            .OrderByDescending(i => i.IsDefaultRoute)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ethernetShouldLead = usableEthernet.Count > 0;
        var metrics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < usableEthernet.Count; index++)
        {
            metrics[usableEthernet[index].Id] = 10 + (index * 10);
        }

        foreach (var iface in ethernetInterfaces.Where(i => !metrics.ContainsKey(i.Id)))
        {
            metrics[iface.Id] = iface.IsUp ? 80 : 90;
        }

        var wifiInterfaces = metricInterfaces.Where(i => i.Kind == InterfaceKind.Wifi)
            .OrderByDescending(i => i.Probe?.IsOnline == true)
            .ThenByDescending(i => i.IsDefaultRoute)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < wifiInterfaces.Count; index++)
        {
            var iface = wifiInterfaces[index];
            metrics[iface.Id] = iface.IsUp
                ? (ethernetShouldLead ? 50 : 10) + (index * 10)
                : 90;
        }

        var metricsDiffer = metricInterfaces.Any(i =>
            metrics.TryGetValue(i.Id, out var desired) && i.InterfaceMetric != desired);
        var metricSignature = string.Join("|", metrics.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}"));

        if (!metricsDiffer)
        {
            _pendingMetricSignature = null;
            _pendingMetricObservations = 0;
        }
        else if (string.Equals(_pendingMetricSignature, metricSignature, StringComparison.Ordinal))
        {
            _pendingMetricObservations++;
        }
        else
        {
            _pendingMetricSignature = metricSignature;
            _pendingMetricObservations = 1;
        }

        // Never rewrite the route table from one noisy probe round. Two identical consecutive
        // proposals are required in either direction; alternating results therefore leave the last
        // known working outlet untouched instead of making every connection flap.
        if (metricsDiffer && _pendingMetricObservations >= 2)
        {
            actions.Add(new ApplyInterfaceMetricsAction
            {
                MetricsByInterfaceId = metrics,
                Reason = ethernetShouldLead
                    ? $"{usableEthernet.Count} Ethernet interface(s) have Internet access; wired routes ranked first"
                    : "all Ethernet interfaces are unavailable/offline; Wi-Fi is the failover route",
            });
            _pendingMetricSignature = null;
            _pendingMetricObservations = 0;
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
                EapAbandonedNetworks = _eapRetries.Snapshot()
                    .Where(s => s.Abandoned)
                    .Select(s => $"{s.InterfaceGuid:N}|{s.Ssid}|{s.Failures}")
                    .ToList(),
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
