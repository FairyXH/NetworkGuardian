using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// A device that failed to start is broken, not disabled. Enabling it is not a repair, and doing so
/// every monitor cycle is a real state change on the machine, so the engine must never ask for it.
/// </summary>
public sealed class FaultedDeviceHandlingTests
{
    private static GuardianInput Input(GuardianConfigHolder holder, IReadOnlyList<ManagedDevice> devices) => new()
    {
        Now = TestData.Now,
        Config = holder.Config,
        GlobalProbe = TestData.OnlineProbe(),
        Interfaces = Array.Empty<InterfaceRuntimeState>(),
        WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
        Devices = devices,
        Radio = TestData.RadioOn,
    };

    private sealed record GuardianConfigHolder(Core.Configuration.GuardianConfig Config);

    [Fact]
    public void DeviceWithDriverFault_IsNotEnabledAndIsExplained()
    {
        var config = TestData.Config(c => c.General.AutoEnableWifiDevices = true);
        var engine = new GuardianDecisionEngine(config);

        // Problem code 43 = CM_PROB_FAILED_POST_START: the device is present but the driver faulted.
        var faulted = TestData.Managed(
            TestData.Pnp(@"PCI\VEN_8086&DEV_7A70&SUBSYS_40708086&REV_11\3&11583659&0&A3", problemCode: 43, started: false),
            physical: true);

        Assert.True(faulted.Classification.IsPhysical);
        Assert.False(faulted.IsEnabled);
        Assert.False(faulted.IsDisabled);

        var decision = engine.Evaluate(Input(new GuardianConfigHolder(config), new[] { faulted }));

        Assert.DoesNotContain(decision.Actions, a => a is EnableWifiDeviceAction);
        Assert.Contains(decision.Notes, n => n.Contains("43", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledDevice_IsStillEnabled()
    {
        var config = TestData.Config(c => c.General.AutoEnableWifiDevices = true);
        var engine = new GuardianDecisionEngine(config);

        var disabled = TestData.Managed(
            TestData.Pnp(@"USB\VID_0E8D&PID_7961\000000000", problemCode: 22, started: false),
            physical: true);

        Assert.True(disabled.IsDisabled);

        var decision = engine.Evaluate(Input(new GuardianConfigHolder(config), new[] { disabled }));

        Assert.Contains(decision.Actions, a => a is EnableWifiDeviceAction);
    }

    [Theory]
    [InlineData(0, true, true, false)]   // running normally
    [InlineData(0, false, false, false)] // stopped without a problem code
    [InlineData(21, false, false, true)] // CM_PROB_WILL_BE_REMOVED style
    [InlineData(22, false, false, true)] // CM_PROB_DISABLED
    [InlineData(43, false, false, false)] // CM_PROB_FAILED_POST_START
    [InlineData(10, false, false, false)] // CM_PROB_FAILED_START
    public void EnableStateClassification_MatchesDeviceManagerSemantics(
        uint problemCode, bool started, bool expectEnabled, bool expectDisabled)
    {
        var device = TestData.Managed(
            TestData.Pnp(@"PCI\VEN_8086&DEV_7A70&REV_11\3&11583659&0&A3", problemCode: problemCode, started: started),
            physical: true);

        Assert.Equal(expectEnabled, device.IsEnabled);
        Assert.Equal(expectDisabled, device.IsDisabled);
    }
}
