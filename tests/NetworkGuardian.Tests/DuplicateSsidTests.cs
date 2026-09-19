using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// Two Wi-Fi adapters associated with the same access point duplicate every frame and can make the
/// link worse for both. With "one SSID per adapter" the weaker adapter has to give the SSID up, while
/// the stronger one keeps its sticky connection untouched.
/// </summary>
public sealed class DuplicateSsidTests
{
    private static GuardianInput Input(
        Core.Configuration.GuardianConfig config,
        IReadOnlyList<WifiAdapterRuntimeState> adapters,
        DateTimeOffset? now = null) => new()
    {
        Now = now ?? TestData.Now,
        Config = config,
        GlobalProbe = TestData.OnlineProbe(),
        Interfaces = new[]
        {
            TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe("10.20.30.40"), id: "luid:2001"),
            TestData.WifiInterface(TestData.AdapterB, probe: TestData.OnlineProbe("10.20.30.41"), id: "luid:2002"),
        },
        WifiAdapters = adapters,
        Devices = Array.Empty<ManagedDevice>(),
        Radio = TestData.RadioOn,
    };

    private static WifiAdapterRuntimeState Connected(Guid guid, int quality, string description) =>
        TestData.ConnectedAdapter(guid, "CampusWiFi", "CampusWiFi", quality, description: description);

    [Fact]
    public void TwoAdaptersOnTheSameSsid_ReleaseTheWeakerOne()
    {
        var config = TestData.Config(c => c.Wifi.AllowSameSsidOnMultipleAdapters = false);
        var engine = new GuardianDecisionEngine(config);

        var strong = Connected(TestData.AdapterA, 82, "Adapter A");
        var weak = Connected(TestData.AdapterB, 38, "Adapter B");

        var decision = engine.Evaluate(Input(config, new[] { strong, weak }));

        var disconnect = Assert.Single(decision.Actions.OfType<DisconnectWifiAction>());
        Assert.Equal(TestData.AdapterB, disconnect.InterfaceGuid);
        Assert.Equal("CampusWiFi", disconnect.Ssid);
        Assert.True(disconnect.SuppressAutoReconnect);

        // The stronger adapter must not be disturbed: it is the one that keeps the connection.
        Assert.DoesNotContain(decision.Actions, a => a is DisconnectWifiAction && a != disconnect);
        Assert.Contains(decision.Notes, n => n.Contains("CampusWiFi", StringComparison.Ordinal));
    }

    [Fact]
    public void OneSsidPerAdapterIsTheDefault()
    {
        var config = TestData.Config();
        Assert.False(config.Wifi.AllowSameSsidOnMultipleAdapters);
    }

    [Fact]
    public void DuplicateLoser_ImmediatelySwitchesToASavedAlternative()
    {
        var config = TestData.Config(c => c.Wifi.AllowSameSsidOnMultipleAdapters = false);
        var engine = new GuardianDecisionEngine(config);
        var scan = TestData.Scan(
            TestData.AdapterB,
            TestData.Network(TestData.AdapterB, "CampusWiFi", 90),
            TestData.Network(TestData.AdapterB, "DormWiFi", 55));
        var strong = Connected(TestData.AdapterA, 82, "Adapter A");
        var weak = TestData.ConnectedAdapter(
            TestData.AdapterB, "CampusWiFi", "CampusWiFi", 38, scan, "Adapter B") with
        {
            SavedProfiles = new[] { "CampusWiFi", "DormWiFi" },
        };

        var decision = engine.Evaluate(Input(config, new[] { strong, weak }));

        Assert.Contains(decision.Actions,
            action => action is DisconnectWifiAction { InterfaceGuid: var guid } && guid == TestData.AdapterB);
        var connect = Assert.Single(decision.Actions.OfType<ConnectWifiAction>());
        Assert.Equal(TestData.AdapterB, connect.InterfaceGuid);
        Assert.Equal("DormWiFi", connect.Ssid);
    }

    [Fact]
    public void AllowingSharedSsids_LeavesBothAdaptersConnected()
    {
        var config = TestData.Config(c => c.Wifi.AllowSameSsidOnMultipleAdapters = true);
        var engine = new GuardianDecisionEngine(config);

        var decision = engine.Evaluate(Input(config, new[]
        {
            Connected(TestData.AdapterA, 82, "Adapter A"),
            Connected(TestData.AdapterB, 38, "Adapter B"),
        }));

        Assert.DoesNotContain(decision.Actions, a => a is DisconnectWifiAction);
    }

    [Fact]
    public void DifferentSsids_AreNotTouched()
    {
        var config = TestData.Config(c => c.Wifi.AllowSameSsidOnMultipleAdapters = false);
        var engine = new GuardianDecisionEngine(config);

        var first = TestData.ConnectedAdapter(TestData.AdapterA, "CampusWiFi", "CampusWiFi", 70, description: "Adapter A");
        var second = TestData.ConnectedAdapter(TestData.AdapterB, "DormWiFi", "DormWiFi", 55, description: "Adapter B");

        var decision = engine.Evaluate(Input(config, new[] { first, second }));

        Assert.DoesNotContain(decision.Actions, a => a is DisconnectWifiAction);
    }

    [Fact]
    public void TheWeakerAdapter_DoesNotImmediatelyReconnectToTheHeldSsid()
    {
        var config = TestData.Config(c => c.Wifi.AllowSameSsidOnMultipleAdapters = false);
        var engine = new GuardianDecisionEngine(config);

        // Adapter B lost the SSID and now scans it again; the only other visible network is a different
        // one, but the engine must not hand "CampusWiFi" back to it while adapter A still holds it.
        var scan = TestData.Scan(
            TestData.AdapterB,
            TestData.Network(TestData.AdapterB, "CampusWiFi", 90),
            TestData.Network(TestData.AdapterB, "DormWiFi", 55));

        var strong = Connected(TestData.AdapterA, 82, "Adapter A");
        var released = TestData.DisconnectedAdapter(TestData.AdapterB, new[] { "CampusWiFi", "DormWiFi" }, scan);

        // First pass: the adapter has only just lost the SSID, so the disconnect grace period applies.
        engine.Evaluate(Input(config, new[] { strong, released }));

        // Second pass, after the grace period: it may connect again - but not to the held SSID.
        var decision = engine.Evaluate(Input(config, new[] { strong, released },
            TestData.Now.AddSeconds(30)));

        Assert.DoesNotContain(decision.Actions,
            a => a is ConnectWifiAction connect && connect.Ssid == "CampusWiFi");

        var connect = Assert.Single(decision.Actions.OfType<ConnectWifiAction>());
        Assert.Equal("DormWiFi", connect.Ssid);
        Assert.Equal(TestData.AdapterB, connect.InterfaceGuid);
    }
}
