using Microsoft.Extensions.Logging;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Configuration;

/// <summary>
/// Repairs a loaded configuration in place and reports what was changed. The program must never
/// fail to start because a value in config.json is out of range or missing.
/// </summary>
public sealed class ConfigValidator
{
    private readonly ILogger<ConfigValidator> _logger;

    public ConfigValidator(ILogger<ConfigValidator>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigValidator>.Instance;
    }

    public IReadOnlyList<string> Normalize(GuardianConfig config)
    {
        var issues = new List<string>();

        config.Version = config.Version <= 0 ? GuardianConfig.CurrentVersion : config.Version;

        config.General ??= new GeneralSettings();
        config.Recovery ??= new RecoverySettings();
        config.Probe ??= new ProbeSettings();
        config.Wifi ??= new WifiSettings();
        config.Ethernet ??= new EthernetSettings();
        config.CampusAuth ??= new CampusAuthSettings();
        config.Startup ??= new StartupSettings();
        config.Logging ??= new LoggingSettings();
        config.OfflineCommands ??= new List<CommandDefinition>();
        config.InterfaceDenyList ??= new List<string>();
        config.Wifi.SsidDenyList ??= new List<string>();
        config.Wifi.SsidAllowList ??= new List<string>();
        config.Wifi.CampusNetworkSsids ??= new List<string>();
        config.Wifi.CampusWifiAdapterAssignments ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        config.Wifi.CampusWifiAdapterAssignments = new Dictionary<string, string>(
            config.Wifi.CampusWifiAdapterAssignments,
            StringComparer.OrdinalIgnoreCase);
        config.Probe.PingTargets ??= new List<string>();
        config.Probe.PingTargets = config.Probe.PingTargets
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.General.ManageInterfaceMetrics = true;

        if (config.ProbeEndpoints is null || config.ProbeEndpoints.Count == 0)
        {
            issues.Add("probeEndpoints was empty; default endpoints restored.");
            config.ProbeEndpoints = ProbeEndpointSettings.CreateDefaults();
        }

        config.General.HealthSweepSeconds = Clamp(config.General.HealthSweepSeconds, 5, 3600, "general.healthSweepSeconds", issues);
        config.General.EnumerationRefreshSeconds = Clamp(config.General.EnumerationRefreshSeconds, 30, 3600, "general.enumerationRefreshSeconds", issues);
        config.General.ResumeSettleSeconds = Clamp(config.General.ResumeSettleSeconds, 0, 300, "general.resumeSettleSeconds", issues);
        config.General.MinimumScanIntervalSeconds = Clamp(config.General.MinimumScanIntervalSeconds, 5, 3600, "general.minimumScanIntervalSeconds", issues);
        config.General.ScanTimeoutSeconds = Clamp(config.General.ScanTimeoutSeconds, 3, 120, "general.scanTimeoutSeconds", issues);
        config.General.ManualScanTimeoutSeconds = Clamp(config.General.ManualScanTimeoutSeconds, 3, 180, "general.manualScanTimeoutSeconds", issues);
        config.General.DhcpWaitSeconds = Clamp(config.General.DhcpWaitSeconds, 3, 300, "general.dhcpWaitSeconds", issues);
        config.General.RadioWatchdogSeconds = Clamp(config.General.RadioWatchdogSeconds, 1, 600, "general.radioWatchdogSeconds", issues);

        config.Recovery.InternetFailureThreshold = Clamp(config.Recovery.InternetFailureThreshold, 1, 100, "recovery.internetFailureThreshold", issues);
        config.Recovery.InternetRecoveryThreshold = Clamp(config.Recovery.InternetRecoveryThreshold, 1, 100, "recovery.internetRecoveryThreshold", issues);
        config.Recovery.WifiFailureThreshold = Clamp(config.Recovery.WifiFailureThreshold, 1, 100, "recovery.wifiFailureThreshold", issues);
        config.Recovery.CooldownSeconds = Clamp(config.Recovery.CooldownSeconds, 0, 7200, "recovery.cooldownSeconds", issues);
        config.Recovery.BaseBackoffSeconds = Clamp(config.Recovery.BaseBackoffSeconds, 1, 3600, "recovery.baseBackoffSeconds", issues);
        config.Recovery.MaxBackoffSeconds = Clamp(config.Recovery.MaxBackoffSeconds, config.Recovery.BaseBackoffSeconds, 86400, "recovery.maxBackoffSeconds", issues);
        config.Recovery.MaxConnectAttemptsPerRound = Clamp(config.Recovery.MaxConnectAttemptsPerRound, 1, 50, "recovery.maxConnectAttemptsPerRound", issues);
        config.Recovery.ConnectFailureBlacklistSeconds = Clamp(config.Recovery.ConnectFailureBlacklistSeconds, 0, 86400, "recovery.connectFailureBlacklistSeconds", issues);
        config.Recovery.DeviceEnableSettleSeconds = Clamp(config.Recovery.DeviceEnableSettleSeconds, 1, 120, "recovery.deviceEnableSettleSeconds", issues);
        config.Recovery.MaxDeviceEnablePerHour = Clamp(config.Recovery.MaxDeviceEnablePerHour, 1, 120, "recovery.maxDeviceEnablePerHour", issues);
        config.Recovery.OperationCircuitBreakerThreshold = Clamp(config.Recovery.OperationCircuitBreakerThreshold, 1, 1000, "recovery.operationCircuitBreakerThreshold", issues);
        config.Recovery.OperationCircuitBreakerSeconds = Clamp(config.Recovery.OperationCircuitBreakerSeconds, 0, 86400, "recovery.operationCircuitBreakerSeconds", issues);

        config.Probe.IntervalSeconds = Clamp(config.Probe.IntervalSeconds, 3, 3600, "probe.intervalSeconds", issues);
        config.Probe.TimeoutMs = Clamp(config.Probe.TimeoutMs, 200, 60000, "probe.timeoutMs", issues);
        config.Probe.PingTimeoutMs = Clamp(config.Probe.PingTimeoutMs, 200, 10000, "probe.pingTimeoutMs", issues);
        config.Probe.RoundTimeoutMs = Clamp(config.Probe.RoundTimeoutMs,
            Math.Max(config.Probe.TimeoutMs, config.Probe.PingTimeoutMs), 120000, "probe.roundTimeoutMs", issues);
        config.Probe.MaxConcurrency = Clamp(config.Probe.MaxConcurrency, 1, 16, "probe.maxConcurrency", issues);

        var enabledWebEndpoints = config.ProbeEndpoints.Count(endpoint =>
            endpoint.Enabled && endpoint.Kind is ProbeKind.Http or ProbeKind.Https);
        config.Probe.RequiredSuccessCount = Clamp(config.Probe.RequiredSuccessCount, 1,
            Math.Max(1, enabledWebEndpoints), "probe.requiredSuccessCount", issues);

        config.Wifi.SignalHysteresis = Clamp(config.Wifi.SignalHysteresis, 0, 100, "wifi.signalHysteresis", issues);
        config.Wifi.MinimumSignalQuality = Clamp(config.Wifi.MinimumSignalQuality, 0, 100, "wifi.minimumSignalQuality", issues);
        config.Wifi.HighBandBonus = Clamp(config.Wifi.HighBandBonus, -100, 100, "wifi.highBandBonus", issues);
        config.Wifi.RecentProfileBonus = Clamp(config.Wifi.RecentProfileBonus, -100, 100, "wifi.recentProfileBonus", issues);
        config.Wifi.DisconnectGraceSeconds = Clamp(config.Wifi.DisconnectGraceSeconds, 0, 600, "wifi.disconnectGraceSeconds", issues);
        config.Wifi.StaleConnectionSeconds = Clamp(config.Wifi.StaleConnectionSeconds, 30, 3600, "wifi.staleConnectionSeconds", issues);
        config.Wifi.EapConnectMaxAttempts = Clamp(config.Wifi.EapConnectMaxAttempts, 1, 50, "wifi.eapConnectMaxAttempts", issues);
        config.Wifi.CampusQuietStartMinutes = Clamp(config.Wifi.CampusQuietStartMinutes, 0, 1439, "wifi.campusQuietStartMinutes", issues);
        config.Wifi.CampusQuietEndMinutes = Clamp(config.Wifi.CampusQuietEndMinutes, 0, 1439, "wifi.campusQuietEndMinutes", issues);

        config.Ethernet.FailureThreshold = Clamp(config.Ethernet.FailureThreshold, 1, 100, "ethernet.failureThreshold", issues);
        config.Ethernet.LinkUpGraceSeconds = Clamp(config.Ethernet.LinkUpGraceSeconds, 0, 600, "ethernet.linkUpGraceSeconds", issues);

        config.CampusAuth.TriggerAfterConsecutiveFailures = Clamp(config.CampusAuth.TriggerAfterConsecutiveFailures, 1, 100, "campusAuth.triggerAfterConsecutiveFailures", issues);
        config.CampusAuth.MinIntervalSeconds = Clamp(config.CampusAuth.MinIntervalSeconds, 0, 86400, "campusAuth.minIntervalSeconds", issues);
        config.CampusAuth.MaxRunsPerHour = Clamp(config.CampusAuth.MaxRunsPerHour, 1, 1000, "campusAuth.maxRunsPerHour", issues);
        config.CampusAuth.MaxConsecutiveRuns = Clamp(config.CampusAuth.MaxConsecutiveRuns, 1, 1000, "campusAuth.maxConsecutiveRuns", issues);
        config.CampusAuth.ExecutionTimeoutSeconds = Clamp(config.CampusAuth.ExecutionTimeoutSeconds, 0, 3600, "campusAuth.executionTimeoutSeconds", issues);
        config.CampusAuth.WaitAfterRunSeconds = Clamp(config.CampusAuth.WaitAfterRunSeconds, 0, 3600, "campusAuth.waitAfterRunSeconds", issues);
        config.CampusAuth.VerificationProbes = Clamp(config.CampusAuth.VerificationProbes, 0, 20, "campusAuth.verificationProbes", issues);

        config.Logging.UiBufferSize = Clamp(config.Logging.UiBufferSize, 100, 100000, "logging.uiBufferSize", issues);
        config.Logging.MaxFileSizeKb = Clamp(config.Logging.MaxFileSizeKb, 64, 262144, "logging.maxFileSizeKb", issues);
        config.Logging.MaxFiles = Clamp(config.Logging.MaxFiles, 1, 1000, "logging.maxFiles", issues);
        config.Logging.RetentionDays = Clamp(config.Logging.RetentionDays, 1, 3650, "logging.retentionDays", issues);

        foreach (var command in config.OfflineCommands)
        {
            command.ExecutionTimeoutSeconds = Clamp(command.ExecutionTimeoutSeconds, 0, 3600, $"offlineCommands[{command.Name}].executionTimeoutSeconds", issues);
            command.MinIntervalSeconds = Clamp(command.MinIntervalSeconds, 0, 86400, $"offlineCommands[{command.Name}].minIntervalSeconds", issues);
            command.MaxRunsPerHour = Clamp(command.MaxRunsPerHour, 1, 1000, $"offlineCommands[{command.Name}].maxRunsPerHour", issues);
            command.MaxConsecutiveRuns = Clamp(command.MaxConsecutiveRuns, 1, 1000, $"offlineCommands[{command.Name}].maxConsecutiveRuns", issues);
            command.WaitAfterRunSeconds = Clamp(command.WaitAfterRunSeconds, 0, 3600, $"offlineCommands[{command.Name}].waitAfterRunSeconds", issues);
        }

        foreach (var endpoint in config.ProbeEndpoints)
        {
            if (endpoint.TimeoutMs is { } t)
            {
                endpoint.TimeoutMs = Clamp(t, 200, 60000, $"probeEndpoints[{endpoint.Name}].timeoutMs", issues);
            }

            if (endpoint.ExpectedStatusMax < endpoint.ExpectedStatusMin)
            {
                issues.Add($"probeEndpoints[{endpoint.Name}].expectedStatusMax < min; swapped.");
                (endpoint.ExpectedStatusMin, endpoint.ExpectedStatusMax) =
                    (endpoint.ExpectedStatusMax, endpoint.ExpectedStatusMin);
            }
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in config.OfflineCommands)
        {
            if (string.IsNullOrWhiteSpace(command.Id) || !seenIds.Add(command.Id))
            {
                command.Id = Guid.NewGuid().ToString("N");
                seenIds.Add(command.Id);
                issues.Add($"offlineCommands entry had a duplicate/blank id; regenerated as {command.Id}.");
            }
        }

        if (config.CampusAuth.Enabled && string.IsNullOrWhiteSpace(config.CampusAuth.ExecutablePath))
        {
            issues.Add("campusAuth.enabled is true but executablePath is empty; campus auth will be skipped.");
        }

        if (!config.Probe.Enabled)
        {
            issues.Add("probe.enabled is false; connectivity is inferred from link state only.");
        }

        foreach (var issue in issues)
        {
            _logger.LogWarning("Configuration normalized: {Issue}", issue);
        }

        return issues;
    }

    private static int Clamp(int value, int min, int max, string name, List<string> issues)
    {
        if (value < min)
        {
            issues.Add($"{name}={value} is below the minimum {min}; clamped.");
            return min;
        }

        if (value > max)
        {
            issues.Add($"{name}={value} exceeds the maximum {max}; clamped.");
            return max;
        }

        return value;
    }

    private static double Clamp(double value, double min, double max, string name, List<string> issues)
    {
        if (double.IsNaN(value))
        {
            issues.Add($"{name} was NaN; reset to 0.");
            return 0;
        }

        if (value < min)
        {
            issues.Add($"{name}={value} is below the minimum {min}; clamped.");
            return min;
        }

        if (value > max)
        {
            issues.Add($"{name}={value} exceeds the maximum {max}; clamped.");
            return max;
        }

        return value;
    }
}
