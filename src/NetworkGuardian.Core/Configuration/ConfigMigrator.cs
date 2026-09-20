using Microsoft.Extensions.Logging;
using NetworkGuardian.Core.Models;

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

        if (version < 7)
        {
            var defaults = ProbeEndpointSettings.CreateDefaults();
            foreach (var endpoint in defaults.Where(candidate =>
                         candidate.Kind is ProbeKind.Http or ProbeKind.Https &&
                         config.ProbeEndpoints.All(existing =>
                             !string.Equals(existing.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))))
            {
                config.ProbeEndpoints.Add(endpoint);
            }

            config.Probe.RequiredSuccessCount = 1;
            config.Version = 7;
            version = 7;
            applied.Add("v7: web-only Internet verdict with Baidu, Bing, QQ and Cloudflare probes.");
        }

        if (version < 8)
        {
            var defaults = ProbeEndpointSettings.CreateDefaults();
            foreach (var endpoint in config.ProbeEndpoints)
            {
                endpoint.Enabled = false;
            }

            foreach (var ping in defaults.Where(candidate =>
                         candidate.Enabled && candidate.Kind == ProbeKind.Icmp))
            {
                var existing = config.ProbeEndpoints.FirstOrDefault(endpoint =>
                    string.Equals(endpoint.Name, ping.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    config.ProbeEndpoints.Add(ping);
                }
                else
                {
                    existing.Enabled = true;
                    existing.Kind = ProbeKind.Icmp;
                    existing.Target = ping.Target;
                    existing.TimeoutMs = ping.TimeoutMs;
                }
            }

            config.Probe.AllowIcmp = true;
            config.Probe.RequiredSuccessCount = 1;
            config.Probe.TimeoutMs = 1200;
            config.Probe.RoundTimeoutMs = 2500;
            config.Version = 8;
            version = 8;
            applied.Add("v8: fast single-packet ICMP verdict using four parallel public targets.");
        }

        config.Version = GuardianConfig.CurrentVersion;

        foreach (var note in applied)
        {
            _logger.LogInformation("Configuration migration: {Note}", note);
        }

        return applied;
    }
}
