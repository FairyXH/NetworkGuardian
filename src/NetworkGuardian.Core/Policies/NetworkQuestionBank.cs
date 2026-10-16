using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

public static class NetworkQuestionBank
{
    public static IReadOnlyList<SurveyTemplate> Templates { get; } = Build();

    private static IReadOnlyList<SurveyTemplate> Build()
    {
        var groups = new Dictionary<string, string[]>
        {
            ["campus"] = new[] {
                "现在 {adapter} 连接的 {ssid} 是校园网吗？", "{ssid} 是学校提供的网络吗？",
                "连接 {ssid} 时通常需要学校账号吗？", "{ssid} 的使用时间受学校作息限制吗？",
                "{ssid} 是否属于宿舍或教学区网络？", "离开学校后，您通常还会使用 {ssid} 吗？" },
            ["adapter"] = new[] {
                "{ssid} 您更习惯连接在哪块网卡上？", "在当前环境，您希望哪块网卡优先连接 {ssid}？",
                "长时间使用 {ssid} 时，您更愿意用哪块网卡？", "{ssid} 用于视频会议时，您偏向哪块网卡？",
                "{ssid} 用于下载时，您偏向哪块网卡？", "{ssid} 用于日常浏览时，您偏向哪块网卡？",
                "{ssid} 用于游戏时，您偏向哪块网卡？", "{ssid} 断开后重连，您更希望先尝试哪块网卡？" },
            ["priority"] = new[] {
                "现在选择网络，您最看重什么？", "工作日这个时段，您最看重哪项网络表现？",
                "周末使用网络时，您通常最看重什么？", "进行视频会议时，您最看重什么？",
                "玩在线游戏时，您最看重什么？", "下载大文件时，您最看重什么？",
                "观看在线视频时，您最看重什么？", "远程连接其他电脑时，您最看重什么？",
                "夜间使用网络时，您最看重什么？", "网络质量接近时，您会优先考虑什么？",
                "正在上传文件时，您最希望优先保证什么？", "电脑无人值守时，您最看重什么？" },
            ["stable"] = new[] {
                "刚才使用 {ssid} 时感觉稳定吗？", "现在 {ssid} 有明显卡顿吗？",
                "{adapter} 连接 {ssid} 时，经常短暂掉线吗？", "最近使用 {ssid}，视频播放顺畅吗？",
                "{adapter} 连接 {ssid} 时，网页打开通常顺畅吗？", "{ssid} 在当前时段通常可靠吗？",
                "{ssid} 用于语音通话时通常顺畅吗？", "您最近是否感觉 {ssid} 比平时更慢？" },
            ["metered"] = new[] {
                "{ssid} 有流量配额或按流量收费吗？", "{ssid} 是手机热点吗？",
                "使用 {ssid} 时，您需要节省流量吗？", "{ssid} 适合长时间下载大文件吗？",
                "{ssid} 是否有每月流量上限？", "您希望把 {ssid} 主要留作应急备用吗？" },
            ["role"] = new[] {
                "{ssid} 您通常希望作为哪种网络？", "在当前时段，{ssid} 更适合扮演什么角色？",
                "工作期间，{ssid} 您通常如何使用？", "晚上，{ssid} 您通常如何使用？",
                "周末，{ssid} 您通常如何使用？", "同时有多个网络时，{ssid} 您倾向如何安排？",
                "网络恢复正常后，{ssid} 您希望如何安排？", "电脑长时间在线时，{ssid} 您倾向如何使用？" },
            ["recovery"] = new[] {
                "网络只是轻微变慢时，您希望管家怎么做？", "正在视频会议时发现更好的网络，您希望怎么做？",
                "后台下载时发现更好的网络，您希望怎么做？", "网络偶尔丢包时，您希望管家怎么做？",
                "连接还可用但质量持续下降时，您希望怎么做？", "两块网卡表现接近时，您希望怎么做？" },
            ["schedule"] = new[] {
                "您通常在哪个时段最需要稳定网络？", "您通常在哪个时段下载较多？",
                "您通常在哪个时段进行视频会议？", "您通常在哪个时段玩在线游戏？",
                "您通常在哪个时段希望减少自动切换？", "您通常在哪个时段主要使用校园网？" },
        };
        return groups.SelectMany(g => g.Value.Select((p, i) => new SurveyTemplate($"{g.Key}-{i + 1:D2}", g.Key, p))).ToArray();
    }

