using System.Runtime.InteropServices;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Native;
using NetworkGuardian.Windows.Radio;
using Xunit;
using static NetworkGuardian.Windows.Native.WlanApiNative;

namespace NetworkGuardian.Tests;

/// <summary>
/// The radio write path goes through <c>WlanSetInterface</c>, so the struct layout and the opcode
/// ordinals have to match <c>wlanapi.h</c> exactly; a wrong size silently corrupts the radio state.
/// </summary>
public sealed class WlanRadioStateLayoutTests
{
    [Fact]
    public void RadioStateStructs_MatchTheNativeSizes()
    {
        Assert.Equal(12, Marshal.SizeOf<WLAN_PHY_RADIO_STATE>());
        Assert.Equal(772, Marshal.SizeOf<WLAN_RADIO_STATE>());
        Assert.Equal(64u, WLAN_MAX_PHY_INDEX);
    }

    [Fact]
    public void RadioOpcodesAndStates_MatchTheSdkHeader()
    {
        // WLAN_INTF_OPCODE: radio_state is ordinal 4 (autoconf_start=0 ... media_streaming_mode=3).
        Assert.Equal(4u, WlanIntfOpcodeRadioState);

        // DOT11_RADIO_STATE: unknown, on, off.
        Assert.Equal(0u, Dot11RadioStateUnknown);
        Assert.Equal(1u, Dot11RadioStateOn);
        Assert.Equal(2u, Dot11RadioStateOff);

        // WLAN_NOTIFICATION_MSM value 7: the radio state change notification.
        Assert.Equal(7u, WlanNotificationMsmRadioStateChange);
    }

    [Fact]
    public void RadioStateBuffer_IsLaidOutAsPhyIndexSoftwareHardware()
    {
        var state = new WLAN_RADIO_STATE { dwNumberOfPhys = 2 };
        unsafe
        {
            state.PhyRadioState[1] = Dot11RadioStateOn;
            state.PhyRadioState[2] = Dot11RadioStateOff;
            state.PhyRadioState[4] = Dot11RadioStateOff;
            state.PhyRadioState[5] = Dot11RadioStateOn;

            Assert.Equal(Dot11RadioStateOn, state.PhyRadioState[(0 * 3) + 1]);
            Assert.Equal(Dot11RadioStateOff, state.PhyRadioState[(0 * 3) + 2]);
            Assert.Equal(Dot11RadioStateOff, state.PhyRadioState[(1 * 3) + 1]);
            Assert.Equal(Dot11RadioStateOn, state.PhyRadioState[(1 * 3) + 2]);
        }

        Assert.Equal(2u, state.dwNumberOfPhys);
    }
}

/// <summary>
/// <see cref="WifiRadioController"/> is the only radio implementation since WinRT was dropped, so its
/// mapping, read-back verification and change detection are locked here against a fake WLAN layer.
/// </summary>
public sealed class WifiRadioControllerTests
{
    [Fact]
    public async Task SoftwareRadioOff_IsReportedAsOff()
    {
        using var controller = new WifiRadioController(access: new FakeRadioAccess(softwareOn: false, hardwareOn: true));

        var snapshot = await controller.GetAsync(CancellationToken.None);

        Assert.Equal(RadioState.Off, snapshot.State);
        Assert.False(snapshot.IsOn);
        Assert.True(snapshot.IsAccessAllowed);
    }

    [Fact]
    public async Task HardwareSwitchOff_IsSurfacedAsAFailureReason()
    {
        using var controller = new WifiRadioController(access: new FakeRadioAccess(softwareOn: true, hardwareOn: false));

        var snapshot = await controller.GetAsync(CancellationToken.None);

        Assert.Equal(RadioState.On, snapshot.State);
        Assert.Contains("硬件", snapshot.FailureReason);
    }

    [Fact]
    public async Task UnreadableState_IsUnknownAndNotAccessAllowed()
    {
        using var controller = new WifiRadioController(access: new FakeRadioAccess(null, null, detail: "no WLAN handle"));

        var snapshot = await controller.GetAsync(CancellationToken.None);

        Assert.Equal(RadioState.Unknown, snapshot.State);
        Assert.False(snapshot.IsAccessAllowed);
        Assert.Equal("no WLAN handle", snapshot.FailureReason);
    }

    [Fact]
    public async Task StateChange_IsRaisedOnceWhenTheObservationChanges()
    {
        var fake = new FakeRadioAccess(softwareOn: false, hardwareOn: true);
        using var controller = new WifiRadioController(access: fake);

        var raised = 0;
        controller.StateChanged += (_, snapshot) =>
        {
            raised++;
            Assert.Equal(RadioState.On, snapshot.State);
        };

        await controller.GetAsync(CancellationToken.None);
        Assert.Equal(0, raised); // nothing observed before, so no transition yet

        fake.SoftwareOn = true;
        await controller.GetAsync(CancellationToken.None);
        Assert.Equal(1, raised);

        await controller.GetAsync(CancellationToken.None);
        Assert.Equal(1, raised); // unchanged observations do not raise again
    }

