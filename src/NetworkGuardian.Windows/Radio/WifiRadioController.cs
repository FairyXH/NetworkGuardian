using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Windows.Radio;

/// <summary>
/// Controls the Windows-wide Wi-Fi switch.
/// </summary>
/// <remarks>
/// The only implementation is Native Wi-Fi (<c>wlan_intf_opcode_radio_state</c> over
/// <c>WlanQueryInterface</c> / <c>WlanSetInterface</c>). The previous implementation used
/// <c>Windows.Devices.Radios</c>, which is not usable from a Native AOT process: CsWinRT is not
/// AOT compatible and the WinRT projection adds ~24 MB to the deployment. The WLAN opcode drives the
/// same software radio state as the Windows 11 quick-settings Wi-Fi tile, so behaviour is unchanged
/// for the recovery engine; where a firmware/hardware switch or a policy owns the radio the write is
/// reported as a failure instead of a false success (the requested state is read back and verified).
/// </remarks>
public sealed class WifiRadioController : IWifiRadioController, IDisposable
{
    private readonly ILogger<WifiRadioController> _logger;
    private readonly IRadioStateAccess _native;
    private readonly object _gate = new();
    private RadioState _lastObserved = RadioState.Unknown;
    private bool _disposed;

    public WifiRadioController(
        ILogger<WifiRadioController>? logger = null,
        IRadioStateAccess? access = null,
        ILogger<NativeRadioAccess>? nativeLogger = null)
    {
        _logger = logger ?? NullLogger<WifiRadioController>.Instance;
        _native = access ?? new NativeRadioAccess(nativeLogger);
    }

    public event EventHandler<WifiRadioSnapshot>? StateChanged;

    public Task<WifiRadioSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = Read();

        // The periodic sweep is the authoritative source, so a state change is detected by comparing
        // consecutive observations instead of relying on a push notification.
        RadioState previous;
        lock (_gate)
        {
            previous = _lastObserved;
            _lastObserved = snapshot.State;
        }

        if (previous != RadioState.Unknown && previous != snapshot.State)
        {
            _logger.LogInformation("Wi-Fi radio state changed from {Previous} to {State}", previous, snapshot.State);
            try
            {
                StateChanged?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A radio state change handler failed");
            }
        }

        return Task.FromResult(snapshot);
    }

    public Task<RadioOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Turning Wi-Fi off is a single system-wide write; turning it on has to visit every adapter,
        // because Windows lets each one be switched off on its own.
        return Task.FromResult(enabled ? EnableAllAdapters() : SetSoftwareRadioState(false));
    }

    /// <summary>
    /// Turns every adapter's software radio back on.
    /// </summary>
    /// <remarks>
    /// Windows Settings shows one Wi-Fi switch per adapter. The WLAN opcode path only ever touched the
    /// first interface, which is why an adapter switched off in Settings never came back on - and why
    /// "the radio is on" was reported while a second adapter was still off. Every adapter is visited
    /// here, and a partial result says exactly which one refused.
    /// </remarks>
    private RadioOperationResult EnableAllAdapters()
    {
        var instances = _native.ReadRadioInstances();
        if (instances.Count == 0)
        {
            // No radio manager (or no adapters): fall back to the WLAN opcode path.
            return SetSoftwareRadioState(true);
        }

        var candidates = instances.Where(instance => !instance.IsOn).ToList();
        if (candidates.Count == 0)
        {
            return new RadioOperationResult { Success = true, StateChanged = false };
        }

        var changed = 0;
        var failures = new List<string>();

        foreach (var instance in candidates)
        {
            var result = _native.SetInstanceRadioOn(instance.InterfaceGuid);
            if (result.Success)
            {
                if (result.StateChanged)
                {
                    changed++;
                }
            }
            else
            {
                failures.Add($"{instance.Name}: {result.Failure}");
            }
        }

        if (failures.Count == 0)
        {
            lock (_gate)
            {
                _lastObserved = RadioState.On;
            }

            _logger.LogInformation("Turned the software radio of {Count} Wi-Fi adapter(s) back on", changed);
            return new RadioOperationResult { Success = true, StateChanged = changed > 0 };
        }

        var message = string.Join("；", failures);

        if (changed > 0)
        {
            // Partial success must not read as a clean failure either: report both sides.
            return new RadioOperationResult
            {
                Success = false,
                StateChanged = true,
                Failure = $"已打开 {changed} 块网卡的无线电，但仍有失败：{message}",
            };
        }

        _logger.LogWarning("Turning the Wi-Fi radios on failed: {Failure}", message);
        return new RadioOperationResult { Success = false, Failure = message };
    }

    /// <summary>Writes the Windows-wide software radio state through the WLAN opcode.</summary>
    private RadioOperationResult SetSoftwareRadioState(bool enabled)
    {
        var before = _native.ReadRadioState();
        if (before.SoftwareOn == enabled)
        {
            return new RadioOperationResult { Success = true, StateChanged = false };
        }

        var result = _native.SetSoftwareRadioState(enabled);

        if (result.Success)
        {
            lock (_gate)
            {
                _lastObserved = enabled ? RadioState.On : RadioState.Off;
            }

            return result;
        }

        if (result.AccessDenied)
        {
            return new RadioOperationResult
            {
                Success = false,
                AccessDenied = true,
                Failure = "打开/关闭 Wi-Fi 无线电被拒绝（ERROR_ACCESS_DENIED）。可能是策略、硬件开关或权限限制。",
            };
        }

        _logger.LogWarning("Setting the Wi-Fi radio to {Desired} failed: {Failure}", enabled, result.Failure);
        return result;
    }

    private WifiRadioSnapshot Read()
    {
        // One switch per adapter; the system-wide Wi-Fi switch is simply all of them. Reading them one
        // by one is also the only view that notices an adapter switched off in Windows Settings.
        var instances = _native.ReadRadioInstances();
        if (instances.Count > 0)
        {
            var off = instances.Where(instance => !instance.IsOn).ToList();
            var hardwareOff = off.Where(instance => instance.IsHardwareOff).ToList();

            return new WifiRadioSnapshot
            {
                State = off.Count == 0 ? RadioState.On : RadioState.Off,
                IsAccessAllowed = true,
                Name = $"Windows 无线电管理器（{instances.Count} 块无线网卡）",
                FailureReason = hardwareOff.Count > 0
                    ? $"网卡「{hardwareOff[0].Name}」的无线电被硬件开关关闭，软件无法打开。"
                    : null,
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        var native = _native.ReadRadioState();

        var state = native.SoftwareOn switch
        {
            true => RadioState.On,
            false => RadioState.Off,
            null => RadioState.Unknown,
        };

        string? failure = native.SoftwareOn switch
        {
            true when native.HardwareOn == false =>
                "硬件无线开关处于关闭状态；软件无线电已打开，但无线电仍不可用。",
            true => null,
            false => null,
            null => native.Detail ?? "无法读取 Wi-Fi 无线电状态。",
        };

        return new WifiRadioSnapshot
        {
            State = state,
            IsAccessAllowed = native.SoftwareOn is not null,
            Name = "Native Wi-Fi (wlanapi)",
            FailureReason = failure,
            ObservedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_native as IDisposable)?.Dispose();
    }
}
