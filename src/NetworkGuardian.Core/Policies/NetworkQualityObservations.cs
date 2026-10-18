using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

public static class NetworkQualityObservations
{
    public static double[] Features(LinkQualityEstimate quality, bool online, int? signal) => new[]
    {
        quality.Score / 100, quality.TcpFailure ?? quality.GatewayLoss ?? .1,
        Math.Min(1, (quality.LatencyMs ?? 100) / 500), Math.Min(1, quality.JitterMs / 100),
        signal is { } strength ? strength / 100d : .5, quality.Confidence, online ? 1d : 0, .5,
    };

    public static IReadOnlyList<DistillationExample> Capture(GuardianSnapshot snapshot,
        IReadOnlyList<LinkQualityEstimate> history, DateTimeOffset now)
    {
        var result = new List<DistillationExample>();
        foreach (var iface in snapshot.Interfaces)
        {
            if (NetworkQualityIdentity.UnavailableReason(iface) is not null) continue;
            var wifi = snapshot.WifiAdapters.FirstOrDefault(a => a.InterfaceGuid == iface.WlanInterfaceGuid);
            var identity = NetworkQualityIdentity.Create(iface, wifi);
            var quality = identity?.Find(history);
            if (quality is null || !quality.IsMeasured || !NetworkQualityIdentity.Fresh(quality.UpdatedAtUtc, now) ||
                quality.UpdatedAtUtc > now || quality.TcpFailure is null && quality.GatewayLoss is null) continue;
            var online = iface.Probe is { } probe && NetworkQualityIdentity.Fresh(probe.TimestampUtc, now) && probe.TimestampUtc <= now
                ? probe.IsOnline : quality.InternetOnline;
            if (online is null) continue;
            var features = Features(quality, online.Value, wifi?.SignalQuality);
            if (features.Any(x => !double.IsFinite(x) || x < 0 || x > 1)) continue;
            result.Add(new() { Id = BrainDistillation.ExampleId(features), Features = features,
                Source = "observed-aggregate", ObservedAtUtc = quality.UpdatedAtUtc,
                NetworkKind = iface.Kind, InterfaceId = iface.Id, NetworkId = identity!.NetworkId,
                NetworkName = identity.NetworkName });
        }
        return result;
    }
}