    [Fact]
    public async Task EnablingAnAlreadyEnabledRadio_DoesNotWrite()
    {
        var fake = new FakeRadioAccess(softwareOn: true, hardwareOn: true);
        using var controller = new WifiRadioController(access: fake);

        var result = await controller.SetEnabledAsync(true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.StateChanged);
        Assert.Equal(0, fake.WriteCount);
    }

    [Fact]
    public async Task EnablingAWriteRefusedByTheDriver_IsReportedAsAccessDenied()
    {
        var fake = new FakeRadioAccess(softwareOn: false, hardwareOn: true)
        {
            NextWrite = new RadioOperationResult { Success = false, AccessDenied = true, Failure = "denied" },
        };
        using var controller = new WifiRadioController(access: fake);

        var result = await controller.SetEnabledAsync(true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.AccessDenied);
        Assert.Contains("ERROR_ACCESS_DENIED", result.Failure);
    }

    [Fact]
    public async Task EnablingTurnsOnEveryAdapterThatIsSwitchedOff()
    {
        var fake = new FakeRadioAccess(softwareOn: false, hardwareOn: true);
        var onGuid = Guid.NewGuid();
        var offGuid = Guid.NewGuid();
        fake.Instances.AddRange(
        [
            new RadioInstanceInfo(onGuid, "WLAN 3", RadioDeviceState.On),
            new RadioInstanceInfo(offGuid, "WLAN", RadioDeviceState.SoftwareOff),
        ]);

        using var controller = new WifiRadioController(access: fake);

        var result = await controller.SetEnabledAsync(true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.StateChanged);

        // Only the adapter that was switched off is touched - and it is the per-adapter switch that
        // makes this work at all, since the WLAN opcode only ever saw the first interface.
        Assert.Equal(new[] { offGuid }, fake.RadioOnRequests);
    }

    [Fact]
    public async Task EnablingReportsTheAdaptersThatRefused()
    {
        var fake = new FakeRadioAccess(softwareOn: false, hardwareOn: true);
        fake.Instances.Add(new RadioInstanceInfo(Guid.NewGuid(), "WLAN", RadioDeviceState.SoftwareOff));
        fake.NextInstanceResult = new RadioSetResult(false, false, "hardware switch is off");

        using var controller = new WifiRadioController(access: fake);

        var result = await controller.SetEnabledAsync(true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.StateChanged);
        Assert.Contains("hardware switch is off", result.Failure);
    }

    [Fact]
    public async Task AnAdapterSwitchedOffInWindowsMakesTheRadioStateOff()
    {
        var fake = new FakeRadioAccess(softwareOn: true, hardwareOn: true);
        fake.Instances.Add(new RadioInstanceInfo(Guid.NewGuid(), "WLAN", RadioDeviceState.SoftwareOff));

        using var controller = new WifiRadioController(access: fake);

        var snapshot = await controller.GetAsync(CancellationToken.None);

        // The first interface reports "on"; without the per-adapter view this would read as On.
        Assert.Equal(RadioState.Off, snapshot.State);
        Assert.True(snapshot.IsAccessAllowed);
    }

    private sealed class FakeRadioAccess : IRadioStateAccess
    {
        public FakeRadioAccess(bool? softwareOn, bool? hardwareOn, string? detail = null)
        {
            SoftwareOn = softwareOn;
            HardwareOn = hardwareOn;
            Detail = detail;
        }

        public bool? SoftwareOn { get; set; }

        public bool? HardwareOn { get; }

        public string? Detail { get; }

        public int WriteCount { get; private set; }

        public RadioOperationResult? NextWrite { get; init; }

        public List<RadioInstanceInfo> Instances { get; } = [];

        public List<Guid> RadioOnRequests { get; } = [];

        public RadioSetResult? NextInstanceResult { get; set; }

        public RadioStateReadResult ReadRadioState() =>
            new(SoftwareOn, HardwareOn, SoftwareOn is null ? 0u : 1u, Detail);

        public RadioOperationResult SetSoftwareRadioState(bool enabled)
        {
            WriteCount++;

            if (NextWrite is { } refusal)
            {
                return refusal;
            }

            SoftwareOn = enabled;
            return new RadioOperationResult { Success = true, StateChanged = true };
        }

        public IReadOnlyList<RadioInstanceInfo> ReadRadioInstances() => Instances;

        public RadioSetResult SetInstanceRadioOn(Guid interfaceGuid)
        {
            RadioOnRequests.Add(interfaceGuid);

            if (NextInstanceResult is { } refusal)
            {
                return refusal;
            }

            var index = Instances.FindIndex(instance => instance.InterfaceGuid == interfaceGuid);
            if (index >= 0)
            {
                Instances[index] = Instances[index] with { State = RadioDeviceState.On };
            }

            return new RadioSetResult(true, true, null);
        }
    }
}
