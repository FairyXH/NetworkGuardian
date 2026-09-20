using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Wlan;

namespace NetworkGuardian.Core.Policies;

/// <summary>
/// Turns a raw per-adapter scan into a ranked list of connectable, already-saved profiles.
/// It is used only when an adapter has no working connection: the sticky connection policy forbids
/// using it to switch adapters that are currently fine.
/// </summary>
public sealed class CandidateSelector
{
    /// <summary>
    /// Marks a rejection that the user has to see, not only a verbose log: a campus network that cannot
    /// be joined because the account is missing (or was already given up) is the answer to "why does my
    /// Wi-Fi not connect", so the engine always surfaces these.
    /// </summary>
    public const string EapRejectionPrefix = "[802.1X] ";

    private readonly ConnectFailureBlacklist _blacklist;
    private readonly EapConnectRetryPolicy _eapRetries;

    public CandidateSelector(ConnectFailureBlacklist blacklist, EapConnectRetryPolicy? eapRetries = null)
    {
        _blacklist = blacklist;
        _eapRetries = eapRetries ?? new EapConnectRetryPolicy();
    }

    public IReadOnlyList<WifiCandidate> SelectCandidates(
        Guid interfaceGuid,
        AdapterScanSnapshot? scan,
        IReadOnlyList<string> savedProfiles,
        WifiSettings settings,
        DateTimeOffset now,
        out IReadOnlyList<string> rejectionReasons,
        string? lastSuccessfulProfile = null,
        IReadOnlyCollection<string>? excludeSsids = null,
        WifiEapCatalog? eapCatalog = null)
    {
        var catalog = eapCatalog ?? WifiEapCatalog.Empty;
        var rejections = new List<string>();
        var profiles = new HashSet<string>(savedProfiles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // SSIDs another adapter is already holding: with "one SSID per adapter" a dropped duplicate
        // must not simply re-associate, so those networks are skipped rather than reconnected.
        var excluded = excludeSsids is { Count: > 0 }
            ? new HashSet<string>(excludeSsids, StringComparer.OrdinalIgnoreCase)
            : null;

        if (scan is null || scan.Networks.Count == 0)
        {
            rejectionReasons = new[] { "no scan results available for this adapter" };
            return Array.Empty<WifiCandidate>();
        }

        var candidates = new List<WifiCandidate>();

        foreach (var network in scan.Networks)
        {
            if (string.IsNullOrWhiteSpace(network.Ssid))
            {
                continue;
            }

            if (excluded is not null && excluded.Contains(network.Ssid))
            {
                rejections.Add($"{network.Ssid}: already held by another adapter (one SSID per adapter)");
                continue;
            }

            if (CampusQuietPeriod.IsActive(settings, now) && CampusQuietPeriod.IsCampusSsid(settings, network.Ssid))
            {
                rejections.Add($"{network.Ssid}: 校园网关闭时段内暂停自动连接");
                continue;
            }

            if (network.BssType == WifiBssType.Independent)
            {
                rejections.Add($"{network.Ssid}: ad-hoc (independent BSS) networks are never auto-joined");
                continue;
            }

            var profileName = network.ProfileName;
            if (string.IsNullOrWhiteSpace(profileName) && profiles.Contains(network.Ssid))
            {
                profileName = network.Ssid;
            }

            if (string.IsNullOrWhiteSpace(profileName))
            {
                if (settings.OnlySavedProfiles)
                {
                    rejections.Add($"{network.Ssid}: no saved profile (auto-join of unknown networks is disabled)");
                    continue;
                }

                rejections.Add($"{network.Ssid}: no saved profile");
                continue;
            }

            if (!profiles.Contains(profileName) && settings.OnlySavedProfiles)
            {
                rejections.Add($"{network.Ssid}: profile '{profileName}' is not present for this adapter");
                continue;
            }

            if (settings.SsidDenyList.Any(s => string.Equals(s, network.Ssid, StringComparison.OrdinalIgnoreCase)) ||
                settings.SsidDenyList.Any(s => string.Equals(s, profileName, StringComparison.OrdinalIgnoreCase)))
            {
                rejections.Add($"{network.Ssid}: denied by ssidDenyList");
                continue;
            }

            if (settings.SsidAllowList.Count > 0 &&
                !settings.SsidAllowList.Any(s => string.Equals(s, network.Ssid, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(s, profileName, StringComparison.OrdinalIgnoreCase)))
            {
                rejections.Add($"{network.Ssid}: not present in ssidAllowList");
                continue;
            }

            if (!network.Connectable)
            {
                rejections.Add($"{network.Ssid}: driver reports the network as not connectable");
                continue;
            }

            // ---------- 802.1X/EAP networks ----------
            // Such a network authenticates with the account in the built-in library, so an entry has to
            // exist; and once the attempt budget is spent for this adapter the network is left alone for
            // the rest of the run (the next start tries again). This check comes before the short-term
            // blacklist on purpose: "given up for this run" is the final reason and should be the one the
            // user (and the log) sees, not a ban that expires a few minutes later.
            var requiresEap = WifiProfileInspector.IsEnterpriseSecurity(network.Security);
            var usesLibraryCredential = false;

            if (requiresEap && settings.UseCredentialLibraryForEap)
            {
                if (!catalog.HasCredential(network.Ssid))
                {
                    rejections.Add($"{EapRejectionPrefix}{network.Ssid}: 需要 802.1X 认证，" +
                                   "但自维护无线网络库中没有该网络的账号（账号密码在“网络凭据库”中添加）");
                    continue;
                }

                if (_eapRetries.IsAbandoned(interfaceGuid, network.Ssid))
                {
                    rejections.Add($"{EapRejectionPrefix}{network.Ssid}: 已按 802.1X 重试上限" +
                                   $"（{_eapRetries.MaxAttempts} 次）临时放弃，本次运行不再尝试（重启程序后会继续尝试）");
                    continue;
                }

                usesLibraryCredential = true;
                var libraryProfile = catalog.ProfileNameFor(network.Ssid);
                if (!string.IsNullOrWhiteSpace(libraryProfile))
                {
                    profileName = libraryProfile;
                }
            }

            if (!profiles.Contains(profileName) && settings.OnlySavedProfiles)
            {
                rejections.Add($"{network.Ssid}: credential-library profile '{profileName}' is not present for this adapter");
                continue;
            }

            if (_blacklist.IsBlacklisted(interfaceGuid, profileName, now, out var remaining))
            {
                rejections.Add($"{network.Ssid}: temporarily blacklisted for {remaining.TotalSeconds:F0}s after a connect failure");
                continue;
            }

            var score = Score(network, settings, lastSuccessfulProfile);
            var failures = _blacklist.FailureCount(interfaceGuid, profileName);

            candidates.Add(new WifiCandidate
            {
                InterfaceGuid = interfaceGuid,
                Ssid = network.Ssid,
                ProfileName = profileName,
                SignalQuality = network.SignalQuality,
                Rssi = network.Rssi,
                Band = network.Band,
                Security = network.Security,
                Score = score.Value,
                ScoreReason = score.Reason + (failures > 0 ? $", priorFailures={failures}" : string.Empty),
                RecentConnectFailures = failures,
                RequiresEap = requiresEap,
                UsesLibraryCredential = usesLibraryCredential,
            });
        }

        // Weakly visible networks are dropped only when a stronger option exists.
        var strongest = candidates.Count == 0 ? 0 : candidates.Max(c => c.SignalQuality);
        if (strongest >= settings.MinimumSignalQuality)
        {
            var before = candidates.Count;
            candidates.RemoveAll(c => c.SignalQuality < settings.MinimumSignalQuality);
            if (before != candidates.Count)
            {
                rejections.Add($"{before - candidates.Count} network(s) below minimumSignalQuality " +
                               $"{settings.MinimumSignalQuality} dropped because a stronger option exists");
            }
        }

        var ranked = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.SignalQuality)
            .ThenByDescending(c => c.Rssi)
            .ThenBy(c => c.Ssid, StringComparer.Ordinal)
            .ToList();

        rejectionReasons = rejections;
        return ranked;
    }

    private static (double Value, string Reason) Score(
        ScannedNetwork network,
        WifiSettings settings,
        string? lastSuccessfulProfile)
    {
        var value = (double)network.SignalQuality;
        var parts = new List<string> { $"signal={network.SignalQuality}" };

        if (settings.PreferHighBand && network.Band is NetworkBand.Band5GHz or NetworkBand.Band6GHz)
        {
            value += settings.HighBandBonus;
            parts.Add($"+{settings.HighBandBonus:F1} highBand({network.Band})");
        }

        if (settings.PreferRecentProfiles &&
            !string.IsNullOrWhiteSpace(lastSuccessfulProfile) &&
            (string.Equals(lastSuccessfulProfile, network.ProfileName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lastSuccessfulProfile, network.Ssid, StringComparison.OrdinalIgnoreCase)))
        {
            value += settings.RecentProfileBonus;
            parts.Add($"+{settings.RecentProfileBonus:F1} recentlyUsed");
        }

        return (value, string.Join(", ", parts));
    }
}
