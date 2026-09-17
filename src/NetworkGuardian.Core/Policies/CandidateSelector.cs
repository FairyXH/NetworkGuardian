using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

/// <summary>
/// Turns a raw per-adapter scan into a ranked list of connectable, already-saved profiles.
/// It is used only when an adapter has no working connection: the sticky connection policy forbids
/// using it to switch adapters that are currently fine.
/// </summary>
public sealed class CandidateSelector
{
    private readonly ConnectFailureBlacklist _blacklist;

    public CandidateSelector(ConnectFailureBlacklist blacklist)
    {
        _blacklist = blacklist;
    }

    public IReadOnlyList<WifiCandidate> SelectCandidates(
        Guid interfaceGuid,
        AdapterScanSnapshot? scan,
        IReadOnlyList<string> savedProfiles,
        WifiSettings settings,
        DateTimeOffset now,
        out IReadOnlyList<string> rejectionReasons,
        string? lastSuccessfulProfile = null,
        string? excludeSsid = null)
    {
        var rejections = new List<string>();
        var profiles = new HashSet<string>(savedProfiles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

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

            if (excludeSsid is not null && string.Equals(network.Ssid, excludeSsid, StringComparison.Ordinal))
            {
                rejections.Add($"{network.Ssid}: currently connected on this adapter");
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
