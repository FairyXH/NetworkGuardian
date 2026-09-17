using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Models;
using Radios = global::Windows.Devices.Radios;

namespace NetworkGuardian.Windows.Radio;

/// <summary>
/// Controls the Windows-wide Wi-Fi switch.
/// </summary>
/// <remarks>
/// The primary implementation uses <c>Windows.Devices.Radios</c> as required by the project brief.
/// Unpackaged desktop applications can be refused access to that WinRT surface, so a Native Wi-Fi
/// fallback (<c>wlan_intf_opcode_radio_state</c>) is used to at least observe and, where the driver
/// allows it, change the software radio state.
/// </remarks>
public sealed class WifiRadioController : IWifiRadioController, IDisposable
{
    private readonly ILogger<WifiRadioController> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _changeSignal = new(0, 64);
    private bool _disposed;
    private bool _winRtUnavailable;
    private bool _accessRequested;
    private Radios.Radio? _wifiRadio;

    public WifiRadioController(ILogger<WifiRadioController>? logger = null)
    {
        _logger = logger ?? NullLogger<WifiRadioController>.Instance;
    }

    public event EventHandler<WifiRadioSnapshot>? StateChanged;

    /// <summary>Native access used when the WinRT surface is unavailable.</summary>
    public NativeRadioAccess? NativeFallback { get; set; }

