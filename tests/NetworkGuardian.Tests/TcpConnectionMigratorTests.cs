using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Windows.Network;
using Xunit;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetworkGuardian.Core.Helper;
using NetworkGuardian.Windows.Devices;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Tests;

public sealed class TcpConnectionMigratorTests
{
    [Fact]
    public void CleanupRechecksPhysicalOwnershipAndRejectsAnOutletThatBecameActiveAgain()
    {
        var ethernet = TestData.EthernetInterface();
        var wifi = TestData.WifiInterface(TestData.AdapterA) with { IsPhysicalDevice = true };
        var route = new DefaultRouteInfo { AddressFamily = 2, InterfaceIndex = 2,
            NextHop = "10.20.30.1", InterfaceLuid = 2001 };
        var oldAddresses = ethernet.Ipv4Addresses.ToArray();
        Assert.True(TcpConnectionMigrator.AreSupersededPhysicalAddresses(oldAddresses, new[] { ethernet, wifi }, route));
        Assert.False(TcpConnectionMigrator.AreSupersededPhysicalAddresses(oldAddresses, new[] { ethernet, wifi },
            route with { InterfaceLuid = 1001 }));
        Assert.False(TcpConnectionMigrator.AreSupersededPhysicalAddresses(oldAddresses,
            new[] { ethernet with { IsPhysicalDevice = false }, wifi }, route));
        Assert.False(TcpConnectionMigrator.AreSupersededPhysicalAddresses(oldAddresses, new[] { ethernet, wifi }, null));
    }

    [Theory]
    [InlineData(5u, 0u, false)]
    [InlineData(11u, 42u, false)]
    [InlineData(12u, 42u, false)]
    [InlineData(2u, 42u, false)]
    [InlineData(5u, 42u, true)]
    [InlineData(8u, 42u, true)]
    public void CleanupExcludesUnownedAndCompletedConnections(uint state, uint pid, bool expected) =>
        Assert.Equal(expected, TcpConnectionMigrator.IsLiveOwnedConnection(state, pid));

    [Fact]
    public void ElevatedCleanupContractRoundTripsWithAotMetadata()
    {
        var original = new HelperRequest { Operation = HelperOperations.CloseStaleTcp,
            OldLocalAddresses = new[] { "192.0.2.10" },
            ConnectionMigration = new() { Mode = ConnectionCutMode.AllowList, ProcessPatterns = new() { "test.exe" } } };
        var parsed = HelperProtocol.DeserializeRequest(HelperProtocol.SerializeRequest(original));
        Assert.NotNull(parsed);
        Assert.Equal(original.OldLocalAddresses, parsed.OldLocalAddresses);
        Assert.Equal(ConnectionCutMode.AllowList, parsed.ConnectionMigration!.Mode);
        Assert.Equal("test.exe", Assert.Single(parsed.ConnectionMigration.ProcessPatterns));
        var response = HelperProtocol.DeserializeResponse(HelperProtocol.SerializeResponse(new() { ClosedConnections = 2 }));
        Assert.Equal(2, response!.ClosedConnections);
    }

    [ElevatedFact]
    public async Task NativeDeleteClosesOnlyTheTestOwnedLoopbackConnection()
    {
        Assert.Equal(24, Marshal.SizeOf<TcpConnectionMigrator.TcpOwnerPidRow>());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        var endpoint = (IPEndPoint)client.Client.LocalEndPoint!;
        var port = unchecked((ushort)IPAddress.HostToNetworkOrder((short)endpoint.Port));
        var row = Assert.Single(TcpConnectionMigrator.ReadRows(), r =>
            r.ProcessId == (uint)Environment.ProcessId && r.LocalPort == port && r.State == 5);
        Assert.Equal(0u, TcpConnectionMigrator.DeleteConnection(row));
        Assert.DoesNotContain(TcpConnectionMigrator.ReadRows(), r =>
            r.ProcessId == row.ProcessId && r.LocalPort == row.LocalPort && r.RemotePort == row.RemotePort && r.State == 5);
    }

    private sealed class ElevatedFactAttribute : FactAttribute
    {
        public ElevatedFactAttribute()
        {
            if (!HelperClient.IsProcessElevated()) Skip = "Native TCP deletion requires an elevated test process.";
        }
    }

    [Theory]
    [InlineData("yysls.exe", @"D:\Games\yysls.exe", "yysls.exe", true)]
    [InlineData("helper.exe", @"C:\Apps\baidu\helper.exe", @"baidu\*.exe", true)]
    [InlineData("helper.dll", @"C:\Apps\baidu\helper.dll", @"baidu\*.exe", false)]
    [InlineData("helper.exe", @"C:\Apps\other\helper.exe", @"baidu\*.exe", false)]
    public void GlobMatchesNameAndPath(string name, string path, string pattern, bool expected)
    {
        Assert.Equal(expected, TcpConnectionMigrator.GlobMatches(name, path, pattern));
    }

    [Fact]
    public void AllowAndDenyModesInvertTheSamePatternList()
    {
        var patterns = new[] { "yysls.exe" };

        Assert.True(TcpConnectionMigrator.ShouldClose(
            "yysls.exe", null, ConnectionCutMode.AllowList, patterns));
        Assert.False(TcpConnectionMigrator.ShouldClose(
            "browser.exe", null, ConnectionCutMode.AllowList, patterns));
        Assert.False(TcpConnectionMigrator.ShouldClose(
            "yysls.exe", null, ConnectionCutMode.DenyList, patterns));
        Assert.True(TcpConnectionMigrator.ShouldClose(
            "browser.exe", null, ConnectionCutMode.DenyList, patterns));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("192.168.181.168", true)]
    [InlineData("10.0.0.189", true)]
    public void OnlyRealIpv4InterfaceAddressesAreEligible(string value, bool expected)
    {
        Assert.Equal(expected, TcpConnectionMigrator.IsEligibleOldAddress(value, out _));
    }
}
