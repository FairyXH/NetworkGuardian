using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

public static class NetworkQualityPolicy
{
    public static LinkQualityEstimate Estimate(LinkQualityHistory history, DateTimeOffset now)
    {
        var samples = history.Samples.Where(s => s.AtUtc <= now.AddSeconds(10) && now - s.AtUtc < TimeSpan.FromMinutes(30)).TakeLast(40).ToArray();
        if (samples.Length == 0) return new() { Adapter = history.Adapter, Ssid = history.Ssid, Band = history.Band,
            Kind = history.Kind, InterfaceId = history.InterfaceId, NetworkId = history.NetworkId, NetworkName = history.NetworkName };
        // Echo filtering is common. A gateway must have answered before silence counts as loss.
        var gatewayKnown = history.Samples.Any(s => s.GatewayReplies > 0);
        var internetKnown = history.Samples.Any(s => s.InternetReplies > 0);
        var gatewayLoss = gatewayKnown ? Loss(samples.Sum(s => s.GatewaySent), samples.Sum(s => s.GatewayReplies)) : null;
        var internetLoss = internetKnown ? Loss(samples.Sum(s => s.InternetSent), samples.Sum(s => s.InternetReplies)) : null;
        var tcpKnown = history.Samples.Any(s => s.TcpReplies > 0) || samples.Any(s => s.InternetOnline == false && s.TcpSent > 0);
        var tcpLoss = tcpKnown ? Loss(samples.Sum(s => s.TcpSent), samples.Sum(s => s.TcpReplies)) : null;
        var delays = samples.Where(s => s.LatencyMs.HasValue).Select(s => s.LatencyMs!.Value).ToArray();
        var latency = delays.Length > 0 ? delays.Average() : (double?)null;
        var jitter = samples.Average(s => s.JitterMs);
        var score = Score(gatewayLoss, internetLoss, tcpLoss, latency, jitter);
        var last = samples[^1];
        return new()
        {
            Adapter = history.Adapter, Ssid = history.Ssid, Band = history.Band,
            Kind = history.Kind, InterfaceId = history.InterfaceId, NetworkId = history.NetworkId, NetworkName = history.NetworkName,
            UpdatedAtUtc = samples[^1].AtUtc, Samples = samples.Length,
            GatewayLoss = gatewayLoss, InternetLoss = internetLoss, TcpFailure = tcpLoss,
            LatencyMs = latency, JitterMs = jitter,
            Score = score * .8 + history.LongTermScore * .2,
            RecentScore = Score(gatewayKnown ? Loss(last.GatewaySent, last.GatewayReplies) : null,
                internetKnown ? Loss(last.InternetSent, last.InternetReplies) : null,
                tcpKnown ? Loss(last.TcpSent, last.TcpReplies) : null, last.LatencyMs, last.JitterMs),
        };
    }

    public static double Score(double? gatewayLoss, double? internetLoss, double? tcpLoss, double? latency, double jitter) =>
        Math.Clamp(100 - Math.Max(gatewayLoss ?? 0, tcpLoss ?? 0) * 110 -
            // Public ICMP alone is weak evidence; do not penalize echo filtering as a broken adapter.
            (internetLoss ?? 0) * 10 - Math.Min(15, (latency ?? 100) / 30) - Math.Min(10, jitter / 10), 0, 100);

    public static double CombinationScore(Guid adapter, string ssid, NetworkBand band,
        IReadOnlyList<LinkQualityEstimate> history, DateTimeOffset now)
    {
        var fresh = history.Where(q => q.Kind == InterfaceKind.Wifi && q.IsMeasured && now - q.UpdatedAtUtc < TimeSpan.FromDays(7)).ToArray();
        var exact = fresh.FirstOrDefault(q => q.Adapter == adapter && q.Ssid == ssid && q.Band == band);
        var device = fresh.Where(q => q.Adapter == adapter).ToArray();
        var network = fresh.Where(q => q.Ssid == ssid && (band == NetworkBand.Unknown || q.Band == band)).ToArray();
        var adapterScore = device.Length > 0 ? device.Average(q => q.Score) : 50;
        var networkScore = network.Length > 0 ? network.Max(q => q.Score) : 50;
        // The product makes reliable hardware valuable specifically for good networks.
        var value = exact is not null ? exact.Score * .7 + adapterScore * .3 :
            adapterScore * .65 + networkScore * .35;
        var confidence = exact?.Confidence ?? (device.Length > 0 ? device.Max(q => q.Confidence) * .6 : .2);
        return 50 + (value - 50) * confidence;
    }

    public static WifiCandidate Adjust(WifiCandidate candidate, IReadOnlyList<LinkQualityEstimate> history, DateTimeOffset now)
    {
        var quality = CombinationScore(candidate.InterfaceGuid, candidate.Ssid, candidate.Band, history, now);
        var network = history.Where(q => q.Kind == InterfaceKind.Wifi && q.Ssid == candidate.Ssid && q.IsMeasured && now - q.UpdatedAtUtc < TimeSpan.FromDays(7))
            .Select(q => q.Score).DefaultIfEmpty(50).Max();
        return candidate with
        {
            Score = (candidate.Score * .25 + network * .25 + 50) * Math.Pow(quality / 100, 2),
            ScoreReason = candidate.ScoreReason + $", quality={quality:F0}/100 (接口历史与网络组合)",
        };
    }

    private static double? Loss(int sent, int received) => sent > 0 ? 1 - received / (double)sent : null;
}
