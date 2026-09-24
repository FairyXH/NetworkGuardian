using System.Runtime.InteropServices;
using System.Text;

namespace NetworkGuardian.Windows.Native;

/// <summary>P/Invoke declarations for <c>SetupAPI</c> (device information sets).</summary>
internal static class SetupApiNative
{
    internal const string Dll = "setupapi.dll";

    internal const uint DIGCF_DEFAULT = 0x00000001;
    internal const uint DIGCF_PRESENT = 0x00000002;
    internal const uint DIGCF_ALLCLASSES = 0x00000004;
    internal const uint DIGCF_PROFILE = 0x00000008;
    internal const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    internal const int INVALID_HANDLE_VALUE = -1;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>Network adapters setup class GUID (GUID_DEVCLASS_NET).</summary>
    internal static readonly Guid GuidDevClassNet = new("4d36e972-e325-11ce-bfc1-08002be10318");

    /// <summary>Network adapter device-interface class GUID (GUID_DEVINTERFACE_NET).</summary>
    internal static readonly Guid GuidDevInterfaceNet = new("cac88484-7515-4c03-82e6-71a87abac361");

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(
        in Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport(Dll, SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        [Out] char[] deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport(Dll, SetLastError = true)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        [Out] byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    internal const uint SPDRP_DEVICEDESC = 0x00000000;
    internal const uint SPDRP_HARDWAREID = 0x00000001;
    internal const uint SPDRP_COMPATIBLEIDS = 0x00000002;
    internal const uint SPDRP_SERVICE = 0x00000004;
    internal const uint SPDRP_CLASS = 0x00000007;
    internal const uint SPDRP_CLASSGUID = 0x00000008;
    internal const uint SPDRP_DRIVER = 0x00000009;
    internal const uint SPDRP_MFG = 0x0000000B;
    internal const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    internal const uint SPDRP_LOCATION_INFORMATION = 0x0000000D;
    internal const uint SPDRP_PHYSICAL_DEVICE_OBJECT_NAME = 0x0000000E;
    internal const uint SPDRP_CAPABILITIES = 0x0000000F;
    internal const uint SPDRP_UI_NUMBER = 0x00000010;
    internal const uint SPDRP_UPPERFILTERS = 0x00000011;
    internal const uint SPDRP_LOWERFILTERS = 0x00000012;
    internal const uint SPDRP_BUSTYPEGUID = 0x00000013;
    internal const uint SPDRP_ENUMERATOR_NAME = 0x00000016;
    internal const uint SPDRP_ADDRESS = 0x0000001C;

    internal static string? GetStringProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var size = 0u;
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out size);
        if (size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, size, out var required))
        {
            if (required == 0 || required > size)
            {
                return null;
            }

            buffer = new byte[required];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, required, out _))
            {
                return null;
            }
        }

        var value = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal static IReadOnlyList<string> GetMultiStringProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var size = 0u;
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out size);
        if (size == 0)
        {
            return Array.Empty<string>();
        }

        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, size, out _))
        {
            return Array.Empty<string>();
        }

        return Encoding.Unicode
            .GetString(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}
