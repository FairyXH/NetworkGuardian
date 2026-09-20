using Microsoft.Extensions.Logging;

namespace NetworkGuardian.Core.Configuration;

/// <summary>
/// Upgrades older configuration documents in place. Migration is intentionally tolerant: a document
/// that cannot be understood is replaced by defaults rather than crashing the application.
/// </summary>
public sealed class ConfigMigrator
{
    private readonly ILogger<ConfigMigrator> _logger;

    public ConfigMigrator(ILogger<ConfigMigrator>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigMigrator>.Instance;
    }

    public IReadOnlyList<string> Migrate(GuardianConfig config)
    {
        var applied = new List<string>();
        var version = config.Version;

        if (version > GuardianConfig.CurrentVersion)
        {
            applied.Add($"config version {version} is newer than supported version " +
                        $"{GuardianConfig.CurrentVersion}; unknown members are preserved as-is.");
            _logger.LogWarning("Configuration was written by a newer build (version {Version}).", version);
            return applied;
        }

        if (version < 1)
        {
            // Version 0 = hand written or pre-release document. Defaults already cover the gaps.
            config.ProbeEndpoints = config.ProbeEndpoints is { Count: > 0 }
                ? config.ProbeEndpoints
                : ProbeEndpointSettings.CreateDefaults();
            config.Version = 1;
            version = 1;
            applied.Add("v0 -> v1: probe endpoints seeded with defaults.");
        }

        if (version < 2)
        {
            // v2 introduced the generic offline command list and the shell/exe distinction.
            config.OfflineCommands ??= new List<CommandDefinition>();
            config.CampusAuth.Kind = config.CampusAuth.Kind;
            config.Version = 2;
            version = 2;
            applied.Add("v2: offline command list enabled; command kind defaults to executable.");
        }

        if (version < 3)
        {
            // v3 introduced sticky-connection guarantees and per-adapter scan throttling.
            config.Wifi.StickyConnection = true;
            config.General.MinimumScanIntervalSeconds = Math.Max(config.General.MinimumScanIntervalSeconds, 25);
            config.Version = 3;
            version = 3;
            applied.Add("v3: sticky connection forced on; per-adapter scan interval >= 25s.");
        }

        if (version < 4)
        {
            // v4: at most one Wi-Fi adapter may hold a connection to a given SSID. Two adapters on the
            // same access point only produce duplicate associations (and, on many drivers, a worse
            // link for both), so the default is now "one SSID, one adapter" and an already connected
            // duplicate is dropped in favour of the better signal.
            config.Wifi.AllowSameSsidOnMultipleAdapters = false;
            config.Version = 4;
            applied.Add("v4: one SSID per Wi-Fi adapter (allowSameSsidOnMultipleAdapters=false).");
        }

        if (version < 5)
        {
            // v5: the self-maintained wireless network library (802.1X/EAP accounts) plus the
            // per-adapter software radio watchdog. Both default to on because they only act on
            // networks the user has entered credentials for, and on a radio the user switched off.
            config.Wifi.EapConnectMaxAttempts = config.Wifi.EapConnectMaxAttempts <= 0
                ? 5
                : config.Wifi.EapConnectMaxAttempts;
            config.General.RadioWatchdogSeconds = config.General.RadioWatchdogSeconds <= 0
                ? 3
                : config.General.RadioWatchdogSeconds;
            config.Version = 5;
            version = 5;
            applied.Add("v5: wireless credential library + 3s radio watchdog + 5 EAP connect attempts.");
        }

        if (version < 6)
        {
            config.General.ManageInterfaceMetrics = true;
            config.Wifi.CampusNetworkSsids ??= new List<string>();
            config.Wifi.CampusWifiAdapterAssignments ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            config.Version = 6;
            version = 6;
            applied.Add("v6: automatic wired-first route failover + campus quiet-period policy.");
        }

        config.Version = GuardianConfig.CurrentVersion;

        foreach (var note in applied)
        {
            _logger.LogInformation("Configuration migration: {Note}", note);
        }

        return applied;
    }
}
