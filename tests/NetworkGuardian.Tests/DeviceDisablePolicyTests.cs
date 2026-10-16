using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class DeviceDisablePolicyTests
{
    private const string DeviceId = @"USB\VID_0E8D&PID_7961\TEST";

    private static ManagedDevice Device(uint problem = 0, bool ethernet = false) => TestData.Managed(
        TestData.Pnp(DeviceId, problemCode: problem, started: problem == 0,
            netCfgInstanceId: TestData.AdapterA.ToString("D"),
            physicalMediaType: ethernet ? 14 : 9, service: ethernet ? "rt640x64" : "netwtw10"), true);

    private static GuardianInput Input(GuardianConfig config, params ManagedDevice[] devices) => new()
    {
        Now = TestData.Now, Config = config, Devices = devices, GlobalProbe = TestData.OfflineProbe(),
        Interfaces = new[] { TestData.WifiInterface(TestData.AdapterA) },
        WifiAdapters = new[] { TestData.DisconnectedAdapter(TestData.AdapterA) }, Radio = TestData.RadioOn,
    };

    [Theory]
    [InlineData(22u, false)]
    [InlineData(22u, true)]
    [InlineData(10u, false)]
    [InlineData(43u, false)]
    public void DisabledRule_PreventsAutomaticEnableRepairAndScan(uint problem, bool ethernet)
    {
        var config = TestData.Config(c => c.DisabledDeviceInstanceIds.Add(DeviceId.ToLowerInvariant()));
        var decision = new GuardianDecisionEngine(config).Evaluate(Input(config, Device(problem, ethernet)));

        Assert.Empty(decision.Actions.OfType<EnableWifiDeviceAction>());
        Assert.Empty(decision.Actions.OfType<RestartWifiDeviceAction>());
        Assert.Empty(decision.Actions.OfType<ScanAdapterAction>());
        Assert.Empty(decision.Actions.OfType<ConnectWifiAction>());
    }

    [Fact]
    public void RemovingRule_RestoresAutomaticEnable()
    {
        var config = TestData.Config(c => c.DisabledDeviceInstanceIds.Add(DeviceId));
        var engine = new GuardianDecisionEngine(config);
        Assert.Empty(engine.Evaluate(Input(config, Device(22))).Actions.OfType<EnableWifiDeviceAction>());

        config.DisabledDeviceInstanceIds.Clear();

        Assert.Single(engine.Evaluate(Input(config, Device(22))).Actions.OfType<EnableWifiDeviceAction>());
    }

    [Fact]
    public void Filter_RemovesBlockedInterfacesAndKeepsUnrelatedAdapter()
    {
        var config = TestData.Config(c => c.DisabledDeviceInstanceIds.Add(DeviceId));
        var original = Input(config, Device()) with
        {
            WifiAdapters = new[] { TestData.ConnectedAdapter(TestData.AdapterA, "Network", "Profile"), TestData.DisconnectedAdapter(TestData.AdapterB) },
            Interfaces = new[] { TestData.WifiInterface(TestData.AdapterA), TestData.WifiInterface(TestData.AdapterB) },
        };
        var filtered = DeviceDisablePolicy.Filter(original);

        Assert.Equal(TestData.AdapterB, Assert.Single(filtered.WifiAdapters).InterfaceGuid);
        Assert.Equal(TestData.AdapterB, Assert.Single(filtered.Interfaces).WlanInterfaceGuid);
        Assert.Empty(filtered.Devices);
        Assert.Equal(2, original.WifiAdapters.Count);
    }

    [Fact]
    public void Filter_UsesAdapterDeviceIdWhenPnpMappingIsTemporarilyMissing()
    {
        var config = TestData.Config(c => c.DisabledDeviceInstanceIds.Add(DeviceId));
        var input = Input(config) with
        {
            WifiAdapters = new[] { TestData.DisconnectedAdapter(TestData.AdapterA) with { DeviceInstanceId = DeviceId } },
        };

        var filtered = DeviceDisablePolicy.Filter(input);

        Assert.Empty(filtered.WifiAdapters);
        Assert.Empty(filtered.Interfaces);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(10u, true)]
    [InlineData(21u, false)]
    [InlineData(22u, false)]
    public void Enforcement_OnlyRequestsDisableWhenNeeded(uint problem, bool expected)
    {
        var config = TestData.Config(c => { c.DisabledDeviceInstanceIds.Add(DeviceId); c.General.AutomaticRecovery = false; });
        Assert.Equal(expected, DeviceDisablePolicy.NeedsDisable(config, Device(problem)));
        Assert.False(DeviceDisablePolicy.NeedsDisable(config, Device(problem) with { Record = Device(problem).Record with { IsPresent = false } }));
        Assert.False(DeviceDisablePolicy.NeedsDisable(config, Device(problem) with { Classification =
            Device(problem).Classification with { IsPhysical = false, Category = DeviceCategory.Virtual } }));
    }

    [Fact]
    public void Rule_SurvivesSerializationAndUnrelatedStaleSettingsSave()
    {
        var baseline = TestData.Config();
        var latest = ConfigJson.Deserialize(ConfigJson.Serialize(baseline))!;
        latest.DisabledDeviceInstanceIds.Add(DeviceId);
        baseline.General.HealthSweepSeconds = 19;
        var staleBaseline = TestData.Config();

        var saved = ConfigMerge.Apply(latest, staleBaseline, baseline);
        var reloaded = ConfigJson.Deserialize(ConfigJson.Serialize(saved))!;

        Assert.Equal(DeviceId, Assert.Single(reloaded.DisabledDeviceInstanceIds));
        Assert.Equal(19, reloaded.General.HealthSweepSeconds);
    }

    [Fact]
    public void Normalization_RepairsNullAndDeduplicatesDeviceRules()
    {
        var config = TestData.Config(c => c.DisabledDeviceInstanceIds = null!);
        new ConfigValidator().Normalize(config);
        Assert.Empty(config.DisabledDeviceInstanceIds);
        config.DisabledDeviceInstanceIds.AddRange(new[] { " ", DeviceId, DeviceId.ToLowerInvariant(), $" {DeviceId} " });

        new ConfigValidator().Normalize(config);

        Assert.Equal(DeviceId, Assert.Single(config.DisabledDeviceInstanceIds));
    }

    [Fact]
    public void OldConfig_DefaultsToNoDisabledDevices()
    {
        var old = ConfigJson.Deserialize("{\"version\":15}")!;
        Assert.Empty(old.DisabledDeviceInstanceIds);
        Assert.False(DeviceDisablePolicy.NeedsDisable(old, Device()));
    }
}
