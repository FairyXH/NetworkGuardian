using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// Sticky connection guarantees: an adapter that is connected and working must never be switched,
/// no matter how much stronger another visible network is.
/// </summary>
public sealed class StickyConnectionTests
{
    private static GuardianDecisionEngine Engine(Action<Core.Configuration.GuardianConfig>? configure = null) =>
        new(TestData.Config(configure));

    private static GuardianInput Input(
        Core.Configuration.GuardianConfig config,
        DateTimeOffset now,
        ConnectivityProbeReport probe,
        IReadOnlyList<InterfaceRuntimeState> interfaces,
        IReadOnlyList<WifiAdapterRuntimeState> adapters,
        IReadOnlyList<ManagedDevice>? devices = null,
        WifiRadioSnapshot? radio = null,
        bool paused = false)
    {
        _ = interfaces;
        return new GuardianInput
        {
            Now = now,
            Config = config,
            GlobalProbe = probe,
            Interfaces = interfaces,
            WifiAdapters = adapters,
            Devices = devices ?? Array.Empty<ManagedDevice>(),
            Radio = radio ?? TestData.RadioOn,
            IsPaused = paused,
        };
    }

    [Fact]
    public void ConnectedAndWorkingAdapter_IsNeverScannedOrSwitched()
    {
        var config = TestData.Config();
        var engine = new GuardianDecisionEngine(config);

        // The adapter sees a much stronger network than the one it is connected to.
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "DormWiFi", 92),
            TestData.Network(TestData.AdapterA, "CampusWiFi", 45));

        var adapter = TestData.ConnectedAdapter(TestData.AdapterA, "CampusWiFi", "CampusWiFi", 45, scan);
        var interfaces = new[]
        {
            TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe("10.20.30.40")),
        };

        var decision = engine.Evaluate(Input(
            config, TestData.Now, TestData.OnlineProbe(), interfaces, new[] { adapter }));

        Assert.DoesNotContain(decision.Actions, a => a is ConnectWifiAction);
        Assert.DoesNotContain(decision.Actions, a => a is DisconnectWifiAction);
        Assert.DoesNotContain(decision.Actions, a => a is ScanAdapterAction);
        Assert.Equal(RecoveryState.Healthy, decision.State);
    }

    [Fact]
    public void TwoConnectedAdapters_AreNeverSwappedEvenWhenTheOtherIsBetter()
    {
        var config = TestData.Config();
        var engine = new GuardianDecisionEngine(config);

        // Adapter A is on Campus at 45%, adapter B on Dorm at 58%; the scans say the opposite would
        // be "better". The engine must not touch either of them.
        var scanA = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 45),
            TestData.Network(TestData.AdapterA, "DormWiFi", 91));
        var scanB = TestData.Scan(
            TestData.AdapterB,
            TestData.Network(TestData.AdapterB, "DormWiFi", 58),
            TestData.Network(TestData.AdapterB, "CampusWiFi", 90));

        var adapterA = TestData.ConnectedAdapter(TestData.AdapterA, "CampusWiFi", "CampusWiFi", 45, scanA);
        var adapterB = TestData.ConnectedAdapter(
            TestData.AdapterB, "DormWiFi", "DormWiFi", 58, scanB, "TP-Link USB Wi-Fi");

        var interfaces = new[]
        {
            TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe("10.20.30.40")),
            TestData.WifiInterface(TestData.AdapterB, probe: TestData.OnlineProbe("10.20.40.40"), id: "luid:2002"),
        };

        var decision = engine.Evaluate(Input(
            config, TestData.Now, TestData.OnlineProbe(), interfaces, new[] { adapterA, adapterB }));

        Assert.DoesNotContain(decision.Actions, a => a is ConnectWifiAction or DisconnectWifiAction or ScanAdapterAction);
    }

    [Fact]
    public void ConnectedAdapterThatStillPassesTraffic_IsLeftAlone_EvenIfGlobalProbeFails()
    {
        // Only one interface is up, and its interface-bound probe says it is fine: the failing global
        // probe must not make us tear down a working connection.
        var config = TestData.Config();
        var engine = new GuardianDecisionEngine(config);

        var adapter = TestData.ConnectedAdapter(TestData.AdapterA, "CampusWiFi", "CampusWiFi", 60);
        var interfaces = new[]
        {
            TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe("10.20.30.40")),
        };

        for (var i = 0; i < 5; i++)
        {
            var decision = engine.Evaluate(Input(
                config,
                TestData.Now.AddSeconds(i * 15),
                TestData.OfflineProbe(),
                interfaces,
                new[] { adapter }));

            Assert.DoesNotContain(decision.Actions, a => a is DisconnectWifiAction);
            Assert.DoesNotContain(decision.Actions, a => a is ConnectWifiAction);
        }
    }

    [Fact]
    public void SingleFailedProbe_DoesNotTriggerRecovery()
    {
        var config = TestData.Config(c => c.Recovery.WifiFailureThreshold = 3);
        var engine = new GuardianDecisionEngine(config);

        var adapter = TestData.ConnectedAdapter(TestData.AdapterA, "CampusWiFi", "CampusWiFi", 60);
        var interfaces = new[]
        {
            TestData.WifiInterface(TestData.AdapterA, probe: TestData.OfflineProbe("10.20.30.40")),
        };

        var decision = engine.Evaluate(Input(
            config, TestData.Now, TestData.OfflineProbe(), interfaces, new[] { adapter }));

        Assert.DoesNotContain(decision.Actions, a => a is DisconnectWifiAction);

        // A successful probe in between must reset the failure counter.
        var recovery = engine.Evaluate(Input(
            config,
            TestData.Now.AddSeconds(15),
            TestData.OnlineProbe(),
            new[] { TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe("10.20.30.40")) },
            new[] { adapter }));

        Assert.DoesNotContain(recovery.Actions, a => a is DisconnectWifiAction);
    }

    [Fact]
    public void StaleConnection_IsDisconnectedAndRescannedAfterThreshold()
    {
        var config = TestData.Config(c =>
        {
            c.Recovery.WifiFailureThreshold = 2;
            c.Wifi.RecoverStaleConnections = true;
        });

        var engine = new GuardianDecisionEngine(config);
        var adapter = TestData.ConnectedAdapter(TestData.AdapterA, "CampusWiFi", "CampusWiFi", 60);
        var interfaces = new[]
        {
            TestData.WifiInterface(TestData.AdapterA, probe: TestData.OfflineProbe("10.20.30.40")),
        };

        GuardianDecision? decision = null;
        for (var i = 0; i < 3; i++)
        {
            decision = engine.Evaluate(Input(
                config,
                TestData.Now.AddSeconds(i * 15),
                TestData.OfflineProbe(),
                interfaces,
                new[] { adapter }));
        }

        Assert.NotNull(decision);
        Assert.Contains(decision!.Actions, a => a is DisconnectWifiAction);
        Assert.Contains(decision.Actions, a => a is ScanAdapterAction);
    }

    [Fact]
    public void StickyConnectionDisabled_StillRequiresFailuresBeforeSwitching()
    {
        var config = TestData.Config(c =>
        {
            c.Wifi.StickyConnection = false;
            c.Recovery.WifiFailureThreshold = 3;
        });

        var engine = new GuardianDecisionEngine(config);
        var adapter = TestData.ConnectedAdapter(TestData.AdapterA, "CampusWiFi", "CampusWiFi", 40);
        var interfaces = new[]
        {
            TestData.WifiInterface(TestData.AdapterA, probe: TestData.OfflineProbe("10.20.30.40")),
        };

        var first = engine.Evaluate(Input(
            config, TestData.Now, TestData.OfflineProbe(), interfaces, new[] { adapter }));

        Assert.DoesNotContain(first.Actions, a => a is ConnectWifiAction);
    }

    [Fact]
    public void DisconnectedAdapter_ScansThenConnectsToTheStrongestSavedProfile()
    {
        var config = TestData.Config(c => c.Wifi.DisconnectGraceSeconds = 0);
        var engine = new GuardianDecisionEngine(config);

        // First round: nothing scanned yet, so the engine must ask for a scan.
        var adapter = TestData.DisconnectedAdapter(TestData.AdapterA, new[] { "CampusWiFi", "DormWiFi" });
        var interfaces = new[]
        {
            TestData.WifiInterface(TestData.AdapterA, up: false, hasAddress: false),
        };

        var first = engine.Evaluate(Input(
            config, TestData.Now, TestData.OfflineProbe(), interfaces, new[] { adapter }));

        Assert.Contains(first.Actions, a => a is ScanAdapterAction);

        // Second round with a completed scan: it must pick the strongest saved profile.
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 40),
            TestData.Network(TestData.AdapterA, "DormWiFi", 85),
            TestData.Network(TestData.AdapterA, "CoffeeShop", 99, hasProfile: false));

        var scanned = TestData.DisconnectedAdapter(TestData.AdapterA, new[] { "CampusWiFi", "DormWiFi" }, scan);

        var second = engine.Evaluate(Input(
            config, TestData.Now.AddSeconds(40), TestData.OfflineProbe(), interfaces, new[] { scanned }));

        var connect = Assert.Single(second.Actions.OfType<ConnectWifiAction>());
        Assert.Equal("DormWiFi", connect.ProfileName);
        Assert.Equal("DormWiFi", connect.Ssid);
    }

    [Fact]
    public void PausedEngine_ProducesNoActions()
    {
        var config = TestData.Config();
        var engine = new GuardianDecisionEngine(config);

        var adapter = TestData.DisconnectedAdapter(TestData.AdapterA);
        var interfaces = new[] { TestData.WifiInterface(TestData.AdapterA, up: false, hasAddress: false) };

        var decision = engine.Evaluate(Input(
            config, TestData.Now, TestData.OfflineProbe(), interfaces, new[] { adapter }, paused: true));

        Assert.Empty(decision.Actions);
        Assert.Equal(RecoveryState.Paused, decision.State);
    }

    [Fact]
    public void RadioOff_TurnsTheRadioOnWhenAllowed()
    {
        var config = TestData.Config(c => c.General.AutoEnableWifiRadio = true);
        var engine = new GuardianDecisionEngine(config);

        var decision = engine.Evaluate(Input(
            config,
            TestData.Now,
            TestData.OfflineProbe(),
            Array.Empty<InterfaceRuntimeState>(),
            Array.Empty<WifiAdapterRuntimeState>(),
            radio: new WifiRadioSnapshot { State = RadioState.Off, ObservedAtUtc = TestData.Now }));

        Assert.Contains(decision.Actions, a => a is EnableWifiRadioAction);
    }

    [Fact]
    public void RadioOff_IsLeftAloneWhenAutoEnableIsDisabled()
    {
        var config = TestData.Config(c => c.General.AutoEnableWifiRadio = false);
        var engine = new GuardianDecisionEngine(config);

        var decision = engine.Evaluate(Input(
            config,
            TestData.Now,
            TestData.OfflineProbe(),
            Array.Empty<InterfaceRuntimeState>(),
            Array.Empty<WifiAdapterRuntimeState>(),
            radio: new WifiRadioSnapshot { State = RadioState.Off, ObservedAtUtc = TestData.Now }));

        Assert.DoesNotContain(decision.Actions, a => a is EnableWifiRadioAction);
        Assert.Equal(RecoveryState.WifiRadioOff, decision.State);
    }

    [Fact]
    public void DisabledPhysicalWifiDevice_RequestsEnableOnceThenWaits()
    {
        var config = TestData.Config(c =>
        {
            c.General.AutoEnableWifiDevices = true;
            c.Recovery.MaxDeviceEnablePerHour = 6;
            c.Recovery.DeviceEnableSettleSeconds = 6;
        });

        var engine = new GuardianDecisionEngine(config);

        var disabled = TestData.Managed(
            TestData.Pnp(
                @"PCI\VEN_8086&DEV_51F0\3&11583659&0&A0",
                problemCode: 22,
                started: false),
            physical: true);

        var first = engine.Evaluate(Input(
            config,
            TestData.Now,
            TestData.OfflineProbe(),
            Array.Empty<InterfaceRuntimeState>(),
            Array.Empty<WifiAdapterRuntimeState>(),
            new[] { disabled }));

        Assert.Contains(first.Actions, a => a is EnableWifiDeviceAction);

        // Immediately afterwards the same request must be rate limited.
        var second = engine.Evaluate(Input(
            config,
            TestData.Now.AddSeconds(2),
            TestData.OfflineProbe(),
            Array.Empty<InterfaceRuntimeState>(),
            Array.Empty<WifiAdapterRuntimeState>(),
            new[] { disabled }));

        Assert.DoesNotContain(second.Actions, a => a is EnableWifiDeviceAction);
    }

    [Fact]
    public void VirtualWifiDevice_IsNeverEnabled()
    {
        var config = TestData.Config(c => c.General.AutoEnableWifiDevices = true);
        var engine = new GuardianDecisionEngine(config);

        var virtualAdapter = TestData.Managed(
            TestData.Pnp(
                @"{5d624f94-8850-40c3-a3fa-a4fd2080baf3}\vwifimp_wfd0",
                service: "vwifibus",
                friendlyName: "Microsoft Wi-Fi Direct Virtual Adapter",
                hardwareIds: new[] { "vwifimp_wfd0" },
                problemCode: 22,
                started: false),
            physical: false);

        var decision = engine.Evaluate(Input(
            config,
            TestData.Now,
            TestData.OfflineProbe(),
            Array.Empty<InterfaceRuntimeState>(),
            Array.Empty<WifiAdapterRuntimeState>(),
            new[] { virtualAdapter }));

        Assert.False(virtualAdapter.Classification.IsPhysical);
        Assert.DoesNotContain(decision.Actions, a => a is EnableWifiDeviceAction);
    }
}
