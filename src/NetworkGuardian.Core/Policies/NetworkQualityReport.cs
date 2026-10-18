using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

public sealed record NetworkQualityReport(string Summary, IReadOnlyList<string> Findings,
    string Advice, string Evidence);

/// <summary>Evidence-based explanations, independent of the model's uncalibrated utility output.</summary>
public static class NetworkQualityReporter
{
    public static NetworkQualityReport Build(GuardianSnapshot snapshot, IReadOnlyList<LinkQualityEstimate> quality,
        GuardianConfig config, NetworkBrainModel brain, DateTimeOffset now)
    {
        static bool Fresh(DateTimeOffset at, DateTimeOffset clock) => at <= clock.AddSeconds(10) && clock - at <= TimeSpan.FromMinutes(5);
        var probe = snapshot.GlobalProbe;
        var current = probe.AttemptCount > 0 && Fresh(probe.TimestampUtc, now) && Fresh(snapshot.TimestampUtc, now);
        var summary = !current ? "正在了解当前网络，暂时还不能判断是否能正常上网。"
            : probe.IsOnline ? "目前可以上网。" : probe.IsStableOnline
                ? "刚出现一次外网探测失败，正在复查，暂不判定为持续断网。"
                : "目前未能验证外网可用；这不一定是网络接口本身的问题。";
        var findings = new List<string>();
        var outlet = snapshot.Interfaces.FirstOrDefault(i => i.IsDefaultRoute);
        var outletFailed = current && probe.IsOnline && outlet?.Probe is { AttemptCount: > 0, IsOnline: false } outletProbe &&
            Fresh(outletProbe.TimestampUtc, now);
        if (outletFailed)
            summary = "已探测到可用网络，但当前出口未通过外网验证，实际使用可能仍受影响。";
        else if (current && probe.IsOnline && snapshot.OutletMatchesPolicy == false)
            summary += "当前出口与管理策略不一致，需要核对路由。";
        if (outlet is not null)
            findings.Add($"当前出口是“{outlet.Name}”（{NetworkQualityIdentity.KindName(outlet.Kind)}）。已连通不等于质量优秀，以下评分以持续实测为准。");
        var rated = new List<LinkQualityEstimate>();
        var subjects = new List<(string Prefix, string? Unavailable, NetworkQualityIdentity? Identity)>();
        foreach (var adapter in snapshot.WifiAdapters)
        {
            var name = AdapterName(snapshot, adapter);
            subjects.Add(($"“{name}”连接“{adapter.CurrentSsid ?? "—"}”：",
                adapter.IsConnected ? null : "目前未连接，无法评价它在当前环境中的实际质量。",
                new(adapter.InterfaceGuid, adapter.CurrentSsid ?? "", adapter.Connection?.Band ?? NetworkBand.Unknown,
                    InterfaceKind.Wifi, "", "", adapter.CurrentSsid ?? "", adapter.DeviceInstanceId)));
        }
        foreach (var iface in snapshot.Interfaces.Where(i => i.Kind != InterfaceKind.Wifi ||
                     !snapshot.WifiAdapters.Any(a => a.InterfaceGuid == i.WlanInterfaceGuid)))
            subjects.Add(($"“{iface.Name}”（{NetworkQualityIdentity.KindName(iface.Kind)}）：",
                NetworkQualityIdentity.UnavailableReason(iface), NetworkQualityIdentity.Create(iface)));
        var outletIdentity = outlet is null ? null : NetworkQualityIdentity.Create(outlet,
            snapshot.WifiAdapters.FirstOrDefault(a => a.InterfaceGuid == outlet.WlanInterfaceGuid));
        foreach (var subject in subjects.OrderBy(s => outletIdentity is not null && s.Identity?.Adapter == outletIdentity.Adapter
                     ? 0 : s.Unavailable is null ? 1 : 2))
        {
            var prefix = subject.Prefix;
            if (subject.Unavailable is { } unavailable)
            {
                findings.Add(prefix + unavailable);
                continue;
            }
            if (!config.Quality.Enabled)
            {
                findings.Add(prefix + "质量采样已关闭，暂不使用历史评分评价当前连接。");
                continue;
            }
            var q = subject.Identity?.Find(quality);
            if (q is null || !Fresh(q.UpdatedAtUtc, now) || !Fresh(snapshot.TimestampUtc, now))
            {
                findings.Add(prefix + (q is null ? "还没有本次连接的实测数据，正在等待采样。" : "上次测量已过时，等待新的采样后再评价。"));
                continue;
            }
            if (!q.IsMeasured || !double.IsFinite(q.Score) || (q.GatewayLoss is null && q.TcpFailure is null))
            {
                findings.Add(prefix + $"已有 {q.Samples} 组采样，但有效证据仍不足，暂不下质量结论。");
                continue;
            }
            rated.Add(q);
            var grade = q.Grade;
            var text = prefix + $"质量{grade}（{q.Score:F0} 分）。";
            if (q.GatewayLoss is >= .05)
                text += $"到网关的本地链路 ICMP 探测约有 {q.GatewayLoss:P0} 未回应，可能出现卡顿；可能与链路或响应策略有关，尚不能确定是硬件损坏。";
            if (q.TcpFailure is >= .05)
                text += $"外网连接尝试约有 {q.TcpFailure:P0} 失败，网页或下载可能需要重试。";
            if (q.GatewayLoss is <= .01 && q.TcpFailure is <= .01)
                text += "本地链路和外网连接尝试都较稳定。";
            else if (q.GatewayLoss is > .01 and < .05 || q.TcpFailure is > .01 and < .05)
                text += "偶尔有丢包或连接失败，建议继续观察。";
            if (q.LatencyMs is { } latency)
                text += latency >= 150 ? $"平均响应约 {latency:F0} 毫秒，实时通话或游戏可能感觉迟钝。"
                    : $"平均响应约 {latency:F0} 毫秒。";
            if (q.JitterMs >= 30) text += $"延迟波动约 {q.JitterMs:F0} 毫秒，实时应用可能不够平稳。";
            if (q.InternetLoss is >= .2 && q.TcpFailure is < .05)
                text += "公网 ping 回应较少，但外网连接正常，不据此判断断网。";
            if (q.GatewayLoss is null || q.TcpFailure is null)
                text += "部分丢包或外网连接指标尚未测明，评价仍有局限。";
            text += $"依据充分度{(q.Samples >= 20 ? "较高" : "一般")}（{q.Samples} 组采样）。";
            findings.Add(text);
        }
        if (subjects.Count == 0) findings.Add("当前没有可评价的网络接口；已保持禁用的网卡不参与此次评价。");
        var advice = outletFailed ? "先核对当前外网出口及路由；有备用网络可用，不代表当前出口已经恢复。"
            : !current ? "等待新一轮连通性探测和质量采样，再决定是否需要调整网络。"
            : !probe.IsOnline ? "先等待复查；若持续无法上网，请检查校园网认证、路由器及宽带出口。"
            : rated.Any(q => q.GatewayLoss is >= .05 || q.TcpFailure is >= .05)
                ? "保留当前可用出口，优先排查上述不稳定的网络连接；不要仅凭一次测量或信号强度更换网卡。"
                : rated.Count > 0 ? "暂时不必换网，继续观察稳定性即可。"
                : "目前只确认了能否上网，积累更多有效采样后再比较网卡质量。";
        if (!config.Quality.Enabled) advice += "如需持续质量评价，可在“自动管理”中开启质量评级。";
        var evidence = $"基于本机近期探测生成；未测量下载带宽，不能据此判断网速上限。" +
            (config.Quality.Enabled && config.Quality.LearningEnabled && config.Quality.ApplyLearnedPreferences
                ? $"本地模型已进行 {brain.PersonalSteps} 次个人更新，可有限参与选网评分。"
                : "质量评估、学习或偏好应用已关闭，模型不参与选网评分。") +
            "评价文字由实测数据和解释规则生成，不是神经网络自由生成的诊断。";
        return new(summary, findings, advice, evidence);
    }

    private static string AdapterName(GuardianSnapshot snapshot, WifiAdapterRuntimeState adapter)
    {
        var device = snapshot.WifiDevices.FirstOrDefault(d =>
            (!string.IsNullOrEmpty(adapter.DeviceInstanceId) && string.Equals(d.Record.DeviceInstanceId, adapter.DeviceInstanceId, StringComparison.OrdinalIgnoreCase)) ||
            (Guid.TryParse(d.Record.NetCfgInstanceId, out var guid) && guid == adapter.InterfaceGuid));
        return device?.Record.FriendlyName ?? device?.Record.DeviceDescription ?? adapter.Description;
    }
}
