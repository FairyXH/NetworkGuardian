using System.Runtime.InteropServices;

namespace NetworkGuardian.Windows.Radio;

/// <summary>Adapter radio state, matching <c>DEVICE_RADIO_STATE</c> from <c>um/RadioMgr.h</c>.</summary>
public enum RadioDeviceState
{
    /// <summary>Radio on.</summary>
    On = 0,

    /// <summary>Switched off in software - the per-adapter Wi-Fi switch in Windows Settings.</summary>
    SoftwareOff = 1,

    /// <summary>Switched off by a hardware switch; software cannot turn it back on.</summary>
    HardwareOff = 2,

    SoftwareAndHardwareOff = 3,

    HardwareOnUncontrollable = 4,

    Invalid = 5,
}

/// <summary>One radio instance: the per-adapter Wi-Fi switch in Windows Settings.</summary>
public sealed record RadioInstanceInfo(Guid InterfaceGuid, string Name, RadioDeviceState State)
{
    public bool IsOn => State == RadioDeviceState.On;

    /// <summary>Off because Windows/the user switched it off in software - this can be turned back on.</summary>
    public bool IsSoftwareOff => State is RadioDeviceState.SoftwareOff or RadioDeviceState.SoftwareAndHardwareOff;

    /// <summary>Off because of a hardware switch - software cannot change it.</summary>
    public bool IsHardwareOff => State is RadioDeviceState.HardwareOff or RadioDeviceState.SoftwareAndHardwareOff;

    public override string ToString() => $"{Name} [{InterfaceGuid:D}] {State}";
}

/// <summary>Outcome of a radio state change.</summary>
public readonly record struct RadioSetResult(bool Success, bool StateChanged, string? Failure);

/// <summary>
/// The Windows Radio Manager API (<c>um/RadioMgr.h</c>) - the only way to control an individual
/// adapter's Wi-Fi switch.
/// </summary>
/// <remarks>
/// <para>
/// Windows Settings exposes a Wi-Fi switch per adapter, which maps to
/// <c>IRadioInstance::GetRadioState/SetRadioState</c>. Two other routes do not work:
/// <c>wlan_intf_opcode_radio_state</c> is rejected with ERROR_INVALID_PARAMETER (87) by the drivers
/// tested here (two different USB adapters), and the WinRT <c>Windows.Devices.Radios</c> projection
/// cannot be used from a Native AOT build (CsWinRT is not AOT compatible; driving the raw WinRT ABI by
/// hand fails because the returned <c>IAsyncOperation</c> pointer does not expose the inherited
/// <c>IAsyncInfo</c> members in its vtable).
/// </para>
/// <para>
/// This API is plain Win32 COM: synchronous, and its coclass is not declared in the SDK header, so the
/// CLSID below was found through the registry's class descriptions and verified by activation.
/// </para>
/// </remarks>
public static class RadioManagerInterop
{
    // Registry display name "Wlan Radio Manager"; implements IMediaRadioManager for Wi-Fi adapters.
    private static readonly Guid ClsidWlanRadioManager = new("833A69FB-5E17-4893-85A5-1EF469217372");

    // MIDL_INTERFACE("6CFDCAB5-FC47-42A5-9241-074B58830E73") IMediaRadioManager : IUnknown
    private static readonly Guid IidMediaRadioManager = new("6CFDCAB5-FC47-42A5-9241-074B58830E73");

    private const uint CoInitMultithreaded = 0x0;
    private const uint ClsctxAll = 0x17;
    private const int RadioStateOn = 0;
    private const uint SetRadioTimeoutSeconds = 5;

