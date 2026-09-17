using System.Runtime.InteropServices;
using System.Text;

namespace NetworkGuardian.Windows.Native;

/// <summary>
/// P/Invoke declarations for <c>CfgMgr32.dll</c> (Configuration Manager).
/// </summary>
/// <remarks>
/// Device node properties are read through <c>CM_Get_DevNode_Registry_PropertyW</c> using the
/// documented <c>CM_DRP_*</c> identifiers, which avoids the separate PROPERTYKEY plumbing of
/// SetupAPI while returning exactly the same data.
/// </remarks>
internal static class CfgMgr32Native
{
    internal const string Dll = "cfgmgr32.dll";

    internal const int CR_SUCCESS = 0x00000000;
    internal const int CR_NO_SUCH_VALUE = 0x00000025;
    internal const int CR_BUFFER_SMALL = 0x0000001A;
    internal const int CR_ACCESS_DENIED = 0x00000033;
    internal const int CR_INVALID_DEVINST = 0x00000037;
    internal const int CR_REMOVE_VETOED = 0x00000017;

    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    internal const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;

    internal const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000000;
    internal const uint CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES = 0x00000001;

    // CM_Get_DevNode_Status flags.
    internal const uint DN_HAS_PROBLEM = 0x00000400;
    internal const uint DN_STARTED = 0x00000008;
    internal const uint DN_DISABLEABLE = 0x00002000;
    internal const uint DN_PRESENT = 0x00000000;
    internal const uint DN_PRIVATE_PROBLEM = 0x00008000;

    // CM_DRP_* property selectors, copied verbatim from um/cfgmgr32.h (SDK 10.0.26100.0).
    // These are deliberately not the SPDRP_* values from setupapi.h: SPDRP_DRIVER is 0x09 while
    // CM_DRP_DRIVER is 0x0A, and mixing the two silently reads the wrong property.
    internal const uint CM_DRP_DEVICEDESC = 0x00000001;
    internal const uint CM_DRP_HARDWAREID = 0x00000002;
    internal const uint CM_DRP_COMPATIBLEIDS = 0x00000003;
    internal const uint CM_DRP_SERVICE = 0x00000005;
    internal const uint CM_DRP_CLASS = 0x00000008;
    internal const uint CM_DRP_CLASSGUID = 0x00000009;
    internal const uint CM_DRP_DRIVER = 0x0000000A;
    internal const uint CM_DRP_CONFIGFLAGS = 0x0000000B;
    internal const uint CM_DRP_MFG = 0x0000000C;
    internal const uint CM_DRP_FRIENDLYNAME = 0x0000000D;
    internal const uint CM_DRP_LOCATION_INFORMATION = 0x0000000E;
    internal const uint CM_DRP_CAPABILITIES = 0x00000010;
    internal const uint CM_DRP_BUSTYPEGUID = 0x00000014;
    internal const uint CM_DRP_BUSNUMBER = 0x00000016;
    internal const uint CM_DRP_ENUMERATOR_NAME = 0x00000017;
    internal const uint CM_DRP_ADDRESS = 0x0000001D;
    internal const uint CM_DRP_INSTALL_STATE = 0x00000023;
    internal const uint CM_DRP_LOCATION_PATHS = 0x00000024;

    internal const uint CM_REG_SZ = 1;
    internal const uint CM_REG_MULTI_SZ = 7;

    // CM_NOTIFY_FILTER_TYPE
    internal const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    internal const uint CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE = 1;
    internal const uint CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE = 2;

    internal const int MAX_DEVICE_ID_LEN = 200;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint CmNotifyCallback(IntPtr context, uint action, IntPtr eventData, IntPtr eventDataSize);

    // CM_NOTIFY_ACTION
    internal const uint CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    internal const uint CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;
    internal const uint CM_NOTIFY_ACTION_DEVICEQUERYREMOVE = 2;
    internal const uint CM_NOTIFY_ACTION_DEVICEQUERYREMOVEFAILED = 3;
    internal const uint CM_NOTIFY_ACTION_DEVICEREMOVEPENDING = 4;
    internal const uint CM_NOTIFY_ACTION_DEVICEREMOVECOMPLETE = 5;
    internal const uint CM_NOTIFY_ACTION_DEVICECUSTOMEVENT = 6;
    internal const uint CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED = 7;
    internal const uint CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED = 8;
    internal const uint CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED = 9;

