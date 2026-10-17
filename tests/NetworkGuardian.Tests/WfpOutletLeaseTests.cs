using System.Net;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Network;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class WfpOutletLeaseTests
{
    [Fact]
    public void FamilySpecificActiveOutletIsNeverBlockedAndOwnershipIsRevalidated()
    {
        var old = TestData.EthernetInterface() with { DeviceInstanceId = "old-device", InterfaceIndex = 1,
            Ipv6Addresses = new[] { "2001:db8::1", "fe80::1%3" } };
        var next = TestData.WifiInterface(TestData.AdapterA) with { IsPhysicalDevice = true, InterfaceIndex = 2 };
        var routes = new[]
        {
            new DefaultRouteInfo { AddressFamily = 2, InterfaceIndex = next.InterfaceIndex, NextHop = "10.20.30.1" },
            new DefaultRouteInfo { AddressFamily = 23, InterfaceIndex = old.InterfaceIndex, NextHop = "fe80::1" },
        };
        var requested = old.Ipv4Addresses.Concat(old.Ipv6Addresses).ToArray();
        var targets = WfpOutletLease.SelectTargets("old-device", requested, new[] { old, next }, routes);
        Assert.Equal(old.Ipv4Addresses.Select(IPAddress.Parse), targets);
        Assert.Empty(WfpOutletLease.SelectTargets("old-device", requested, new[] { old, next },
            new[] { routes[0] with { InterfaceIndex = old.InterfaceIndex } }));
        Assert.Empty(WfpOutletLease.SelectTargets("old-device", requested,
            new[] { old with { IsPhysicalDevice = false }, next }, routes));
        Assert.Empty(WfpOutletLease.SelectTargets("unrelated", requested, new[] { old, next }, routes));
        Assert.Empty(WfpOutletLease.SelectTargets("old-device", new[] { "192.0.2.99" }, new[] { old, next }, routes));
    }

    [Theory]
    [InlineData(4, 1, true, false, true, null)]
    [InlineData(5, 0, true, false, true, "observed old endpoints released")]
    [InlineData(6, 1, true, false, true, null)]
    [InlineData(60, 1, true, false, true, "maximum lease reached")]
    [InlineData(1, 1, false, false, true, "owner exited")]
    [InlineData(1, 1, true, true, true, "cancelled")]
    [InlineData(1, 1, true, false, false, "outlet/address changed")]
    public void LeaseExtendsOnlyWithinItsBoundAndReleasesImmediatelyWhenUnnecessary(
        int seconds, int endpoints, bool ownerAlive, bool cancelled, bool superseded, string? expected) =>
        Assert.Equal(expected, WfpOutletLease.ReleaseReason(TimeSpan.FromSeconds(seconds), 5, 60,
            ownerAlive, cancelled, superseded, endpoints));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1%3")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:192.0.2.1")]
    public void AmbiguousOrNonUnicastAddressesAreExcluded(string value) =>
        Assert.False(WfpOutletLease.IsEligibleAddress(value, out _));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndpointInventoryTracksExplicitBindsAndTheirRelease(bool ipv6)
    {
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var socket = new System.Net.Sockets.UdpClient(new IPEndPoint(address, 0));
        var port = (uint)(ushort)IPAddress.HostToNetworkOrder((short)((IPEndPoint)socket.Client.LocalEndPoint!).Port);
        var found = OutletEndpointTracker.Read(new[] { address });
        var endpoint = Assert.Single(found, e => e.Protocol == 17 && e.Port == port && e.Pid == (uint)Environment.ProcessId);
        socket.Dispose();
        Assert.DoesNotContain(endpoint, OutletEndpointTracker.Read(new[] { address }));
    }
}
