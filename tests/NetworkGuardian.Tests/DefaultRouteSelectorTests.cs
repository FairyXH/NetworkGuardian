using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class DefaultRouteSelectorTests
{
    [Fact]
    public void Order_SelectsOnlyLowestEffectiveMetricAsFirstRoute()
    {
        var wifi = Route(index: 22, luid: 220, routeMetric: 1, interfaceMetric: 12);
        var ethernet = Route(index: 15, luid: 150, routeMetric: 1, interfaceMetric: 10);

        var ordered = DefaultRouteSelector.Order(new[] { wifi, ethernet });

        Assert.Same(ethernet, ordered[0]);
        Assert.Same(wifi, ordered[1]);
    }

    [Fact]
    public void MatchesInterface_UsesIndexWhenRouteHasNoLuid()
    {
        var route = Route(index: 22, luid: null, routeMetric: 1, interfaceMetric: 12);
        var wifi = TestData.WifiInterface(TestData.AdapterA) with { InterfaceIndex = 22 };

        Assert.True(DefaultRouteSelector.MatchesInterface(route, wifi));
    }

    [Fact]
    public void FindInterface_ReturnsActualBestRouteOwner()
    {
        var ethernet = TestData.EthernetInterface() with { Id = "luid:150", InterfaceIndex = 15 };
        var wifi = TestData.WifiInterface(TestData.AdapterA) with { Id = "luid:220", InterfaceIndex = 22 };
        var best = Route(index: 15, luid: 150, routeMetric: 1, interfaceMetric: 10);

        Assert.Same(ethernet, DefaultRouteSelector.FindInterface(best, new[] { wifi, ethernet }));
    }

    private static DefaultRouteInfo Route(
        uint index,
        ulong? luid,
        int routeMetric,
        int interfaceMetric) => new()
    {
        AddressFamily = 2,
        InterfaceIndex = index,
        InterfaceLuid = luid,
        NextHop = "10.0.0.1",
        RouteMetric = routeMetric,
        InterfaceMetric = interfaceMetric,
    };
}
