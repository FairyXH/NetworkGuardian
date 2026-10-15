using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Infrastructure.Configuration;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class PreferenceLearningTests
{
    [Fact]
    public void RepeatedChoicesLearnNetworkAndAdapterInContext()
    {
        var state = new PreferenceState();
        var a = Option(TestData.AdapterA, "Home");
        var b = Option(TestData.AdapterB, "Campus");
        for (var i = 0; i < 15; i++) PreferencePolicy.Train(state, new()
        { AtUtc = TestData.Now.AddDays(i), Chosen = a, Alternatives = new[] { a, b } });
        Assert.True(PreferencePolicy.Predict(state, a, TestData.Now.AddDays(15)) > PreferencePolicy.Predict(state, b, TestData.Now.AddDays(15)));
        Assert.True(PreferencePolicy.Bonus(state, a, TestData.Now.AddDays(15)) > 0);
        Assert.InRange(PreferencePolicy.Bonus(state, a, TestData.Now.AddDays(15)), -4, 4);
    }

    [Fact]
    public void ColdStartAndBadQualityNeverGetLearnedBonus()
    {
        var a = Option(TestData.AdapterA, "Home");
        var state = new PreferenceState();
        Assert.Equal(0, PreferencePolicy.Bonus(state, a, TestData.Now));
        state.ExplicitChoices = 100;
        Assert.Equal(0, PreferencePolicy.Bonus(state, a with { Quality = 20 }, TestData.Now));
        Assert.Equal(0, PreferencePolicy.Bonus(state, a with { Confidence = 0 }, TestData.Now));
    }

    [Fact]
    public void ImplicitChoicesDoNotUnlockAutomaticLearning()
    {
        var state = new PreferenceState();
        var a = Option(TestData.AdapterA, "Home");
        var b = Option(TestData.AdapterB, "Campus");
        for (var i = 0; i < 30; i++) PreferencePolicy.Train(state, new()
        { AtUtc = TestData.Now.AddMinutes(i), Chosen = a, Source = "manual", Alternatives = new[] { b } });
        Assert.Equal(0, state.ExplicitChoices);
        Assert.Equal(0, PreferencePolicy.Bonus(state, a, TestData.Now.AddHours(1)));
    }

    [Fact]
    public void QuestionsHonorDailyAndContextCooldowns()
    {
        var state = new PreferenceState { LastQuestionUtc = TestData.Now };
        state.QuestionContexts["same"] = TestData.Now;
        Assert.False(PreferencePolicy.CanAsk(state, "different", TestData.Now.AddHours(23)));
        Assert.True(PreferencePolicy.CanAsk(state, "different", TestData.Now.AddDays(1)));
        Assert.False(PreferencePolicy.CanAsk(state, "same", TestData.Now.AddDays(6)));
        Assert.True(PreferencePolicy.CanAsk(state, "same", TestData.Now.AddDays(7)));
    }

    [Fact]
    public async Task LearnedDataSurvivesRestartAndCanBeDeleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ng-preference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "history.json");
            var store = new NetworkHistoryStore(path);
            var a = Option(TestData.AdapterA, "Home");
            var b = Option(TestData.AdapterB, "Campus");
            store.RecordPreference(new() { AtUtc = TestData.Now, Chosen = a, Alternatives = new[] { b } });
            store.MarkQuestion("context", TestData.Now);
            await store.SaveAsync(default);
            var loaded = new NetworkHistoryStore(path);
            await loaded.LoadAsync(default);
            Assert.Single(loaded.Preferences.Feedback);
            Assert.Equal(1, loaded.Preferences.ExplicitChoices);
            Assert.False(PreferencePolicy.CanAsk(loaded.Preferences, "context", TestData.Now.AddDays(1)));
            loaded.RemovePreference(TestData.Now);
            Assert.Empty(loaded.Preferences.Feedback);
            Assert.Empty(loaded.Preferences.Weights);
            loaded.ResetPreferences();
            await loaded.SaveAsync(default);
            var reset = new NetworkHistoryStore(path);
            await reset.LoadAsync(default);
            Assert.Empty(reset.Preferences.Feedback);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static PreferenceOption Option(Guid adapter, string ssid) => new()
    { Candidate = new() { InterfaceGuid = adapter, Ssid = ssid, ProfileName = ssid, SignalQuality = 70 }, Quality = 85, Confidence = 1 };
}
