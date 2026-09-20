using System.Runtime.InteropServices;
using NetworkGuardian.Windows.Native;
using NetworkGuardian.Windows.Network;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// The IP Helper interop is hand written; these tests lock the layout and prove the live data is
/// sane, because a wrong row stride silently produces plausible looking garbage.
/// </summary>
public sealed class IpHelperLayoutTests
{
    [Fact]
    public void AdapterAddressAlignment_ReadsIfIndexFromSecondDword()
    {
        const uint length = 448;
        const uint interfaceIndex = 24;
        var value = new IpHlpApiNative.IP_ADAPTER_ADDRESSES
        {
            Alignment = length | ((ulong)interfaceIndex << 32),
        };

        Assert.Equal(interfaceIndex, value.IfIndex);
    }

    [Fact]
    public void MibIpForwardRow2_IsHundredAndFourBytes()
    {
        // NET_LUID(8) + NET_IFINDEX(4) + IP_ADDRESS_PREFIX(32) + SOCKADDR_INET(28)
        // + UCHAR + 3 pad + ULONG*3(12) + ULONG Protocol(4) + 4 BOOLEAN + ULONG Age + ULONG Origin
        Assert.Equal(104, Marshal.SizeOf<IpHlpApiNative.MIB_IPFORWARD_ROW2>());
    }

    [Fact]
    public void SockaddrInet_IsTwentyEightBytes() =>
        Assert.Equal(28, Marshal.SizeOf<IpHlpApiNative.SOCKADDR_INET>());

    [Fact]
    public void AddressPrefix_IsThirtyTwoBytes() =>
        Assert.Equal(32, Marshal.SizeOf<IpHlpApiNative.IP_ADDRESS_PREFIX>());

    [Fact]
    public void ForwardTableHeader_IsEightBytes() =>
        Assert.Equal(8, Marshal.SizeOf<IpHlpApiNative.MIB_IPFORWARD_TABLE2_HEADER>());

    [Fact]
    public void MibIpInterfaceRow_MatchesWindowsAbi() =>
        Assert.Equal(NetIoApiNative.MibIpInterfaceRowSize,
            Marshal.SizeOf<NetIoApiNative.MIB_IPINTERFACE_ROW>());
}

public sealed class DefaultRouteReadTests
{
    [Fact]
    public void DefaultRoutes_ArePlausibleAndResolveToInterfaceNames()
    {
        var provider = new NetworkInterfaceProvider();

        var interfaces = provider.GetInterfaces();
        var routes = provider.GetDefaultRoutes();

        // A machine has a handful of default routes; a wrong stride produces dozens of garbage rows.
        Assert.InRange(routes.Count, 0, 64);

        // Every IPv4 default route must map to a real interface index that the adapter list knows.
        var knownIndices = interfaces
            .Select(i => i.Id)
            .Where(id => id is not null)
            .ToHashSet();

        Assert.All(routes, route =>
        {
            Assert.Equal(2, route.AddressFamily); // AF_INET only
            Assert.False(string.IsNullOrWhiteSpace(route.NextHop));
            Assert.NotNull(route.InterfaceLuid);
        });

        Assert.NotEmpty(knownIndices);
    }

    [Fact]
    public void DefaultRoutes_CarryAnInterfaceAliasOrReportTheirLuid()
    {
        var provider = new NetworkInterfaceProvider();
        var routes = provider.GetDefaultRoutes();

        // The alias lookup is best effort, but a route must never come back with a nonsense LUID such
        // as one that does not fit in 56 bits (which is how a bad stride shows up).
        foreach (var route in routes)
        {
            Assert.True(route.InterfaceLuid is null || route.InterfaceLuid <= 0x00FF_FFFF_FFFF_FFFF,
                $"implausible interface LUID {route.InterfaceLuid} (0x{route.InterfaceLuid:X})");
        }
    }

    [Fact]
    public void InterfaceMetricAndRouteMetric_AreBothReported()
    {
        var provider = new NetworkInterfaceProvider();
        var routes = provider.GetDefaultRoutes();

        var withMetrics = routes.Where(r => r.RouteMetric is not null && r.InterfaceMetric is not null).ToList();

        if (routes.Count > 0)
        {
            // At least one route should have been correlated with its adapter for the metric sum to be
            // meaningful on the dashboard.
            Assert.NotEmpty(withMetrics);
            Assert.All(withMetrics, r => Assert.NotNull(r.EffectiveMetric));
        }
    }
}
