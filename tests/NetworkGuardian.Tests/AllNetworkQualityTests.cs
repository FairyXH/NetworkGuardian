using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Serialization;
using NetworkGuardian.Infrastructure.Configuration;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class AllNetworkQualityTests
{
    [Fact]
    public void RawWifiQueriesMayOmitBandButCannotSubstituteAnotherAssociation()
    {
        var adapter = TestData.ConnectedAdapter(TestData.AdapterA, "Home", "Home") with
        { Connection = TestData.ConnectedAdapter(TestData.AdapterA, "Home", "Home").Connection! with
            { Band = NetworkBand.Band5GHz, Bssid = "00:11:22:33:44:55" } };
        var raw = adapter.Connection with { Band = NetworkBand.Unknown };
        Assert.True(NetworkQualityIdentity.SameWifiAssociation(adapter, raw));
        Assert.False(NetworkQualityIdentity.SameWifiAssociation(adapter, raw with { Bssid = "00:11:22:33:44:56" }));
        Assert.False(NetworkQualityIdentity.SameWifiAssociation(adapter, raw with { Ssid = "Other" }));
        Assert.False(NetworkQualityIdentity.SameWifiAssociation(adapter, raw with { Band = NetworkBand.Band2_4GHz }));
        Assert.False(NetworkQualityIdentity.SameWifiAssociation(adapter, null));
    }
    [Theory]
    [InlineData(InterfaceKind.Ethernet)]
    [InlineData(InterfaceKind.Wifi)]
    [InlineData(InterfaceKind.Tunnel)]
    [InlineData(InterfaceKind.Virtual)]
    [InlineData(InterfaceKind.Other)]
    [InlineData(InterfaceKind.Unknown)]
    public void EveryRoutableKindUsesMeasuredHistoryAndKeepsNetworksSeparate(InterfaceKind kind)
    {
        var iface = TestData.EthernetInterface() with { Kind = kind, AdapterGuid = TestData.AdapterA };
        var identity = NetworkQualityIdentity.Create(iface)!;
        Assert.Null(NetworkQualityIdentity.UnavailableReason(iface));
        var store = new NetworkHistoryStore("unused-history.json");
        for (var i = 0; i < 6; i++) store.Record(identity, new()
        { AtUtc = TestData.Now.AddSeconds(i), TcpSent = 3, TcpReplies = 3, LatencyMs = 20 });
        var quality = Assert.Single(store.Snapshot);
        Assert.True(identity.Matches(quality));
        Assert.Equal(kind, quality.Kind);
        Assert.True(quality.IsMeasured);
        Assert.True(quality.Score > 80);
        var moved = NetworkQualityIdentity.Create(iface with { Ipv4Gateways = new[] { "10.10.10.2" } })!;
        Assert.False(moved.Matches(quality));
        Assert.Null(moved.Find(store.Snapshot));
    }

    [Fact]
    public void LegacyWifiRecordsStillMatchTheirCurrentSsidAndBand()
    {
        var legacy = NetworkGuardianJson.Deserialize<LinkQualityEstimate>(
            """{"adapter":"aaaaaaaa-1111-2222-3333-444444444444","ssid":"Home","band":2,"score":90}""")!;
        var wifi = TestData.ConnectedAdapter(TestData.AdapterA, "Home", "Home") with
        { Connection = TestData.ConnectedAdapter(TestData.AdapterA, "Home", "Home").Connection! with { Band = NetworkBand.Band5GHz } };
        var identity = NetworkQualityIdentity.Create(TestData.WifiInterface(TestData.AdapterA), wifi)!;
        Assert.Equal(identity.Adapter, legacy.Adapter);
        Assert.Equal(identity.Kind, legacy.Kind);
        Assert.Equal(identity.Ssid, legacy.Ssid);
        Assert.Equal(identity.Band, legacy.Band);
        Assert.Equal(identity.NetworkId, legacy.NetworkId);
        Assert.True(identity.Matches(legacy));
        Assert.False((identity with { Ssid = "Other" }).Matches(legacy));
    }

    [Fact]
    public void UnsupportedScopesHaveExplicitReasonsAndIpv6RoutedTunnelsCanBeSampled()
    {
        Assert.Contains("回环", NetworkQualityIdentity.UnavailableReason(TestData.EthernetInterface() with { Kind = InterfaceKind.Loopback }));
        Assert.Contains("未连接", NetworkQualityIdentity.UnavailableReason(TestData.EthernetInterface(up: false)));
        Assert.Contains("仅局域网", NetworkQualityIdentity.UnavailableReason(TestData.EthernetInterface(hasGateway: false)));
        var tunnel = TestData.EthernetInterface(hasAddress: false, hasGateway: false) with
        { Kind = InterfaceKind.Tunnel, IsDefaultRoute = true, Ipv6Addresses = new[] { "2001:db8::1" } };
        Assert.Null(NetworkQualityIdentity.UnavailableReason(tunnel));
        Assert.Equal("2001:db8::1", NetworkQualityIdentity.Source(tunnel)!.ToString());
        var splitVpn = TestData.EthernetInterface(hasGateway: false) with
        { Kind = InterfaceKind.Virtual, HasIpv4InternetRoute = true };
        Assert.Null(NetworkQualityIdentity.UnavailableReason(splitVpn));
        var ipv6OnlyRoute = splitVpn with { HasIpv4InternetRoute = false, HasIpv6InternetRoute = true,
            Ipv6Addresses = new[] { "2001:db8::1" } };
        Assert.Equal("2001:db8::1", NetworkQualityIdentity.Source(ipv6OnlyRoute)!.ToString());
    }

    [Fact]
    public void VerifiedOfflineSamplesArePoorEvenWithoutPriorTcpSuccess()
    {
        var history = new LinkQualityHistory { Kind = InterfaceKind.Ethernet };
        for (var i = 0; i < 6; i++) history.Samples.Add(new()
        { AtUtc = TestData.Now.AddSeconds(i), TcpSent = 3, InternetOnline = false });
        var quality = NetworkQualityPolicy.Estimate(history, TestData.Now.AddMinutes(1));
        Assert.Equal(1d, quality.TcpFailure);
        Assert.Equal("较差", quality.Grade);
        Assert.Null(quality.GatewayLoss);
    }

    [Theory]
    [InlineData(InterfaceKind.Ethernet)]
    [InlineData(InterfaceKind.Tunnel)]
    [InlineData(InterfaceKind.Virtual)]
    [InlineData(InterfaceKind.Other)]
    [InlineData(InterfaceKind.Unknown)]
    public void ReportsGradeEveryMeasuredNetworkKindWithoutCallingItWifi(InterfaceKind kind)
    {
        var iface = TestData.EthernetInterface() with { Kind = kind, AdapterGuid = TestData.AdapterA };
        var identity = NetworkQualityIdentity.Create(iface)!;
        var quality = new LinkQualityEstimate { Adapter = identity.Adapter, Kind = kind,
            NetworkId = identity.NetworkId, Ssid = "", Samples = 8, UpdatedAtUtc = TestData.Now,
            Score = 95, TcpFailure = 0, LatencyMs = 17 };
        var snapshot = GuardianSnapshot.Initial(TestData.Config(), TestData.Now) with
        { GlobalProbe = TestData.OnlineProbe(), Interfaces = new[] { iface } };
        var report = NetworkQualityReporter.Build(snapshot, new[] { quality }, TestData.Config(), NetworkBrain.Seed(), TestData.Now);
        Assert.Contains(report.Findings, text => text.Contains("优秀（95 分）", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, text => text.Contains("没有与 Wi-Fi", StringComparison.Ordinal));
    }
}
