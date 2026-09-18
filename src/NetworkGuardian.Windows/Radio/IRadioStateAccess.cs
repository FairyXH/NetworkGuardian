namespace NetworkGuardian.Windows.Radio;

/// <summary>
/// The Native Wi-Fi radio state surface used by <see cref="WifiRadioController"/>.
/// </summary>
/// <remarks>
/// Extracted so the state mapping, the read-back verification and the change detection can be unit
/// tested without touching the machine's real radio.
/// </remarks>
public interface IRadioStateAccess
{
    /// <summary>Reads the software/hardware radio state of the first WLAN interface.</summary>
    RadioStateReadResult ReadRadioState();

    /// <summary>Writes the software radio state and verifies the result.</summary>
    Core.Abstractions.RadioOperationResult SetSoftwareRadioState(bool enabled);

    /// <summary>
    /// Reads every Wi-Fi radio instance - one per WLAN adapter - which is what Windows Settings shows
    /// as a Wi-Fi switch per adapter.
    /// </summary>
    IReadOnlyList<RadioInstanceInfo> ReadRadioInstances();

    /// <summary>Turns one adapter's software radio back on and verifies it.</summary>
    RadioSetResult SetInstanceRadioOn(Guid interfaceGuid);
}
