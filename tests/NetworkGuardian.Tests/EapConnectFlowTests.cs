using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// The 802.1X/EAP behaviour requested for campus networks: connect through the built-in wireless network
/// library, count the attempts, and give the network up for the rest of the run after the configured
/// number of failures (the next start tries again).
/// </summary>
public sealed class EapConnectFlowTests
{
    private static GuardianInput Input(
        GuardianConfig config,
        DateTimeOffset now,
        IReadOnlyList<WifiAdapterRuntimeState> adapters,
        WifiEapCatalog? catalog = null) => new()
    {
        Now = now,
        Config = config,
        GlobalProbe = TestData.OfflineProbe(),
        Interfaces = new[] { TestData.WifiInterface(TestData.AdapterA, up: false, hasAddress: false) },
        WifiAdapters = adapters,
        Devices = Array.Empty<ManagedDevice>(),
        Radio = TestData.RadioOn,
        EapCatalog = catalog ?? WifiEapCatalog.Empty,
    };

    private static AdapterScanSnapshot CampusScan() => TestData.Scan(
        TestData.AdapterA,
        TestData.Network(TestData.AdapterA, "HXXY-WiFi", 70, security: WifiSecurity.Wpa2Enterprise));

    private static WifiEapCatalog CatalogWith(params string[] ssids) => new()
    {
        SsidsWithCredentials = ssids.ToHashSet(StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void EnterpriseNetworkWithoutALibraryEntry_IsNotConnected()
    {
        var config = TestData.Config(c => c.Wifi.DisconnectGraceSeconds = 0);
        var engine = new GuardianDecisionEngine(config);

        var adapter = TestData.DisconnectedAdapter(
            TestData.AdapterA, new[] { "HXXY-WiFi" }, CampusScan());

        var decision = engine.Evaluate(Input(config, TestData.Now, new[] { adapter }));

        Assert.DoesNotContain(decision.Actions, a => a is ConnectWifiAction);
        Assert.Contains(decision.Notes, n => n.Contains("自维护无线网络库"));
    }

    [Fact]
    public void EnterpriseNetworkWithALibraryEntry_ConnectsWithTheLibraryAccount()
    {
        var config = TestData.Config(c => c.Wifi.DisconnectGraceSeconds = 0);
        var engine = new GuardianDecisionEngine(config);

        var adapter = TestData.DisconnectedAdapter(
            TestData.AdapterA, new[] { "HXXY-WiFi" }, CampusScan());

        var decision = engine.Evaluate(Input(
            config, TestData.Now, new[] { adapter }, CatalogWith("HXXY-WiFi")));

        var connect = Assert.Single(decision.Actions.OfType<ConnectWifiAction>());
        Assert.Equal("HXXY-WiFi", connect.Ssid);
        Assert.True(connect.RequiresEap);
        Assert.True(connect.UsesLibraryCredential);
        Assert.Contains("自维护无线网络库", connect.Reason);
    }

    [Fact]
    public void PersonalNetworkInLibrary_IsCandidateWithoutAWindowsProfile()
    {
        var config = TestData.Config(c => c.Wifi.DisconnectGraceSeconds = 0);
        var engine = new GuardianDecisionEngine(config);
        var scan = TestData.Scan(TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "NewAdapterHome", 80,
                hasProfile: false, security: WifiSecurity.Wpa2Personal));
        var adapter = TestData.DisconnectedAdapter(TestData.AdapterA, Array.Empty<string>(), scan);
        var catalog = new WifiEapCatalog
        {
            SsidsWithCredentials = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NewAdapterHome" },
            ProfileNamesBySsid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NewAdapterHome"] = "NewAdapterHome",
            },
        };

        var decision = engine.Evaluate(Input(config, TestData.Now, new[] { adapter }, catalog));

        var connect = Assert.Single(decision.Actions.OfType<ConnectWifiAction>());
        Assert.Equal("NewAdapterHome", connect.ProfileName);
        Assert.False(connect.RequiresEap);
        Assert.True(connect.UsesLibraryCredential);
    }

