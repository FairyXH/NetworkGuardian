using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Windows.Native;
using static NetworkGuardian.Windows.Native.WlanApiNative;

namespace NetworkGuardian.Windows.Radio;

public sealed record RadioStateReadResult(bool? SoftwareOn, bool? HardwareOn, uint PhyCount, string? Detail);

/// <summary>
/// Native fallback for Wi-Fi radio state, used when <c>Windows.Devices.Radios</c> is not reachable
/// from an unpackaged process. It opens its own WLAN client handle so it never interferes with the
/// handle owned by <see cref="Wlan.NativeWifiManager"/>.
/// </summary>
public sealed class NativeRadioAccess : IDisposable
{
    private readonly ILogger<NativeRadioAccess> _logger;
    private readonly object _gate = new();
    private WlanClientHandle? _client;
    private bool _disposed;

    public NativeRadioAccess(ILogger<NativeRadioAccess>? logger = null)
    {
        _logger = logger ?? NullLogger<NativeRadioAccess>.Instance;
        Open();
    }

    private void Open()
    {
        var result = WlanOpenHandle(WlanClientVersionWindows7, IntPtr.Zero, out _, out var handle);
        if (result != ERROR_SUCCESS)
        {
            _logger.LogWarning("Native radio fallback could not open a WLAN handle: {Error}",
                Win32Error.Describe((int)result));
            return;
        }

        lock (_gate)
        {
            _client = new WlanClientHandle(handle);
        }
    }

    public RadioStateReadResult ReadRadioState()
    {
        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            return new RadioStateReadResult(null, null, 0, "no WLAN handle");
        }

        // The radio state opcode is per interface; use the first available interface.
        IntPtr listPointer = IntPtr.Zero;
        try
        {
            var enumResult = WlanEnumInterfaces(handle, IntPtr.Zero, out listPointer);
            if (enumResult != ERROR_SUCCESS)
            {
                return new RadioStateReadResult(null, null, 0, Win32Error.Describe((int)enumResult));
            }

            var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST_HEADER>(listPointer);
            if (header.dwNumberOfItems == 0)
            {
                return new RadioStateReadResult(null, null, 0, "no WLAN interfaces");
            }

            var itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
            var itemPointer = IntPtr.Add(listPointer, ListHeaderSize);
            var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(itemPointer);
            _ = itemSize;

            return QueryRadioState(handle, info.InterfaceGuid);
        }
        finally
        {
            if (listPointer != IntPtr.Zero)
            {
                WlanFreeMemory(listPointer);
            }
        }
    }

    private RadioStateReadResult QueryRadioState(IntPtr handle, Guid interfaceGuid)
    {
        IntPtr dataPointer = IntPtr.Zero;
        try
        {
            var result = WlanQueryInterface(
                handle, interfaceGuid, WlanIntfOpcodeRadioState, IntPtr.Zero, out var size, out dataPointer, out _);

            if (result != ERROR_SUCCESS || dataPointer == IntPtr.Zero)
            {
                return new RadioStateReadResult(null, null, 0, Win32Error.Describe((int)result));
            }

            var state = Marshal.PtrToStructure<WLAN_RADIO_STATE>(dataPointer);
            _ = size;

            if (state.dwNumberOfPhys == 0)
            {
                return new RadioStateReadResult(null, null, 0, "driver reported no PHYs");
            }

            var softwareOn = true;
            var hardwareOn = true;

            unsafe
            {
                for (var i = 0; i < Math.Min(state.dwNumberOfPhys, WLAN_MAX_PHY_INDEX); i++)
                {
                    var software = state.PhyRadioState[(i * 3) + 1];
                    var hardware = state.PhyRadioState[(i * 3) + 2];

                    softwareOn &= software == Dot11RadioStateOn;
                    hardwareOn &= hardware == Dot11RadioStateOn;
                }
            }

            return new RadioStateReadResult(softwareOn, hardwareOn, state.dwNumberOfPhys, null);
        }
        catch (Exception ex)
        {
            return new RadioStateReadResult(null, null, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (dataPointer != IntPtr.Zero)
            {
                WlanFreeMemory(dataPointer);
            }
        }
    }

    /// <summary>
    /// Attempts to change the software radio state through <c>WlanSetInterface</c>. Many drivers
    /// reject this, in which case the caller reports that Windows.Devices.Radios is required.
    /// </summary>
    public RadioOperationResult SetSoftwareRadioState(bool enabled)
    {
        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            return new RadioOperationResult { Success = false, Failure = "no WLAN handle" };
        }

        IntPtr listPointer = IntPtr.Zero;
        IntPtr statePointer = IntPtr.Zero;

        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out listPointer) != ERROR_SUCCESS)
            {
                return new RadioOperationResult { Success = false, Failure = "WlanEnumInterfaces failed" };
            }

            var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST_HEADER>(listPointer);
            if (header.dwNumberOfItems == 0)
            {
                return new RadioOperationResult { Success = false, Failure = "no WLAN interfaces" };
            }

            var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(IntPtr.Add(listPointer, ListHeaderSize));
            var current = QueryRadioState(handle, info.InterfaceGuid);
            var phyCount = Math.Max(1u, Math.Min(current.PhyCount == 0 ? 1u : current.PhyCount, WLAN_MAX_PHY_INDEX));

            var size = Marshal.SizeOf<WLAN_RADIO_STATE>();
            statePointer = Marshal.AllocHGlobal(size);

            var state = new WLAN_RADIO_STATE { dwNumberOfPhys = phyCount };
            unsafe
            {
                for (var i = 0; i < phyCount; i++)
                {
                    state.PhyRadioState[(i * 3) + 0] = (uint)i;
                    state.PhyRadioState[(i * 3) + 1] = enabled ? Dot11RadioStateOn : Dot11RadioStateOff;
                    state.PhyRadioState[(i * 3) + 2] = Dot11RadioStateUnknown;
                }
            }

            Marshal.StructureToPtr(state, statePointer, false);

            var result = WlanSetInterface(handle, info.InterfaceGuid, WlanIntfOpcodeRadioState,
                (uint)size, statePointer, IntPtr.Zero);

            if (result != ERROR_SUCCESS)
            {
                var message = result == ERROR_NOT_SUPPORTED
                    ? "The driver does not allow changing the software radio state through WlanSetInterface. " +
                      "Windows.Devices.Radios is required for this machine."
                    : Win32Error.Build("WlanSetInterface(radio_state)", (int)result);

                _logger.LogWarning("{Message}", message);
                return new RadioOperationResult
                {
                    Success = false,
                    AccessDenied = Win32Error.IsAccessDenied((int)result),
                    Failure = message,
                };
            }

            var after = QueryRadioState(handle, info.InterfaceGuid);
            var changed = after.SoftwareOn == enabled;
            _logger.LogInformation("WlanSetInterface(radio_state) {Result}; observed software state {State}",
                changed ? "succeeded" : "did not take effect", after.SoftwareOn);

            return new RadioOperationResult
            {
                Success = changed,
                StateChanged = changed,
                Failure = changed ? null : "the radio did not change state after WlanSetInterface",
            };
        }
        catch (Exception ex)
        {
            return new RadioOperationResult { Success = false, Failure = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            if (listPointer != IntPtr.Zero)
            {
                WlanFreeMemory(listPointer);
            }

            if (statePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(statePointer);
            }
        }
    }

    private IntPtr CurrentHandle()
    {
        lock (_gate)
        {
            return _client is { IsInvalid: false, IsClosed: false }
                ? _client.DangerousGetHandle()
                : IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
