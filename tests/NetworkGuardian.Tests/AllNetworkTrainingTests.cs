using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Serialization;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class AllNetworkTrainingTests
{
    [Theory]
    [InlineData(InterfaceKind.Ethernet)]
    [InlineData(InterfaceKind.Wifi)]
    [InlineData(InterfaceKind.Tunnel)]
    [InlineData(InterfaceKind.Virtual)]
    [InlineData(InterfaceKind.Other)]
    [InlineData(InterfaceKind.Unknown)]
    public void EveryRoutableKindProducesTraceableTrainingDataWithoutFakeSignal(InterfaceKind kind)
    {
        var iface = TestData.EthernetInterface(probe: TestData.OnlineProbe()) with
        { Kind = kind, AdapterGuid = TestData.AdapterA };
        var wifi = kind == InterfaceKind.Wifi ? TestData.ConnectedAdapter(TestData.AdapterA, "Home", "Home", 74) : null;
        if (wifi is not null) iface = iface with { WlanInterfaceGuid = TestData.AdapterA };
        var identity = NetworkQualityIdentity.Create(iface, wifi)!;
        var quality = new LinkQualityEstimate { Kind = kind, Adapter = identity.Adapter, Ssid = identity.Ssid,
            Band = identity.Band, NetworkId = identity.NetworkId, Samples = 8, Score = 90,
            TcpFailure = 0, LatencyMs = 20, UpdatedAtUtc = TestData.Now };
        var snapshot = GuardianSnapshot.Initial(TestData.Config(), TestData.Now) with
        { Interfaces = new[] { iface }, WifiAdapters = wifi is null ? Array.Empty<WifiAdapterRuntimeState>() : new[] { wifi } };
        var row = Assert.Single(NetworkQualityObservations.Capture(snapshot, new[] { quality }, TestData.Now));
        Assert.Equal(kind, row.NetworkKind);
        Assert.Equal(identity.NetworkId, row.NetworkId);
        Assert.Equal(kind == InterfaceKind.Wifi ? .74 : .5, row.Features[4]);
        Assert.Null(row.Target);
        var parsed = NetworkGuardianJson.Deserialize<DistillationExample[]>(NetworkGuardianJson.Serialize(new[] { row }))!;
        Assert.Equal(kind, parsed[0].NetworkKind);
        Assert.Equal(8, parsed[0].Features.Length);
        Assert.Empty(NetworkQualityObservations.Capture(snapshot, new[] { quality with { NetworkId = "old-network" } }, TestData.Now));
        Assert.Empty(NetworkQualityObservations.Capture(snapshot, new[] { quality with { UpdatedAtUtc = TestData.Now.AddMinutes(-6) } }, TestData.Now));
    }

    [Theory]
    [InlineData(InterfaceKind.Ethernet)]
    [InlineData(InterfaceKind.Tunnel)]
    [InlineData(InterfaceKind.Virtual)]
    [InlineData(InterfaceKind.Other)]
    public void RealPriorityAnswersCanUpdateTheModelForNonWifiLinks(InterfaceKind kind)
    {
        var link = new SurveyLink { Kind = kind, Adapter = TestData.AdapterA, NetworkId = "network-a",
            NetworkName = "当前有线或隧道网络", Connected = true, Online = true, Signal = 0,
            Quality = new() { Samples = 20, UpdatedAtUtc = TestData.Now, Score = 90, TcpFailure = 0, LatencyMs = 20 } };
        var context = new SurveyContext { CapturedAt = TestData.Now, Links = new[] { link } };
        var question = new NetworkSurveyQuestion("test-priority", new("priority-01", "priority", "优先什么"),
            "优先什么", "", TestData.AdapterA, new[] { new SurveyOption("latency", "低延迟") }, context, link.NetworkId);
        var answer = NetworkQuestionBank.Evaluate(question, "latency", context);
        var state = new NetworkHistory { Brain = NetworkBrain.Seed() };
        var before = state.Brain.PersonalSteps;
        SurveyLearning.Record(state, answer);
        Assert.Equal(before + 1, state.Brain.PersonalSteps);
        Assert.True(NetworkBrain.Valid(state.Brain));
        var moved = NetworkQuestionBank.Evaluate(question, "latency", context with { Links = new[] { link with { NetworkId = "network-b" } } });
        Assert.True(moved.Reliability < answer.Reliability);
    }

    [Fact]
    public void WiredRankingUsesMeasuredQualityInsteadOfAdvertisedSpeed()
    {
        var good = TestData.EthernetInterface(probe: TestData.OnlineProbe(), id: "slow-good") with
        { QualityScore = 95, SpeedBitsPerSecond = 100_000_000 };
        var bad = TestData.EthernetInterface(probe: TestData.OnlineProbe(), id: "fast-bad") with
        { QualityScore = 30, SpeedBitsPerSecond = 1_000_000_000 };
        var plan = InterfaceMetricPlanner.Plan(new[] { bad, good }, Array.Empty<WifiAdapterRuntimeState>());
        Assert.True(plan[good.Id] < plan[bad.Id]);
    }
}
