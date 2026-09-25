using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class NetworkDiversityPolicyTests
{
    [Fact]
    public void HealthyEthernetOutletMovesStandbyWifiToDifferentKnownNetwork()
    {
        var config = TestData.Config(value =>
        {
            value.Wifi.CampusNetworkSsids.Add("CampusWiFi");
            value.Wifi.DisconnectGraceSeconds = 0;
        });
        var engine = new GuardianDecisionEngine(config);
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 90),
            TestData.Network(TestData.AdapterA, "PhoneHotspot", 55));
        var adapter = TestData.ConnectedAdapter(
            TestData.AdapterA, "CampusWiFi", "CampusWiFi", 90, scan) with
        {
            SavedProfiles = new[] { "CampusWiFi", "PhoneHotspot" },
        };

        var decision = engine.Evaluate(Input(config,
            EthernetOutlet(),
            TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe()),
            adapter));

        var disconnect = Assert.Single(decision.Actions.OfType<DisconnectWifiAction>());
        Assert.Equal("CampusWiFi", disconnect.Ssid);
        Assert.Contains("PhoneHotspot", disconnect.Reason);
    }

    [Fact]
    public void WifiOutletIsNeverMovedEvenWhenEthernetIsHealthy()
    {
        var config = TestData.Config(value => value.Wifi.CampusNetworkSsids.Add("CampusWiFi"));
        var engine = new GuardianDecisionEngine(config);
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 90),
            TestData.Network(TestData.AdapterA, "PhoneHotspot", 55));
        var adapter = TestData.ConnectedAdapter(
            TestData.AdapterA, "CampusWiFi", "CampusWiFi", 90, scan) with
        {
            SavedProfiles = new[] { "CampusWiFi", "PhoneHotspot" },
        };
        var ethernet = EthernetOutlet() with { IsDefaultRoute = false };
        var wifi = TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe()) with
        {
            IsDefaultRoute = true,
        };

        var decision = engine.Evaluate(Input(config, ethernet, wifi, adapter));

        Assert.DoesNotContain(decision.Actions, action => action is DisconnectWifiAction);
    }

    [Fact]
    public void SameNpcapNextHopIsAcceptedAsSameNetworkEvidence()
    {
        var config = TestData.Config();
        var engine = new GuardianDecisionEngine(config);
        var sharedHop = "00:11:22:33:44:55";
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "HomeWiFi", 90),
            TestData.Network(TestData.AdapterA, "PhoneHotspot", 50));
        var adapter = TestData.ConnectedAdapter(
            TestData.AdapterA, "HomeWiFi", "HomeWiFi", 90, scan) with
        {
            SavedProfiles = new[] { "HomeWiFi", "PhoneHotspot" },
        };
        var ethernet = EthernetOutlet() with
        {
            Probe = TestData.OnlineProbe() with { StableOnline = true, CaptureNextHopMac = sharedHop },
        };
        var wifi = TestData.WifiInterface(TestData.AdapterA, probe:
            TestData.OnlineProbe() with { StableOnline = true, CaptureNextHopMac = sharedHop });

        var decision = engine.Evaluate(Input(config, ethernet, wifi, adapter));

        Assert.Contains(decision.Actions, action => action is DisconnectWifiAction);
    }

    [Fact]
    public void RedundantWifiRemainsFallbackWhenNoDiverseKnownNetworkExists()
    {
        var config = TestData.Config(value =>
        {
            value.Wifi.CampusNetworkSsids.Add("CampusWiFi");
            value.Wifi.DisconnectGraceSeconds = 0;
        });
        var engine = new GuardianDecisionEngine(config);
        var scan = TestData.Scan(TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 80));
        var adapter = TestData.DisconnectedAdapter(TestData.AdapterA, new[] { "CampusWiFi" }, scan);

        var decision = engine.Evaluate(Input(config,
            EthernetOutlet(),
            TestData.WifiInterface(TestData.AdapterA, up: false, hasAddress: false),
            adapter));

        var connect = Assert.Single(decision.Actions.OfType<ConnectWifiAction>());
        Assert.Equal("CampusWiFi", connect.Ssid);
    }

    [Fact]
    public void OfflineStandbyWifiIsNotRotatedWhileEthernetIsHealthy()
    {
        var config = TestData.Config(value =>
        {
            value.Wifi.StaleConnectionSeconds = 0;
            value.Recovery.WifiFailureThreshold = 1;
        });
        var engine = new GuardianDecisionEngine(config);
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "LossyHotspot", 55),
            TestData.Network(TestData.AdapterA, "HomeWiFi", 90));
        var adapter = TestData.ConnectedAdapter(
            TestData.AdapterA, "LossyHotspot", "LossyHotspot", 55, scan) with
        {
            SavedProfiles = new[] { "LossyHotspot", "HomeWiFi" },
        };
        var standby = TestData.WifiInterface(TestData.AdapterA, probe: TestData.OfflineProbe()) with
        {
            IsDefaultRoute = false,
        };

        var decision = engine.Evaluate(Input(config, EthernetOutlet(), standby, adapter));

        Assert.DoesNotContain(decision.Actions, action => action is DisconnectWifiAction);
        Assert.Contains(decision.Notes, note => note.Contains("preserving the standby"));
    }

    private static InterfaceRuntimeState EthernetOutlet() =>
        TestData.EthernetInterface(probe: TestData.OnlineProbe() with { StableOnline = true }) with
        {
            IsDefaultRoute = true,
        };

    private static GuardianInput Input(
        Core.Configuration.GuardianConfig config,
        InterfaceRuntimeState ethernet,
        InterfaceRuntimeState wifi,
        WifiAdapterRuntimeState adapter) => new()
    {
        Now = TestData.Now,
        Config = config,
        GlobalProbe = TestData.OnlineProbe(),
        Interfaces = new[] { ethernet, wifi },
        WifiAdapters = new[] { adapter },
        Devices = Array.Empty<ManagedDevice>(),
        Radio = TestData.RadioOn,
    };
}
