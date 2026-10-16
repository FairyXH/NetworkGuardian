using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

public static class ActionConfidencePolicy
{
    public static GuardianAction Assess(GuardianAction action, GuardianInput input)
    {
        var guid = action switch { ConnectWifiAction c => c.InterfaceGuid, DisconnectWifiAction d => d.InterfaceGuid,
            ScanAdapterAction s => s.InterfaceGuid, _ => Guid.Empty };
        var ssid = action switch { ConnectWifiAction c => c.Ssid, DisconnectWifiAction d => d.Ssid, _ => "" };
        var adapter = input.WifiAdapters.FirstOrDefault(a => a.InterfaceGuid == guid);
        var quality = input.LinkQuality.FirstOrDefault(q => q.Adapter == guid && q.Ssid == ssid &&
            input.Now - q.UpdatedAtUtc < TimeSpan.FromMinutes(5));
        var freshScan = adapter?.LastScan?.CompletedAtUtc is { } at && input.Now - at < TimeSpan.FromMinutes(1);
        var confidence = action switch
        {
            ScanAdapterAction => .9,
            ConnectWifiAction => .3 + (freshScan ? .2 : 0) + .35 * (quality?.Confidence ?? 0),
            DisconnectWifiAction when WifiBindingPolicy.Owner(input.Config.Wifi, ssid) is { } owner && owner != guid => .95,
            DisconnectWifiAction => .45 + .35 * (quality?.Confidence ?? 0),
            ApplyInterfaceMetricsAction => input.Interfaces.Any(i => i.Probe is { IsOnline: true } p && input.Now - p.TimestampUtc < TimeSpan.FromSeconds(30)) ? .9 : .4,
            WaitAction => .85,
            _ => .65,
        };
        return action with { Confidence = Math.Clamp(confidence, .1, .95),
            ConfidenceEvidence = $"执行依据可信度（非成功概率）；实测 {quality?.Samples ?? 0} 组；{(freshScan ? "扫描新鲜" : "无近期扫描")}；回答仅提供软偏好" };
    }
}