    public static NetworkSurveyQuestion? Next(SurveyContext context, string? previousId, Random random)
    {
        var connected = context.Links.Where(l => l.Connected && l.Ssid.Length > 0).ToArray();
        var eligible = Templates.Where(t => t.Id != previousId &&
            (t.Kind is "priority" or "recovery" or "schedule" || connected.Length > 0) &&
            (t.Kind != "adapter" || context.Links.Select(l => l.Adapter).Distinct().Count() > 1)).ToArray();
        if (eligible.Length == 0) return null;
        var template = eligible[random.Next(eligible.Length)];
        var subject = connected.Length > 0 ? connected[random.Next(connected.Length)] : null;
        var options = template.Kind switch
        {
            "adapter" => context.Links.DistinctBy(l => l.Adapter).Select(l => new SurveyOption(l.Adapter.ToString("D"), l.AdapterName)).ToArray(),
            "priority" => Options("stability|稳定、少丢包", "latency|低延迟", "speed|传输速度", "cost|节省流量"),
            "role" => Options("primary|主要网络", "backup|备用网络", "avoid|尽量不用", "depends|视情况而定"),
            "recovery" => Options("keep|保留现有连接", "verify|先验证更好网络再切换", "ask|先询问我"),
            "schedule" => Options("morning|早晨", "daytime|白天", "evening|晚上", "night|深夜", "variable|不固定"),
            _ => Options("yes|是", "no|不是", "unknown|不确定"),
        };
        return new(Guid.NewGuid().ToString("N"), template,
            template.Prompt.Replace("{adapter}", subject?.AdapterName ?? "当前网卡").Replace("{ssid}", subject?.Ssid ?? "当前网络"),
            subject?.Ssid ?? "", subject?.Adapter, options, context);
    }

    private static SurveyOption[] Options(params string[] values) => values.Select(v => v.Split('|'))
        .Select(p => new SurveyOption(p[0], p[1])).ToArray();

    public static SurveyAnswer Evaluate(NetworkSurveyQuestion question, string value, SurveyContext context)
    {
        if (!question.Options.Any(o => o.Value == value)) throw new ArgumentException("Invalid survey answer", nameof(value));
        var reliability = value is "unknown" or "depends" or "variable" ? .15 : .55;
        var evidence = "用户回答作为软标签；探测不能证明校园归属、收费或个人习惯";
        var live = context.Links.FirstOrDefault(l => l.Adapter == question.Adapter && l.Ssid == question.Ssid && l.Connected);
        if (question.Ssid.Length > 0 && live is null)
        {
            reliability *= .4;
            evidence = "答题时网络状态已变化，本条样本降权";
        }
        if (question.Template.Kind == "stable" && live?.Quality is { IsMeasured: true } quality)
        {
            // Negative questions reverse yes/no semantics.
            var negative = question.Template.Id is "stable-02" or "stable-03" or "stable-08";
            var reportsStable = value == (negative ? "no" : "yes");
            var agrees = reportsStable == (quality.Score >= 65);
            reliability = value == "unknown" ? .15 : agrees ? .75 : .2;
            evidence = agrees ? "回答与近期接口质量采样一致" : "回答与近期接口质量采样有冲突，保留回答但降低权重";
        }
        return new() { Question = question, Value = value, AnswerContext = context, Reliability = reliability, Evidence = evidence };
    }
}
