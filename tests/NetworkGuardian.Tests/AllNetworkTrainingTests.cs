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
}
