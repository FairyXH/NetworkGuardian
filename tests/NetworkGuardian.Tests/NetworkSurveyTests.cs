using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Infrastructure.Configuration;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class NetworkSurveyTests
{
    [Fact]
    public void BankHasSixtyDistinctTemplatesAndRandomSkipDoesNotAnswer()
    {
        Assert.Equal(60, NetworkQuestionBank.Templates.Count);
        Assert.Equal(60, NetworkQuestionBank.Templates.Select(t => t.Id).Distinct().Count());
        var context = Context();
        var random = new Random(42);
        string? previous = null;
        var seen = new HashSet<string>();
        for (var i = 0; i < 300; i++)
        {
            var question = NetworkQuestionBank.Next(context, previous, random)!;
            Assert.NotEqual(previous, question.Template.Id);
            Assert.DoesNotContain("{ssid}", question.Prompt);
            Assert.DoesNotContain("{adapter}", question.Prompt);
            Assert.InRange(question.Options.Count, 2, 8);
            seen.Add(question.Template.Id);
            previous = question.Template.Id;
        }
        Assert.True(seen.Count >= 55);
    }

    [Fact]
    public void NoAdapterStillAllowsGeneralQuestions()
    {
        var question = NetworkQuestionBank.Next(new(), null, new Random(1))!;
        Assert.Contains(question.Template.Kind, new[] { "priority", "recovery", "schedule" });
    }

    [Fact]
    public void ContradictoryStabilityAnswerIsDownweighted()
    {
        var context = Context(20);
        var q = Question("stable-01", context);
        var contradiction = NetworkQuestionBank.Evaluate(q, "yes", context);
        var consistent = NetworkQuestionBank.Evaluate(q, "no", context);
        Assert.True(contradiction.Reliability < consistent.Reliability);
        Assert.Contains("冲突", contradiction.Evidence);
        var changed = NetworkQuestionBank.Evaluate(q, "yes", context with { Links = Array.Empty<SurveyLink>() });
        Assert.True(changed.Reliability < .3);
        Assert.Throws<ArgumentException>(() => NetworkQuestionBank.Evaluate(q, "made-up", context));
    }

    [Fact]
    public void AnswersPreserveBothContextsAndCannotRepeatTrain()
    {
        var context = Context();
        var answerAt = context with { CapturedAt = context.CapturedAt.AddMinutes(2) };
        var question = Question("priority-01", context);
        var answer = NetworkQuestionBank.Evaluate(question, "stability", answerAt);
        var state = new NetworkHistory { Brain = NetworkBrain.Seed() };
        SurveyLearning.Record(state, answer);
        var steps = state.Brain.PersonalSteps;
        Assert.True(steps > 0);
        SurveyLearning.Record(state, answer);
        Assert.Single(state.Survey.Answers);
        Assert.Equal(steps, state.Brain.PersonalSteps);
        Assert.Equal(context.CapturedAt, state.Survey.Answers[0].Question.AskedContext.CapturedAt);
        Assert.Equal(answerAt.CapturedAt, state.Survey.Answers[0].AnswerContext.CapturedAt);
    }

    [Fact]
    public void SeedIsActualDistilledNeuralModelAndPrefersHealthyOnlineLink()
    {
        var model = NetworkBrain.Seed();
        Assert.True(NetworkBrain.Valid(model));
        Assert.True(model.TrainingSteps >= 240000);
        Assert.Contains("deepseek-v4-flash", model.Provenance);
        var good = new[] { .95, 0, .03, .01, .8, 1, 1, .5 };
        var bad = new[] { .2, .7, .8, .8, .95, 1, 0, .9 };
        Assert.True(NetworkBrain.Predict(model, good) > NetworkBrain.Predict(model, bad));
    }

    [Fact]
    public void ActionConfidenceDependsOnEvidenceRatherThanUserVotes()
    {
        var input = new GuardianInput
        {
            Now = TestData.Now, Config = TestData.Config(), GlobalProbe = TestData.OnlineProbe(),
            Radio = TestData.RadioOn, Devices = Array.Empty<ManagedDevice>(),
            Interfaces = Array.Empty<InterfaceRuntimeState>(), WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
        };
        var action = new ConnectWifiAction { InterfaceGuid = TestData.AdapterA, Ssid = "Synthetic", ProfileName = "Synthetic", Reason = "test" };
        var unknown = ActionConfidencePolicy.Assess(action, input);
        var measured = ActionConfidencePolicy.Assess(action, input with { LinkQuality = new[] { new LinkQualityEstimate
        { Adapter = TestData.AdapterA, Ssid = "Synthetic", Samples = 20, UpdatedAtUtc = TestData.Now } } });
        Assert.True(measured.Confidence > unknown.Confidence);
        input.Preferences.ExplicitChoices = 10000;
        Assert.Equal(unknown.Confidence, ActionConfidencePolicy.Assess(action, input).Confidence);
    }

    [Fact]
    public async Task DatasetAndModelSurviveRestartAndDeleteRetrains()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ng-survey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new NetworkHistoryStore(Path.Combine(directory, "history.json"));
            await store.LoadAsync(default);
            Assert.True(File.Exists(store.BrainPath));
            var question = Question("priority-01", Context());
            store.RecordAnswer(NetworkQuestionBank.Evaluate(question, "latency", Context()));
            await store.SaveAsync(default);
            var loaded = new NetworkHistoryStore(Path.Combine(directory, "history.json"));
            await loaded.LoadAsync(default);
            Assert.Single(loaded.Survey.Answers);
            Assert.True(loaded.Brain.PersonalSteps > 0);
            loaded.RemoveAnswer(question.Id);
            Assert.Empty(loaded.Survey.Answers);
            Assert.Equal(0, loaded.Brain.PersonalSteps);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static SurveyContext Context(double score = 90) => new()
    {
        CapturedAt = TestData.Now, InternetOnline = true,
        Links = new[] { new SurveyLink { Adapter = TestData.AdapterA, AdapterName = "Internal", Ssid = "Synthetic", Connected = true, Online = true,
            Signal = 80, Quality = new() { Adapter = TestData.AdapterA, Ssid = "Synthetic", Score = score, Samples = 20, UpdatedAtUtc = TestData.Now } },
            new SurveyLink { Adapter = TestData.AdapterB, AdapterName = "USB" } },
    };
    private static NetworkSurveyQuestion Question(string id, SurveyContext context)
    {
        var template = NetworkQuestionBank.Templates.Single(t => t.Id == id);
        return new(Guid.NewGuid().ToString("N"), template, template.Prompt, "Synthetic", TestData.AdapterA,
            template.Kind == "priority" ? new[] { new SurveyOption("stability", "稳定"), new SurveyOption("latency", "延迟") } :
                new[] { new SurveyOption("yes", "是"), new SurveyOption("no", "不是") }, context);
    }
}
