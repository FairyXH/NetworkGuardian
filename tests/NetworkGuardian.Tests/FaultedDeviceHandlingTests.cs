using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// A device that failed to start is broken, not disabled: enabling it changes nothing. The only PnP
/// action with a real chance is a restart (disable + enable), which is what the engine asks for -
/// bounded per device - while still making the situation visible when it is turned off or throttled.
/// </summary>
public sealed class FaultedDeviceHandlingTests
{
    private static GuardianInput Input(
        Core.Configuration.GuardianConfig config,
        IReadOnlyList<ManagedDevice> devices,
        bool internetOnline = false) => new()
    {
        Now = TestData.Now,
        Config = config,
        GlobalProbe = internetOnline ? TestData.OnlineProbe() : TestData.OfflineProbe(),
        Interfaces = Array.Empty<InterfaceRuntimeState>(),
        WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
        Devices = devices,
        Radio = TestData.RadioOn,
    };

    [Fact]
    public void DeviceWithDriverFault_IsRestartedNotEnabled()
    {
        var config = TestData.Config(c => c.General.AutoRestartFaultedWifiDevices = true);
        var engine = new GuardianDecisionEngine(config);

        // Problem code 43 = CM_PROB_FAILED_POST_START: the device is present but the driver faulted.
        var faulted = TestData.Managed(
            TestData.Pnp(@"PCI\VEN_8086&DEV_7A70&SUBSYS_40708086&REV_11\3&11583659&0&A3", problemCode: 43, started: false),
            physical: true);

        Assert.True(faulted.Classification.IsPhysical);
        Assert.False(faulted.IsEnabled);
        Assert.False(faulted.IsDisabled);

        var decision = engine.Evaluate(Input(config, new[] { faulted }));

        // Enabling a faulted device is pointless by definition, so it must never be requested.
        Assert.DoesNotContain(decision.Actions, a => a is EnableWifiDeviceAction);

        var restart = Assert.Single(decision.Actions.OfType<RestartWifiDeviceAction>());
        Assert.Equal(43u, restart.ProblemCode);
        Assert.Equal(faulted.Record.DeviceInstanceId, restart.DeviceInstanceId);

        // The round ends with a settle wait: the device has to come back before it is evaluated again.
        Assert.Contains(decision.Actions, a => a is WaitAction);
    }

    [Fact]
    public void DeviceWithDriverFault_IsLeftAloneWhenRestartIsDisabled()
    {
        var config = TestData.Config(c => c.General.AutoRestartFaultedWifiDevices = false);
        var engine = new GuardianDecisionEngine(config);

        var faulted = TestData.Managed(
            TestData.Pnp(@"PCI\VEN_8086&DEV_7A70&SUBSYS_40708086&REV_11\3&11583659&0&A3", problemCode: 10, started: false),
            physical: true);

        var decision = engine.Evaluate(Input(config, new[] { faulted }));

        Assert.DoesNotContain(decision.Actions, a => a is EnableWifiDeviceAction or RestartWifiDeviceAction);
        Assert.Contains(decision.Notes, n => n.Contains("10", StringComparison.Ordinal));
    }

    [Fact]
    public void DeviceWithDriverFault_IsOnlyRestartedOncePerHourWindow()
    {
        var config = TestData.Config(c => c.General.AutoRestartFaultedWifiDevices = true);
        var engine = new GuardianDecisionEngine(config);

        var faulted = TestData.Managed(
            TestData.Pnp(@"PCI\VEN_8086&DEV_7A70&REV_11\3&11583659&0&A3", problemCode: 10, started: false),
            physical: true);

        var first = engine.Evaluate(Input(config, new[] { faulted }));
        Assert.Single(first.Actions.OfType<RestartWifiDeviceAction>());

        // The per-device limiter (minInterval 120s) must stop the next cycle from touching the machine
        // again: a repair attempt that repeats every 20 seconds would be a state change storm.
        var second = engine.Evaluate(Input(config, new[] { faulted }));
        Assert.Empty(second.Actions.OfType<RestartWifiDeviceAction>());
        Assert.Contains(second.Notes, n => n.Contains("problemCode=10", StringComparison.Ordinal));
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

        var decision = engine.Evaluate(Input(config, new[] { disabled }));

        Assert.Contains(decision.Actions, a => a is EnableWifiDeviceAction);
        Assert.DoesNotContain(decision.Actions, a => a is RestartWifiDeviceAction);
    }

    [Fact]
    public void AvailableInternet_PreventsIntrusiveDeviceRecovery()
    {
        var config = TestData.Config(c =>
        {
            c.General.AutoEnableWifiDevices = true;
            c.General.AutoRestartFaultedWifiDevices = true;
        });
        var engine = new GuardianDecisionEngine(config);
        var disabled = TestData.Managed(
            TestData.Pnp(@"USB\VID_0E8D&PID_7961\000000000", problemCode: 22, started: false),
            physical: true);
        var faulted = TestData.Managed(
            TestData.Pnp(@"PCI\VEN_8086&DEV_7A70\FAULTED", problemCode: 43, started: false),
            physical: true);

        var decision = engine.Evaluate(Input(config, new[] { disabled, faulted }, internetOnline: true));

        Assert.DoesNotContain(decision.Actions,
            action => action is EnableWifiDeviceAction or RestartWifiDeviceAction);
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
