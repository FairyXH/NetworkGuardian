using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Infrastructure.Configuration;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class NetworkQualityTests
{
    [Fact]
    public void SevereGatewayLossOutweighsStrongSignal()
    {
        var history = new LinkQualityHistory { Adapter = TestData.AdapterA, Ssid = "Fast", LongTermScore = 30 };
        for (var i = 0; i < 20; i++) history.Samples.Add(new()
        {
            AtUtc = TestData.Now.AddSeconds(i), GatewaySent = 4, GatewayReplies = 1,
            TcpSent = 3, TcpReplies = 3, LatencyMs = 10,
        });
        var estimate = NetworkQualityPolicy.Estimate(history, TestData.Now.AddMinutes(1));
        Assert.True(estimate.Score < 40);
        Assert.Equal(.75, estimate.GatewayLoss);
    }

    [Fact]
    public void EchoFilteringDoesNotLookLikeBrokenHardware()
    {
        var history = new LinkQualityHistory { LongTermScore = 95 };
        history.Samples.Add(new() { AtUtc = TestData.Now, GatewaySent = 4, InternetSent = 2, TcpSent = 3, TcpReplies = 3, LatencyMs = 20 });
        var estimate = NetworkQualityPolicy.Estimate(history, TestData.Now);
        Assert.Null(estimate.GatewayLoss);
        Assert.Null(estimate.InternetLoss);
        Assert.True(estimate.Score > 90);
        Assert.Equal("待评估", estimate.Grade);
    }

    [Fact]
    public void ExpiredSamplesCannotDriveDecisions()
    {
        var old = Quality(TestData.AdapterA, "Fast", 5) with { UpdatedAtUtc = TestData.Now.AddDays(-8) };
        Assert.Equal(50, NetworkQualityPolicy.CombinationScore(TestData.AdapterA, "Fast", NetworkBand.Band5GHz, new[] { old }, TestData.Now));
    }

    [Fact]
    public void UncalibratedTcpTargetsAreUnknownRatherThanTotalPacketLoss()
    {
        var history = new LinkQualityHistory { LongTermScore = 95 };
        for (var i = 0; i < 10; i++) history.Samples.Add(new()
        { AtUtc = TestData.Now.AddSeconds(i), GatewaySent = 4, GatewayReplies = 4, TcpSent = 3, TcpReplies = 0 });
        var estimate = NetworkQualityPolicy.Estimate(history, TestData.Now.AddMinutes(1));
        Assert.Null(estimate.TcpFailure);
        Assert.Equal(0, estimate.GatewayLoss);
        Assert.True(estimate.Score > 85);
    }

    [Fact]
    public void GlobalPlanKeepsBestNetworkOnReliableAdapter()
    {
        var history = new[] { Quality(TestData.AdapterA, "Fast", 95), Quality(TestData.AdapterB, "Fast", 20),
            Quality(TestData.AdapterA, "Slow", 80), Quality(TestData.AdapterB, "Slow", 20) };
        var sets = new[] { TestData.AdapterA, TestData.AdapterB }.Select(guid => new AdapterCandidateSet(guid,
            new[] { Candidate(guid, "Fast", 100), Candidate(guid, "Slow", 60) }
                .Select(c => NetworkQualityPolicy.Adjust(c, history, TestData.Now)).ToArray())).ToArray();
        var plan = AdapterAssignmentPlanner.Plan(sets, false);
        Assert.Equal("Fast", plan.Single(p => p.InterfaceGuid == TestData.AdapterA).Candidate.Ssid);
        Assert.Equal("Slow", plan.Single(p => p.InterfaceGuid == TestData.AdapterB).Candidate.Ssid);
    }

    [Fact]
    public void GlobalPlanAvoidsGreedyStarvation()
    {
        var plan = AdapterAssignmentPlanner.Plan(new[]
        {
            new AdapterCandidateSet(TestData.AdapterA, new[] { Candidate(TestData.AdapterA, "Only", 100), Candidate(TestData.AdapterA, "Other", 99) }),
            new AdapterCandidateSet(TestData.AdapterB, new[] { Candidate(TestData.AdapterB, "Only", 90) }),
        }, false);
        Assert.Equal(2, plan.Count);
        Assert.Equal("Other", plan.Single(p => p.InterfaceGuid == TestData.AdapterA).Candidate.Ssid);
    }

    [Fact]
    public void RoutePriorityUsesQualityBeforeAdvertisedSpeed()
    {
        var good = TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe(), id: "good") with { QualityScore = 95, SpeedBitsPerSecond = 100_000_000 };
        var bad = TestData.WifiInterface(TestData.AdapterB, probe: TestData.OnlineProbe(), id: "bad") with { QualityScore = 20, SpeedBitsPerSecond = 1_000_000_000 };
        var metrics = InterfaceMetricPlanner.Plan(new[] { bad, good }, Array.Empty<WifiAdapterRuntimeState>());
        Assert.True(metrics["good"] < metrics["bad"]);
    }

    [Fact]
    public void HandoverRetainsSourceUntilFreshTargetProbeAndHonorsBinding()
    {
        var config = TestData.Config();
        var source = TestData.ConnectedAdapter(TestData.AdapterB, "Fast", "Fast") with
        { Connection = TestData.ConnectedAdapter(TestData.AdapterB, "Fast", "Fast").Connection! with { Band = NetworkBand.Band5GHz } };
        var target = TestData.ConnectedAdapter(TestData.AdapterA, "Slow", "Slow", scan:
            TestData.Scan(TestData.AdapterA, TestData.Network(TestData.AdapterA, "Fast", 90), TestData.Network(TestData.AdapterA, "Slow", 60)))
            with { SavedProfiles = new[] { "Fast", "Slow" } };
        var input = new GuardianInput
        {
            Config = config, Now = TestData.Now, GlobalProbe = TestData.OnlineProbe(), Radio = TestData.RadioOn,
            Devices = Array.Empty<ManagedDevice>(), WifiAdapters = new[] { source, target },
            Interfaces = new[] { TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe(), id: "a"),
                TestData.WifiInterface(TestData.AdapterB, probe: TestData.OnlineProbe(), id: "b") },
            LinkQuality = new[] { Quality(TestData.AdapterB, "Fast", 20), Quality(TestData.AdapterA, "Slow", 95) },
        };
        var engine = new GuardianDecisionEngine(config);
        var decision = engine.Evaluate(input);
        Assert.Contains(decision.Actions, a => a is ConnectWifiAction c && c.InterfaceGuid == TestData.AdapterA && c.Ssid == "Fast");
        Assert.DoesNotContain(decision.Actions, a => a is DisconnectWifiAction);
        var connectedTarget = target with { Connection = target.Connection! with { Ssid = "Fast" } };
        var pending = input with { Now = TestData.Now.AddSeconds(2), WifiAdapters = new[] { source, connectedTarget } };
        Assert.DoesNotContain(engine.Evaluate(pending).Actions, a => a is DisconnectWifiAction);
        var verified = pending with { Now = TestData.Now.AddSeconds(4), Interfaces = new[]
        {
            input.Interfaces[0] with { Probe = TestData.OnlineProbe() with { TimestampUtc = TestData.Now.AddSeconds(3) } }, input.Interfaces[1],
        } };
        Assert.Contains(engine.Evaluate(verified).Actions, a => a is DisconnectWifiAction d && d.InterfaceGuid == TestData.AdapterB);
        config.Wifi.CampusWifiAdapterAssignments["Fast"] = TestData.AdapterB.ToString();
        Assert.DoesNotContain(new GuardianDecisionEngine(config).Evaluate(input).Actions, a => a is ConnectWifiAction);
    }

    [Fact]
    public async Task HistorySurvivesRestartAndRecoversBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ng-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "history.json");
            var store = new NetworkHistoryStore(path);
            for (var i = 0; i < 25; i++) store.Record(TestData.AdapterA, "Home", NetworkBand.Band5GHz, "device", new()
            { AtUtc = DateTimeOffset.UtcNow.AddSeconds(-30 + i), GatewaySent = 4, GatewayReplies = 4, TcpSent = 3, TcpReplies = 3, LatencyMs = 10 });
            await store.SaveAsync(default);
            await store.SaveAsync(default);
            await File.WriteAllTextAsync(path, "broken");
            var restored = new NetworkHistoryStore(path);
            await restored.LoadAsync(default);
            Assert.Single(restored.Snapshot);
            Assert.Equal(25, restored.Snapshot[0].Samples);
            Assert.True(restored.Snapshot[0].Score > 85);
            Assert.NotNull(restored.LoadIssue);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static LinkQualityEstimate Quality(Guid adapter, string ssid, double score) => new()
    { Adapter = adapter, Ssid = ssid, Band = NetworkBand.Band5GHz, Score = score, Samples = 20, UpdatedAtUtc = TestData.Now };
    private static WifiCandidate Candidate(Guid adapter, string ssid, double score) => new()
    { InterfaceGuid = adapter, Ssid = ssid, ProfileName = ssid, Score = score, Band = NetworkBand.Band5GHz };
}