    public async Task<WifiRadioSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_winRtUnavailable)
        {
            try
            {
                var radio = await FindWifiRadioAsync(cancellationToken).ConfigureAwait(false);
                if (radio is null)
                {
                    return new WifiRadioSnapshot
                    {
                        State = RadioState.Unknown,
                        FailureReason = "Windows did not report a Wi-Fi radio device. The machine may have no " +
                                        "WLAN hardware, or the WLAN AutoConfig service may be stopped.",
                        ObservedAtUtc = DateTimeOffset.UtcNow,
                    };
                }

                return new WifiRadioSnapshot
                {
                    State = MapState(radio.State),
                    IsAccessAllowed = true,
                    Name = radio.Name,
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                };
            }
            catch (Exception ex)
            {
                _winRtUnavailable = true;
                _logger.LogWarning(
                    "Windows.Devices.Radios is not usable from this process ({Type}: {Message}); " +
                    "falling back to the Native Wi-Fi radio state API.",
                    ex.GetType().Name, ex.Message);
            }
        }

        return ReadNative();
    }

    public async Task<RadioOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_winRtUnavailable)
        {
            try
            {
                var radio = await FindWifiRadioAsync(cancellationToken).ConfigureAwait(false);
                if (radio is null)
                {
                    return new RadioOperationResult
                    {
                        Success = false,
                        Failure = "No Wi-Fi radio was reported by Windows.",
                    };
                }

                if (!await EnsureAccessAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new RadioOperationResult
                    {
                        Success = false,
                        AccessDenied = true,
                        Failure = "Access to the Wi-Fi radio was denied. Check Windows privacy settings and " +
                                  "whether a policy or a hardware switch controls the radio.",
                    };
                }

                var desired = enabled ? Radios.RadioState.On : Radios.RadioState.Off;
                var before = radio.State;

                if (before == desired)
                {
                    return new RadioOperationResult { Success = true, StateChanged = false };
                }

                await radio.SetStateAsync(desired).AsTask(cancellationToken).ConfigureAwait(false);

                // SetStateAsync can report success while the radio never changes (hardware switch,
                // airplane mode, group policy). Always verify the real state afterwards.
                var after = await ReadStateAfterChangeAsync(radio, cancellationToken).ConfigureAwait(false);
                var changed = after == desired;

                if (!changed)
                {
                    _logger.LogWarning(
                        "The Wi-Fi radio did not reach the requested state {Desired} (observed {Observed}). " +
                        "A hardware switch, Airplane mode or a system policy is likely in control.",
                        desired, after);
                }

                return new RadioOperationResult
                {
                    Success = changed,
                    StateChanged = changed,
                    Failure = changed
                        ? null
                        : $"Requested {desired} but the radio reports {after}. " +
                          "Check the hardware wireless switch or Airplane mode.",
                };
            }
            catch (Exception ex)
            {
                _winRtUnavailable = true;
                _logger.LogWarning(ex, "Windows.Devices.Radios failed while setting the radio state");
            }
        }

        return SetNative(enabled);
    }

    private WifiRadioSnapshot ReadNative()
    {
        if (NativeFallback is null)
        {
            return new WifiRadioSnapshot
            {
                State = RadioState.Unknown,
                FailureReason = "Windows.Devices.Radios is unavailable and no native radio access was supplied.",
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        var native = NativeFallback.ReadRadioState();
        return new WifiRadioSnapshot
        {
            State = native.SoftwareOn switch
            {
                true => RadioState.On,
                false => RadioState.Off,
                null => RadioState.Unknown,
            },
            IsAccessAllowed = true,
            Name = "native fallback",
            FailureReason = native.Detail,
            ObservedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private RadioOperationResult SetNative(bool enabled)
    {
        if (NativeFallback is null)
        {
            return new RadioOperationResult
            {
                Success = false,
                Failure = "Neither Windows.Devices.Radios nor the native radio API is available.",
            };
        }

        var result = NativeFallback.SetSoftwareRadioState(enabled);
        if (!result.Success)
        {
            _logger.LogWarning("Native radio change failed: {Failure}", result.Failure);
        }

        return result;
    }

    private async Task<Radios.Radio?> FindWifiRadioAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_wifiRadio is not null)
            {
                return _wifiRadio;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var radios = await Radios.Radio.GetRadiosAsync().AsTask(cancellationToken).ConfigureAwait(false);
        var wifi = radios.FirstOrDefault(r => r.Kind == Radios.RadioKind.WiFi);

        if (wifi is null)
        {
            return null;
        }

        wifi.StateChanged += OnRadioStateChanged;

        lock (_gate)
        {
            _wifiRadio = wifi;
        }

        _logger.LogInformation("Wi-Fi radio '{Name}' discovered in state {State}", wifi.Name, wifi.State);
        return wifi;
    }

    private void OnRadioStateChanged(Radios.Radio sender, object args)
    {
        // WinRT raises this on a background thread; never block and never throw.
        try
        {
            var snapshot = new WifiRadioSnapshot
            {
                State = MapState(sender.State),
                IsAccessAllowed = true,
                Name = sender.Name,
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };

            try
            {
                _changeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Consumers poll the state anyway.
            }

            StateChanged?.Invoke(this, snapshot);
        }
        catch (Exception)
        {
            // Ignore: the periodic sweep is the authoritative source.
        }
    }

    /// <summary>Waits for a radio state change event or the timeout.</summary>
    public Task<bool> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _changeSignal.WaitAsync(timeout, cancellationToken);

    private async Task<bool> EnsureAccessAsync(CancellationToken cancellationToken)
    {
        if (_accessRequested)
        {
            return true;
        }

        try
        {
            var status = await Radios.Radio.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false);
            _accessRequested = true;

            if (status == Radios.RadioAccessStatus.Allowed)
            {
                return true;
            }

            _logger.LogWarning("Radio.RequestAccessAsync returned {Status}", status);
            return false;
        }
        catch (Exception ex)
        {
            // Unpackaged applications frequently cannot raise the consent prompt. The state change is
            // still attempted so a genuinely permitted environment keeps working.
            _logger.LogInformation(
                "Radio.RequestAccessAsync could not be used ({Type}); attempting the state change anyway",
                ex.GetType().Name);
            return true;
        }
    }

    private async Task<Radios.RadioState> ReadStateAfterChangeAsync(
        Radios.Radio radio,
        CancellationToken cancellationToken)
    {
        // Give the radio stack a moment, then confirm the real state.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            try
            {
                var radios = await Radios.Radio.GetRadiosAsync().AsTask(cancellationToken).ConfigureAwait(false);
                var wifi = radios.FirstOrDefault(r => r.Kind == Radios.RadioKind.WiFi);
                if (wifi is not null)
                {
                    return wifi.State;
                }
            }
            catch (Exception)
            {
                return radio.State;
            }
        }

        return radio.State;
    }

    private static RadioState MapState(Radios.RadioState state) => state switch
    {
        Radios.RadioState.On => RadioState.On,
        Radios.RadioState.Off => RadioState.Off,
        Radios.RadioState.Disabled => RadioState.Disabled,
        _ => RadioState.Unknown,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Radios.Radio? radio;
        lock (_gate)
        {
            radio = _wifiRadio;
            _wifiRadio = null;
        }

        if (radio is not null)
        {
            try
            {
                radio.StateChanged -= OnRadioStateChanged;
            }
            catch (Exception)
            {
                // Nothing useful to do while disposing.
            }
        }

        _changeSignal.Dispose();
    }
}