    /// <summary>
    /// <c>CM_NOTIFY_FILTER</c> from cfgmgr32.h modelled with explicit offsets because the C
    /// declaration is a struct containing a union. The union's largest member is
    /// <c>WCHAR InstanceId[MAX_DEVICE_ID_LEN]</c> (400 bytes), so sizeof(CM_NOTIFY_FILTER) is 416
    /// and that value must be written into <c>cbSize</c>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal unsafe struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public uint Reserved;

        // union { struct { GUID ClassGuid; } DeviceInterface;
        //         struct { HANDLE hTarget; } DeviceHandle;
        //         struct { WCHAR InstanceId[MAX_DEVICE_ID_LEN]; } DeviceInstance; }
        [FieldOffset(16)] public Guid ClassGuid;
        [FieldOffset(16)] public IntPtr Target;
        [FieldOffset(16)] public fixed char InstanceId[MAX_DEVICE_ID_LEN];
    }

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int CM_Locate_DevNodeW(
        out uint pdnDevInst,
        string pDeviceID,
        uint ulFlags);

    [DllImport(Dll)]
    internal static extern int CM_Get_DevNode_Status(
        out uint pulStatus,
        out uint pulProblemNumber,
        uint dnDevInst,
        uint ulFlags);

    [DllImport(Dll)]
    internal static extern int CM_Get_Parent(
        out uint pdnDevInst,
        uint dnDevInst,
        uint ulFlags);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_IDW(
        uint dnDevInst,
        [Out] char[] buffer,
        uint bufferLen,
        uint ulFlags);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_ID_Size(
        out uint pulLen,
        uint dnDevInst,
        uint ulFlags);

    [DllImport(Dll)]
    internal static extern int CM_Get_DevNode_Registry_PropertyW(
        uint dnDevInst,
        uint ulProperty,
        out uint pulRegDataType,
        [Out] byte[]? buffer,
        ref uint pulLength,
        uint ulFlags);

    [DllImport(Dll)]
    internal static extern int CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport(Dll)]
    internal static extern int CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport(Dll)]
    internal static extern int CM_Register_Notification(
        IntPtr filter,
        IntPtr context,
        CmNotifyCallback callback,
        out IntPtr notifyContext);

    [DllImport(Dll)]
    internal static extern int CM_Unregister_Notification(IntPtr notifyContext);

    /// <summary>Reads a REG_SZ device property.</summary>
    internal static string? GetStringProperty(uint devInst, uint property)
    {
        var length = 0u;
        var status = CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, null, ref length, 0);
        if (status != CR_BUFFER_SMALL || length == 0)
        {
            return null;
        }

        var buffer = new byte[length];
        status = CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref length, 0);
        if (status != CR_SUCCESS)
        {
            return null;
        }

        var value = Encoding.Unicode.GetString(buffer, 0, (int)length).TrimEnd('\0');
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Reads a REG_MULTI_SZ device property.</summary>
    internal static IReadOnlyList<string> GetStringListProperty(uint devInst, uint property)
    {
        var length = 0u;
        var status = CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, null, ref length, 0);
        if (status != CR_BUFFER_SMALL || length == 0)
        {
            return Array.Empty<string>();
        }

        var buffer = new byte[length];
        status = CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref length, 0);
        if (status != CR_SUCCESS)
        {
            return Array.Empty<string>();
        }

        var raw = Encoding.Unicode.GetString(buffer, 0, (int)length);
        return raw
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>Reads a GUID valued device property such as CM_DRP_BUSTYPEGUID.</summary>
    internal static Guid? GetGuidProperty(uint devInst, uint property)
    {
        var length = 16u;
        var buffer = new byte[16];
        var status = CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref length, 0);
        if (status != CR_SUCCESS || length < 16)
        {
            return null;
        }

        return new Guid(buffer);
    }

    internal static string GetDeviceId(uint devInst)
    {
        if (CM_Get_Device_ID_Size(out var length, devInst, 0) != CR_SUCCESS || length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        return CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Length, 0) == CR_SUCCESS
            ? new string(buffer).TrimEnd('\0')
            : string.Empty;
    }

    internal static string Describe(int cr) => cr switch
    {
        CR_SUCCESS => "CR_SUCCESS",
        CR_NO_SUCH_VALUE => "CR_NO_SUCH_VALUE",
        CR_BUFFER_SMALL => "CR_BUFFER_SMALL",
        CR_ACCESS_DENIED => "CR_ACCESS_DENIED",
        CR_INVALID_DEVINST => "CR_INVALID_DEVINST",
        CR_REMOVE_VETOED => "CR_REMOVE_VETOED",
        _ => $"CR_0x{cr:X8}",
    };
}
