namespace NetworkGuardian.Core.Models;

public sealed record SurveyLink
{
    public Guid Adapter { get; init; }
    public string AdapterName { get; init; } = "";
    public string Ssid { get; init; } = "";
    public bool Connected { get; init; }
    public bool Online { get; init; }
    public bool CampusConfigured { get; init; }
    public int Signal { get; init; }
    public NetworkBand Band { get; init; }
    public LinkQualityEstimate? Quality { get; init; }
}

public sealed record SurveyContext
{
    public DateTimeOffset CapturedAt { get; init; }
    public bool InternetOnline { get; init; }
    public bool AutomaticRecovery { get; init; }
    public IReadOnlyList<SurveyLink> Links { get; init; } = Array.Empty<SurveyLink>();
    public IReadOnlyList<string> VisibleSsids { get; init; } = Array.Empty<string>();
    public Dictionary<string, string> Bindings { get; init; } = new();
}

public sealed record SurveyOption(string Value, string Label);
public sealed record SurveyTemplate(string Id, string Kind, string Prompt);
public sealed record NetworkSurveyQuestion(string Id, SurveyTemplate Template, string Prompt, string Ssid,
    Guid? Adapter, IReadOnlyList<SurveyOption> Options, SurveyContext AskedContext);
public sealed record SurveyAnswer
{
    public required NetworkSurveyQuestion Question { get; init; }
    public required string Value { get; init; }
    public required SurveyContext AnswerContext { get; init; }
    public double Reliability { get; init; }
    public string Evidence { get; init; } = "";
}

public sealed class SurveyState
{
    public long Revision { get; set; }
    public List<SurveyAnswer> Answers { get; set; } = new();
    public Dictionary<string, double> SoftLabels { get; set; } = new();
    public long TotalAnswers { get; set; }
}