    /// <summary>Optional diagnostic sink for the ABI calls (hardware tests and QA tools use it).</summary>
    public static Action<string>? Trace { get; set; }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint clsctx, in Guid iid, out IntPtr instance);

    [DllImport("oleaut32.dll")]
    private static extern void SysFreeString(IntPtr bstr);

    /// <summary>Reads every Wi-Fi radio instance (one per WLAN adapter).</summary>
    public static IReadOnlyList<RadioInstanceInfo> ReadInstances(out string? failure)
    {
        var reason = (string?)null;
        var instances = RunInMta(() => ReadInstancesCore(out reason));
        failure = reason;
        return instances;
    }

    /// <summary>
    /// Turns one adapter's software radio back on - the fix for "the adapter is switched off in
    /// Windows Settings". Fails with an explanation when the switch is a hardware switch.
    /// </summary>
    public static RadioSetResult SetRadioOn(Guid interfaceGuid, out string? failure)
    {
        var reason = (string?)null;
        var result = RunInMta(() => SetRadioOnCore(interfaceGuid, out reason));
        failure = reason;
        return result;
    }

    private static List<RadioInstanceInfo> ReadInstancesCore(out string? failure)
    {
        failure = null;
        var results = new List<RadioInstanceInfo>();

        using var apartment = new ComApartment();
        if (!apartment.Ok)
        {
            failure = apartment.Failure;
            return results;
        }

        if (!TryCreateManager(out var manager, out failure))
        {
            return results;
        }

        try
        {
            if (!TryGetCollection(manager, out var collection, out failure))
            {
                return results;
            }

            try
            {
                if (GetCount(collection, out var count) < 0)
                {
                    failure = "IRadioInstanceCollection.GetCount failed";
                    return results;
                }

                for (uint i = 0; i < count; i++)
                {
                    if (GetAt(collection, i, out var instance) < 0 || instance == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        if (TryReadInstance(instance, out var info))
                        {
                            results.Add(info);
                        }
                    }
                    finally
                    {
                        _ = Marshal.Release(instance);
                    }
                }
            }
            finally
            {
                _ = Marshal.Release(collection);
            }
        }
        finally
        {
            _ = Marshal.Release(manager);
        }

        return results;
    }

    private static RadioSetResult SetRadioOnCore(Guid interfaceGuid, out string? failure)
    {
        failure = null;

        using var apartment = new ComApartment();
        if (!apartment.Ok)
        {
            failure = apartment.Failure;
            return new RadioSetResult(false, false, failure);
        }

        if (!TryCreateManager(out var manager, out failure))
        {
            return new RadioSetResult(false, false, failure);
        }

        try
        {
            if (!TryGetCollection(manager, out var collection, out failure))
            {
                return new RadioSetResult(false, false, failure);
            }

            try
            {
                if (GetCount(collection, out var count) < 0)
                {
                    failure = "IRadioInstanceCollection.GetCount failed";
                    return new RadioSetResult(false, false, failure);
                }

                for (uint i = 0; i < count; i++)
                {
                    if (GetAt(collection, i, out var instance) < 0 || instance == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        if (!TryReadInstance(instance, out var info) || info.InterfaceGuid != interfaceGuid)
                        {
                            continue;
                        }

                        if (info.IsOn)
                        {
                            return new RadioSetResult(true, false, null);
                        }

                        if (info.IsHardwareOff)
                        {
                            failure = $"'{info.Name}' is switched off by its hardware switch; software cannot turn it back on";
                            return new RadioSetResult(false, false, failure);
                        }

                        var hr = SetRadioState(instance, RadioStateOn, SetRadioTimeoutSeconds);
                        Trace?.Invoke($"IRadioInstance.SetRadioState({info.Name}, on) -> 0x{hr:X8}");
                        if (hr < 0)
                        {
                            failure = $"IRadioInstance.SetRadioState failed: 0x{hr:X8}";
                            return new RadioSetResult(false, false, failure);
                        }

                        // Read back instead of trusting the call: the switch either moved or it did not.
                        if (!TryReadInstance(instance, out var after) || !after.IsOn)
                        {
                            failure = $"'{info.Name}' did not turn on (state {after.State}); " +
                                      "the adapter's radio could not be enabled";
                            return new RadioSetResult(false, false, failure);
                        }

                        Trace?.Invoke($"IRadioInstance.SetRadioState({info.Name}) -> now on");
                        return new RadioSetResult(true, true, null);
                    }
                    finally
                    {
                        _ = Marshal.Release(instance);
                    }
                }
            }
            finally
            {
                _ = Marshal.Release(collection);
            }
        }
        finally
        {
            _ = Marshal.Release(manager);
        }

        failure = $"no Wi-Fi radio instance matched interface {interfaceGuid:D}";
        return new RadioSetResult(false, false, failure);
    }

    private static bool TryCreateManager(out IntPtr manager, out string? failure)
    {
        failure = null;
        var clsid = ClsidWlanRadioManager;
        var iid = IidMediaRadioManager;
        var hr = CoCreateInstance(clsid, IntPtr.Zero, ClsctxAll, iid, out manager);
        Trace?.Invoke($"CoCreateInstance(Wlan Radio Manager, IMediaRadioManager) -> 0x{hr:X8}");

        if (hr >= 0 && manager != IntPtr.Zero)
        {
            return true;
        }

        failure = $"the Windows radio manager could not be created: 0x{hr:X8}";
        manager = IntPtr.Zero;
        return false;
    }

    private static bool TryGetCollection(IntPtr manager, out IntPtr collection, out string? failure)
    {
        failure = null;
        var hr = GetRadioInstances(manager, out collection);
        Trace?.Invoke($"IMediaRadioManager.GetRadioInstances -> 0x{hr:X8}");

        if (hr >= 0 && collection != IntPtr.Zero)
        {
            return true;
        }

        failure = $"IMediaRadioManager.GetRadioInstances failed: 0x{hr:X8}";
        collection = IntPtr.Zero;
        return false;
    }

    private static bool TryReadInstance(IntPtr instance, out RadioInstanceInfo info)
    {
        info = new RadioInstanceInfo(Guid.Empty, string.Empty, RadioDeviceState.Invalid);

        var name = string.Empty;
        if (GetFriendlyName(instance, 0, out var nameBstr) < 0 || nameBstr == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            name = Marshal.PtrToStringBSTR(nameBstr) ?? string.Empty;
        }
        finally
        {
            SysFreeString(nameBstr);
        }

        // GetInstanceSignature is the WLAN interface GUID - the same identifier the WLAN API reports,
        // which is why no name matching is needed to line a radio up with a adapter.
        var signature = string.Empty;
        if (GetInstanceSignature(instance, out var signatureBstr) >= 0 && signatureBstr != IntPtr.Zero)
        {
            try
            {
                signature = Marshal.PtrToStringBSTR(signatureBstr) ?? string.Empty;
            }
            finally
            {
                SysFreeString(signatureBstr);
            }
        }

        if (GetRadioState(instance, out var state) < 0)
        {
            return false;
        }

        var interfaceGuid = Guid.TryParse(signature.Trim('{', '}'), out var parsed) ? parsed : Guid.Empty;
        info = new RadioInstanceInfo(interfaceGuid, name, (RadioDeviceState)state);
        return true;
    }

    // ---------- vtable calls (IRadioInstance = IUnknown + 7 methods, IMediaRadioManager = IUnknown + 2) ----------

    private static unsafe int GetRadioInstances(IntPtr manager, out IntPtr collection)
    {
        var function = (delegate* unmanaged<IntPtr, IntPtr*, int>)(*(IntPtr**)manager)[3];
        IntPtr result;
        var hr = function(manager, &result);
        collection = result;
        return hr;
    }

    private static unsafe int GetCount(IntPtr collection, out uint count)
    {
        var function = (delegate* unmanaged<IntPtr, uint*, int>)(*(IntPtr**)collection)[3];
        uint result;
        var hr = function(collection, &result);
        count = result;
        return hr;
    }

    private static unsafe int GetAt(IntPtr collection, uint index, out IntPtr instance)
    {
        var function = (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)collection)[4];
        IntPtr result;
        var hr = function(collection, index, &result);
        instance = result;
        return hr;
    }

    private static unsafe int GetInstanceSignature(IntPtr instance, out IntPtr bstr)
    {
        var function = (delegate* unmanaged<IntPtr, IntPtr*, int>)(*(IntPtr**)instance)[4];
        IntPtr result;
        var hr = function(instance, &result);
        bstr = result;
        return hr;
    }

    private static unsafe int GetFriendlyName(IntPtr instance, uint lcid, out IntPtr bstr)
    {
        var function = (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)instance)[5];
        IntPtr result;
        var hr = function(instance, lcid, &result);
        bstr = result;
        return hr;
    }

    private static unsafe int GetRadioState(IntPtr instance, out int state)
    {
        var function = (delegate* unmanaged<IntPtr, int*, int>)(*(IntPtr**)instance)[6];
        int result;
        var hr = function(instance, &result);
        state = result;
        return hr;
    }

    private static unsafe int SetRadioState(IntPtr instance, int state, uint timeoutSeconds)
    {
        var function = (delegate* unmanaged<IntPtr, int, uint, int>)(*(IntPtr**)instance)[7];
        return function(instance, state, timeoutSeconds);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on an MTA thread. The UI thread is STA, and <c>CoInitializeEx</c>
    /// cannot switch an apartment once it is set, so COM work is moved to the pool when needed.
    /// </summary>
    private static T RunInMta<T>(Func<T> work) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA
            ? work()
            : Task.Run(work).GetAwaiter().GetResult();

    /// <summary>Balances <c>CoInitializeEx</c>/<c>CoUninitialize</c> for one COM call.</summary>
    private readonly struct ComApartment : IDisposable
    {
        private readonly bool _shouldUninitialize;

        public ComApartment()
        {
            var hr = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            Ok = hr >= 0;
            Failure = Ok ? null : $"CoInitializeEx failed: 0x{hr:X8}";

            // S_FALSE (1) means the thread was already initialised: not an error, but it must not be
            // paired with CoUninitialize or the apartment reference count underflows.
            _shouldUninitialize = hr == 0;
        }

        public bool Ok { get; }

        public string? Failure { get; }

        public void Dispose()
        {
            if (_shouldUninitialize)
            {
                CoUninitialize();
            }
        }
    }
}