    [Fact]
    public void LibraryProfileWinsWhenSeveralProfilesShareTheSameSsid()
    {
        var config = TestData.Config(c => c.Wifi.DisconnectGraceSeconds = 0);
        var engine = new GuardianDecisionEngine(config);
        var scan = TestData.Scan(TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "HXXY-WiFi", 70,
                profileName: "NG-SELFTEST-HXXY", security: WifiSecurity.Wpa2Enterprise));
        var adapter = TestData.DisconnectedAdapter(
            TestData.AdapterA, new[] { "NG-SELFTEST-HXXY", "HXXY-WiFi" }, scan);
        var catalog = new WifiEapCatalog
        {
            SsidsWithCredentials = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HXXY-WiFi" },
            ProfileNamesBySsid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["HXXY-WiFi"] = "HXXY-WiFi",
            },
        };

        var decision = engine.Evaluate(Input(config, TestData.Now, new[] { adapter }, catalog));

        var connect = Assert.Single(decision.Actions.OfType<ConnectWifiAction>());
        Assert.Equal("HXXY-WiFi", connect.ProfileName);
    }

    [Fact]
    public void LibraryDisabled_LeavesEnterpriseNetworksToWindows()
    {
        var config = TestData.Config(c =>
        {
            c.Wifi.DisconnectGraceSeconds = 0;
            c.Wifi.UseCredentialLibraryForEap = false;
        });

        var engine = new GuardianDecisionEngine(config);
        var adapter = TestData.DisconnectedAdapter(
            TestData.AdapterA, new[] { "HXXY-WiFi" }, CampusScan());

        var decision = engine.Evaluate(Input(config, TestData.Now, new[] { adapter }));

        var connect = Assert.Single(decision.Actions.OfType<ConnectWifiAction>());
        Assert.True(connect.RequiresEap);
        Assert.False(connect.UsesLibraryCredential);
    }

    [Fact]
    public void FiveFailed8021XAttempts_AbandonTheNetworkForThisRun()
    {
        var config = TestData.Config(c => c.Wifi.DisconnectGraceSeconds = 0);
        var engine = new GuardianDecisionEngine(config);
        var catalog = CatalogWith("HXXY-WiFi");
        var adapter = TestData.DisconnectedAdapter(
            TestData.AdapterA, new[] { "HXXY-WiFi" }, CampusScan());

        // Attempt 1 is produced by the engine, then each failure is reported back.
        var first = engine.Evaluate(Input(config, TestData.Now, new[] { adapter }, catalog));
        Assert.Single(first.Actions.OfType<ConnectWifiAction>());

        for (var attempt = 1; attempt <= engine.EapRetries.MaxAttempts; attempt++)
        {
            engine.NotifyConnectResult(
                TestData.AdapterA, "HXXY-WiFi", success: false, "802.1X authentication failed",
                TestData.Now.AddSeconds(attempt * 30), requiredEap: true);
        }

        Assert.True(engine.EapRetries.IsAbandoned(TestData.AdapterA, "HXXY-WiFi"));

        // Long after the short-term connect blacklist expired: the give-up is what remains.
        var after = engine.Evaluate(Input(
            config, TestData.Now.AddMinutes(30), new[] { adapter }, catalog));

        Assert.DoesNotContain(after.Actions, a => a is ConnectWifiAction);
        Assert.Contains(after.Notes, n => n.Contains("临时放弃"));
        Assert.Contains(
            engine.GetDiagnostics(TestData.Now).EapAbandonedNetworks,
            entry => entry.Contains("HXXY-WiFi"));
    }

    [Fact]
    public void AbandoningOneNetwork_DoesNotAffectOtherNetworksOrAdapters()
    {
        var config = TestData.Config(c => c.Wifi.DisconnectGraceSeconds = 0);
        var engine = new GuardianDecisionEngine(config);
        var catalog = CatalogWith("HXXY-WiFi", "DormWiFi");

        for (var attempt = 0; attempt < engine.EapRetries.MaxAttempts; attempt++)
        {
            engine.NotifyConnectResult(
                TestData.AdapterA, "HXXY-WiFi", success: false, "rejected",
                TestData.Now.AddSeconds(attempt), requiredEap: true);
        }

        Assert.True(engine.EapRetries.IsAbandoned(TestData.AdapterA, "HXXY-WiFi"));
        Assert.False(engine.EapRetries.IsAbandoned(TestData.AdapterB, "HXXY-WiFi"));

        // Another network on the same adapter is still a candidate.
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "HXXY-WiFi", 90, security: WifiSecurity.Wpa2Enterprise),
            TestData.Network(TestData.AdapterA, "DormWiFi", 60, security: WifiSecurity.Wpa2Enterprise));

        var adapter = TestData.DisconnectedAdapter(TestData.AdapterA, new[] { "HXXY-WiFi", "DormWiFi" }, scan);
        var decision = engine.Evaluate(Input(
            config, TestData.Now.AddMinutes(1), new[] { adapter }, catalog));

        var connect = Assert.Single(decision.Actions.OfType<ConnectWifiAction>());
        Assert.Equal("DormWiFi", connect.Ssid);

        // The second adapter may still try the abandoned-by-adapter-A network.
        var adapterB = TestData.DisconnectedAdapter(TestData.AdapterB, new[] { "HXXY-WiFi" }, TestData.Scan(
            TestData.AdapterB,
            TestData.Network(TestData.AdapterB, "HXXY-WiFi", 70, security: WifiSecurity.Wpa2Enterprise)));
        var decisionB = engine.Evaluate(Input(
            config, TestData.Now.AddMinutes(1), new[] { TestData.DisconnectedAdapter(TestData.AdapterA, new[] { "HXXY-WiFi" }, CampusScan()), adapterB }, catalog));

        Assert.Contains(decisionB.Actions.OfType<ConnectWifiAction>(), a => a.Ssid == "HXXY-WiFi");
    }

    [Fact]
    public void ASuccessfulAuthenticationClearsTheCounter()
    {
        var config = TestData.Config();
        var engine = new GuardianDecisionEngine(config);
        var now = TestData.Now;

        engine.NotifyConnectResult(TestData.AdapterA, "HXXY-WiFi", false, "rejected", now, requiredEap: true);
        engine.NotifyConnectResult(TestData.AdapterA, "HXXY-WiFi", false, "rejected", now, requiredEap: true);
        Assert.Equal(2, engine.EapRetries.FailureCount(TestData.AdapterA, "HXXY-WiFi"));

        engine.NotifyConnectResult(TestData.AdapterA, "HXXY-WiFi", true, null, now, requiredEap: true);

        Assert.Equal(0, engine.EapRetries.FailureCount(TestData.AdapterA, "HXXY-WiFi"));
        Assert.False(engine.EapRetries.IsAbandoned(TestData.AdapterA, "HXXY-WiFi"));
    }

    [Fact]
    public void FixingTheAccount_AllowsAManualRetry()
    {
        var config = TestData.Config(c => c.Wifi.DisconnectGraceSeconds = 0);
        var engine = new GuardianDecisionEngine(config);
        var catalog = CatalogWith("HXXY-WiFi");

        for (var attempt = 0; attempt < engine.EapRetries.MaxAttempts; attempt++)
        {
            engine.NotifyConnectResult(
                TestData.AdapterA, "HXXY-WiFi", false, "rejected", TestData.Now, requiredEap: true);
        }

        var adapter = TestData.DisconnectedAdapter(
            TestData.AdapterA, new[] { "HXXY-WiFi" }, CampusScan());
        Assert.DoesNotContain(
            engine.Evaluate(Input(config, TestData.Now.AddMinutes(1), new[] { adapter }, catalog))
                .Actions.OfType<ConnectWifiAction>(),
            _ => true);

        // The user corrected the password in the library: the counters of that SSID are dropped.
        Assert.Equal(1, engine.ClearEapRetries("HXXY-WiFi"));

        Assert.Contains(
            engine.Evaluate(Input(config, TestData.Now.AddMinutes(30), new[] { adapter }, catalog))
                .Actions.OfType<ConnectWifiAction>(),
            a => a.Ssid == "HXXY-WiFi");
    }

    [Fact]
    public void PersonalNetworks_AreUnaffectedByTheEapRules()
    {
        var config = TestData.Config(c => c.Wifi.DisconnectGraceSeconds = 0);
        var engine = new GuardianDecisionEngine(config);

        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "ZhangAndroid", 80));

        var adapter = TestData.DisconnectedAdapter(TestData.AdapterA, new[] { "ZhangAndroid" }, scan);
        var decision = engine.Evaluate(Input(config, TestData.Now, new[] { adapter }));

        var connect = Assert.Single(decision.Actions.OfType<ConnectWifiAction>());
        Assert.False(connect.RequiresEap);
        Assert.False(connect.UsesLibraryCredential);
    }
}
