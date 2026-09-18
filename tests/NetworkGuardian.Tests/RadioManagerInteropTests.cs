using NetworkGuardian.Windows.Radio;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// Hardware tests for the Windows Radio Manager projection - the API behind the per-adapter Wi-Fi
/// switches in Windows Settings. They are tolerant on machines without Wi-Fi adapters but must show
/// the real adapters, and a state change to the *current* state is safe to run and proves the whole
/// activation/read/write chain.
/// </summary>
public sealed class RadioManagerInteropTests
{
    [Fact]
    public void ReadInstances_ReportsWiFiRadiosWithInterfaceGuids()
    {
        var instances = RadioManagerInterop.ReadInstances(out var failure);

        if (instances.Count == 0)
        {
            // No radio on this machine: the call must explain itself rather than fail silently.
            Assert.False(string.IsNullOrWhiteSpace(failure));
            return;
        }

        Assert.Null(failure);
        Assert.All(instances, instance => Assert.False(string.IsNullOrWhiteSpace(instance.Name)));

        // The instance signature is the WLAN interface GUID, which is what lines a radio up with an
        // adapter - a radio without one could never be matched.
        Assert.Contains(instances, instance => instance.InterfaceGuid != Guid.Empty);
    }

    [Fact]
    public void SetRadioOn_OnARadioThatIsAlreadyOn_IsANoOp()
    {
        var instances = RadioManagerInterop.ReadInstances(out _);
        var enabled = instances.FirstOrDefault(instance => instance.IsOn);

        if (enabled is null || string.IsNullOrWhiteSpace(enabled.Name))
        {
            return; // no radio that is already on
        }

        var result = RadioManagerInterop.SetRadioOn(enabled.InterfaceGuid, out var failure);

        Assert.True(result.Success, failure);
        Assert.False(result.StateChanged);
    }

    [Fact]
    public void SetRadioOn_ForAnUnknownInterface_FailsWithAReason()
    {
        var result = RadioManagerInterop.SetRadioOn(Guid.NewGuid(), out var failure);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Fact]
    public void HardwareOffRadios_AreReportedAsUnchangeableBySoftware()
    {
        var instances = RadioManagerInterop.ReadInstances(out _);

        foreach (var instance in instances.Where(i => i.IsHardwareOff))
        {
            var result = RadioManagerInterop.SetRadioOn(instance.InterfaceGuid, out var failure);

            // A hardware switch is beyond software control: the app must say so instead of retrying.
            Assert.False(result.Success);
            Assert.Contains("hardware switch", failure!);
        }
    }
}
