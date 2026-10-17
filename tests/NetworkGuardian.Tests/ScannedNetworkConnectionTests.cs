using NetworkGuardian.Core.Models;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class ScannedNetworkConnectionTests
{
    private static readonly Guid Adapter = Guid.NewGuid();

    [Fact]
    public void SwitchingNetworksOverridesBothScanTimeFlags()
    {
        var previous = Network("HXXY-WiFi", true);
        var current = Network("ZhangAndroid", false);
        var connection = Connection("ZhangAndroid");

        Assert.False(previous.MatchesConnection(connection));
        Assert.True(current.MatchesConnection(connection));
        Assert.False(current.MatchesConnection(null));
        Assert.False(current.MatchesConnection(connection with { State = WifiConnectionState.Disconnected }));
    }

    [Fact]
    public void AssociationMustMatchAdapterAndCaseSensitiveSsid()
    {
        var network = Network("ZhangAndroid", true);

        Assert.False(network.MatchesConnection(Connection("ZhangAndroid") with { InterfaceGuid = Guid.NewGuid() }));
        Assert.False(network.MatchesConnection(Connection("zhangandroid")));
        Assert.True(network.MatchesConnection(Connection("ZhangAndroid")));
    }

    private static ScannedNetwork Network(string ssid, bool scanTimeCurrent) => new()
    {
        InterfaceGuid = Adapter,
        Ssid = ssid,
        IsCurrentConnection = scanTimeCurrent,
        ObservedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
    };

    private static WifiConnectionInfo Connection(string ssid) => new()
    {
        InterfaceGuid = Adapter,
        State = WifiConnectionState.Connected,
        Ssid = ssid,
    };
}
