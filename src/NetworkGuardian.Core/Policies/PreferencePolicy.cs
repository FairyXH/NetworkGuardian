using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

/// <summary>Online pairwise logistic preference model, trained only on user choices.</summary>
public static class PreferencePolicy
{
    public static Dictionary<string, double> Features(PreferenceOption option, DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var slot = local.Hour / 4;
        var weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "weekend" : "weekday";
        var ssid = option.Candidate.Ssid;
        var adapter = option.Candidate.InterfaceGuid;
        return new(StringComparer.Ordinal)
        {
            [$"network:{ssid}"] = 1, [$"adapter:{adapter:D}"] = 1,
            [$"network:{ssid}:{weekend}:{slot}"] = 1,
            [$"adapter:{adapter:D}:{weekend}:{slot}"] = 1,
            ["quality"] = (option.Quality - 50) / 50,
            ["failure"] = option.FailureRate ?? 0,
            ["latency"] = Math.Min(1, (option.LatencyMs ?? 100) / 500),
            ["jitter"] = Math.Min(1, option.JitterMs / 100),
            ["signal"] = option.Candidate.SignalQuality / 100d,
            ["confidence"] = option.Confidence,
        };
    }

    public static void Train(PreferenceState state, PreferenceFeedback feedback)
    {
        foreach (var key in state.Weights.Keys.ToArray()) state.Weights[key] *= .999;
        var chosen = Features(feedback.Chosen, feedback.AtUtc);
        var alternatives = feedback.Alternatives.Where(a => a.Key != feedback.Chosen.Key).ToArray();
        foreach (var alternative in alternatives)
        {
            var other = Features(alternative, feedback.AtUtc);
            var delta = chosen.Keys.Union(other.Keys).ToDictionary(key => key,
                key => chosen.GetValueOrDefault(key) - other.GetValueOrDefault(key), StringComparer.Ordinal);
            var probability = Sigmoid(delta.Sum(p => state.Weights.GetValueOrDefault(p.Key) * p.Value));
            var rate = (feedback.Source == "explicit" ? .2 : .04) * Math.Clamp(feedback.Reliability, 0, 1) / Math.Max(1, alternatives.Length);
            foreach (var pair in delta)
                state.Weights[pair.Key] = Math.Clamp(state.Weights.GetValueOrDefault(pair.Key) + rate * (1 - probability) * pair.Value, -4, 4);
        }
        if (alternatives.Length > 0 && feedback.Source is "explicit" or "survey" && feedback.Reliability >= .4) state.ExplicitChoices++;
        state.Feedback.Add(feedback);
        if (state.Feedback.Count > 500) state.Feedback.RemoveRange(0, state.Feedback.Count - 500);
        if (state.Weights.Count > 4096)
            state.Weights = state.Weights.OrderByDescending(p => Math.Abs(p.Value)).Take(4096).ToDictionary(p => p.Key, p => p.Value);
    }

    public static double Predict(PreferenceState state, PreferenceOption option, DateTimeOffset now) =>
        Sigmoid(Features(option, now).Sum(p => state.Weights.GetValueOrDefault(p.Key) * p.Value));

    public static double Bonus(PreferenceState state, PreferenceOption option, DateTimeOffset now)
    {
        if (state.ExplicitChoices < 10 || option.Quality < 50 || option.Confidence < .25) return 0;
        var last = state.Feedback.LastOrDefault()?.AtUtc;
        var freshness = last is { } at ? Math.Exp(-Math.Max(0, (now - at).TotalDays) / 30) : 0;
        return Math.Clamp((Predict(state, option, now) - .5) * 16 * freshness, -4, 4);
    }

    public static PreferenceOption Option(WifiCandidate candidate, IReadOnlyList<LinkQualityEstimate> history, DateTimeOffset now)
    {
        var quality = history.FirstOrDefault(q => q.Adapter == candidate.InterfaceGuid && q.Ssid == candidate.Ssid &&
            q.Band == candidate.Band && now - q.UpdatedAtUtc < TimeSpan.FromMinutes(5));
        return new()
        {
            Candidate = candidate, Quality = quality?.Score ?? 50, Confidence = quality?.Confidence ?? 0,
            FailureRate = quality?.TcpFailure, LatencyMs = quality?.LatencyMs, JitterMs = quality?.JitterMs ?? 0,
        };
    }

    public static bool CanAsk(PreferenceState state, string context, DateTimeOffset now) =>
        now - state.LastQuestionUtc >= TimeSpan.FromDays(1) &&
        (!state.QuestionContexts.TryGetValue(context, out var last) || now - last >= TimeSpan.FromDays(7));

    public static string Context(IEnumerable<PreferenceOption> options, DateTimeOffset now) =>
        $"{now.ToLocalTime().Hour / 4}|" + string.Join(";", options.Select(o => o.Key).Order(StringComparer.Ordinal));

    private static double Sigmoid(double value) => 1 / (1 + Math.Exp(-Math.Clamp(value, -30, 30)));
}
