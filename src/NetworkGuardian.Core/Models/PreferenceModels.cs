namespace NetworkGuardian.Core.Models;

public sealed class PreferenceState
{
    public Dictionary<string, double> Weights { get; set; } = new(StringComparer.Ordinal);
    public List<PreferenceFeedback> Feedback { get; set; } = new();
    public int ExplicitChoices { get; set; }
    public DateTimeOffset LastQuestionUtc { get; set; }
    public Dictionary<string, DateTimeOffset> QuestionContexts { get; set; } = new();
}

public sealed record PreferenceOption
{
    public required WifiCandidate Candidate { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public double Quality { get; init; } = 50;
    public double? FailureRate { get; init; }
    public double? LatencyMs { get; init; }
    public double JitterMs { get; init; }
    public double Confidence { get; init; }
    public bool Connected { get; init; }
    public string Key => $"{Candidate.InterfaceGuid:D}|{Candidate.Ssid}";
    public string Label => $"{Candidate.Ssid} · {AdapterName}";
}

public sealed record PreferenceFeedback
{
    public DateTimeOffset AtUtc { get; init; }
    public string Source { get; init; } = "explicit";
    public double Reliability { get; init; } = 1;
    public required PreferenceOption Chosen { get; init; }
    public IReadOnlyList<PreferenceOption> Alternatives { get; init; } = Array.Empty<PreferenceOption>();
}

public sealed record PreferenceQuestion(string Context, IReadOnlyList<PreferenceOption> Options, DateTimeOffset CreatedAtUtc);
