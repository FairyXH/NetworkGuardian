using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

public static class SurveyLearning
{
    public static void Record(NetworkHistory state, SurveyAnswer answer)
    {
        if (state.Survey.Answers.Any(a => a.Question.Id == answer.Question.Id)) return;
        state.Survey.Answers.Add(answer);
        state.Survey.TotalAnswers++;
        if (state.Survey.Answers.Count > 1000) state.Survey.Answers.RemoveAt(0);
        var key = $"{answer.Question.Template.Id}|{answer.Question.Ssid}|{answer.Value}";
        state.Survey.SoftLabels[key] = Math.Min(20, state.Survey.SoftLabels.GetValueOrDefault(key) * .99 + answer.Reliability);
        if (state.Survey.SoftLabels.Count > 4096)
            state.Survey.SoftLabels = state.Survey.SoftLabels.OrderByDescending(p => p.Value).Take(4096).ToDictionary(p => p.Key, p => p.Value);
        state.Brain ??= NetworkBrain.Seed();
        var links = answer.AnswerContext.Links;
        if (answer.Question.Template.Kind == "adapter" && Guid.TryParse(answer.Value, out var selected) && answer.Reliability >= .4)
        {
            var alternatives = links.DistinctBy(l => l.Adapter).Select(l => Option(l, answer.Question.Ssid)).ToArray();
            var chosen = alternatives.FirstOrDefault(o => o.Candidate.InterfaceGuid == selected);
            if (chosen is not null && alternatives.Length > 1)
            {
                PreferencePolicy.Train(state.Preferences, new() { AtUtc = answer.AnswerContext.CapturedAt, Source = "survey",
                    Reliability = answer.Reliability, Chosen = chosen, Alternatives = alternatives });
                foreach (var option in alternatives.Where(o => o.Confidence >= .25))
                {
                    var x = NetworkBrain.Features(option, true);
                    var softTarget = option.Key == chosen.Key ? .75 : .35;
                    NetworkBrain.Train(state.Brain, x, .85 * NetworkBrain.Teacher(x) + .15 * softTarget,
                        .008 * answer.Reliability, personal: true);
                }
            }
        }
        else if (answer.Question.Template.Kind == "priority")
        {
            foreach (var link in links.Where(l => l.Connected && l.Quality?.IsMeasured == true))
            {
                var option = Option(link, link.Ssid);
                var x = NetworkBrain.Features(option, link.Online);
                var desired = answer.Value switch
                {
                    "latency" => .6 * (1 - x[2]) + .4 * x[0],
                    "stability" => .65 * (1 - x[1]) + .35 * x[0],
                    // No throughput measurement: signal must not be substituted for bandwidth.
                    "speed" => NetworkBrain.Teacher(x),
                    _ => NetworkBrain.Teacher(x),
                };
                // Small updates, anchored to the teacher, cannot turn subjective answers into truth.
                NetworkBrain.Train(state.Brain, x, .8 * NetworkBrain.Teacher(x) + .2 * desired,
                    .008 * answer.Reliability, personal: true);
            }
        }
    }

    private static PreferenceOption Option(SurveyLink link, string ssid) => new()
    {
        Candidate = new() { InterfaceGuid = link.Adapter, Ssid = ssid, ProfileName = ssid,
            Band = link.Band, SignalQuality = link.Signal }, AdapterName = link.AdapterName,
        Quality = link.Ssid == ssid ? link.Quality?.Score ?? 50 : 50,
        Confidence = link.Ssid == ssid ? link.Quality?.Confidence ?? 0 : 0,
        FailureRate = link.Ssid == ssid ? link.Quality?.TcpFailure : null,
        LatencyMs = link.Ssid == ssid ? link.Quality?.LatencyMs : null,
        Connected = link.Connected && link.Ssid == ssid,
    };
}
