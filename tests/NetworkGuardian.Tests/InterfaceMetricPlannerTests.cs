using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class InterfaceMetricPlannerTests
{
    [Fact]
    public void OnlineEthernetLeadsWifiAndFasterAdaptersLeadTheirClass()
    {
        var fastEthernet = TestData.EthernetInterface(
            probe: TestData.OnlineProbe(), id: "luid:1001") with
        {
            Name = "Ethernet 10G",
            SpeedBitsPerSecond = 10_000_000_000,
        };
        var slowEthernet = TestData.EthernetInterface(
            probe: TestData.OnlineProbe(), id: "luid:1002") with
        {
            Name = "Ethernet 1G",
            SpeedBitsPerSecond = 1_000_000_000,
        };
        var wifi = TestData.WifiInterface(
            TestData.AdapterA, probe: TestData.OnlineProbe(), id: "luid:2001") with
        {
            SpeedBitsPerSecond = 2_400_000_000,
        };

        var metrics = InterfaceMetricPlanner.Plan(
            new[] { slowEthernet, wifi, fastEthernet },
            new[] { TestData.ConnectedAdapter(TestData.AdapterA, "WiFi", "WiFi", 100) });

        Assert.Equal(10, metrics[fastEthernet.Id]);
        Assert.Equal(12, metrics[slowEthernet.Id]);
        Assert.Equal(14, metrics[wifi.Id]);
    }

    [Fact]
    public void WifiLeadsWhenEthernetHasNoInternet()
    {
        var ethernet = TestData.EthernetInterface(probe: TestData.OfflineProbe()) with
        {
            SpeedBitsPerSecond = 10_000_000_000,
        };
        var wifi = TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe()) with
        {
            SpeedBitsPerSecond = 600_000_000,
        };

        var metrics = InterfaceMetricPlanner.Plan(
            new[] { ethernet, wifi },
            new[] { TestData.ConnectedAdapter(TestData.AdapterA, "WiFi", "WiFi", 80) });

        Assert.Equal(10, metrics[wifi.Id]);
        Assert.Equal(60, metrics[ethernet.Id]);
    }

    [Fact]
    public void WifiRankingCombinesLinkRateAndSignalQuality()
    {
        var weakFast = TestData.WifiInterface(
            TestData.AdapterA, probe: TestData.OnlineProbe(), id: "luid:2001") with
        {
            Name = "Wi-Fi fast weak",
            SpeedBitsPerSecond = 1_200_000_000,
        };
        var strongStable = TestData.WifiInterface(
            TestData.AdapterB, probe: TestData.OnlineProbe(), id: "luid:2002") with
        {
            Name = "Wi-Fi stable",
            SpeedBitsPerSecond = 600_000_000,
        };

        var metrics = InterfaceMetricPlanner.Plan(
            new[] { weakFast, strongStable },
            new[]
            {
                TestData.ConnectedAdapter(TestData.AdapterA, "Weak", "Weak", 20),
                TestData.ConnectedAdapter(TestData.AdapterB, "Stable", "Stable", 80),
            });

        Assert.Equal(10, metrics[strongStable.Id]);
        Assert.Equal(12, metrics[weakFast.Id]);
    }

    [Fact]
    public void SingleRawFailure_DoesNotDisplacePreviouslyStableInterface()
    {
        var transientFailure = TestData.OfflineProbe() with { StableOnline = true, ConsecutiveFailures = 1 };
        var ethernet = TestData.EthernetInterface(probe: transientFailure) with
        {
            SpeedBitsPerSecond = 1_000_000_000,
        };
        var wifi = TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe()) with
        {
            SpeedBitsPerSecond = 600_000_000,
        };

        var metrics = InterfaceMetricPlanner.Plan(
            new[] { ethernet, wifi },
            new[] { TestData.ConnectedAdapter(TestData.AdapterA, "WiFi", "WiFi", 90) });

        Assert.Equal(10, metrics[ethernet.Id]);
        Assert.Equal(12, metrics[wifi.Id]);
    }
}
