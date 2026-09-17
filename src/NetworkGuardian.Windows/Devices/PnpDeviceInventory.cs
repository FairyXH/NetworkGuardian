using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Native;
using static NetworkGuardian.Windows.Native.CfgMgr32Native;
using static NetworkGuardian.Windows.Native.SetupApiNative;

namespace NetworkGuardian.Windows.Devices;

/// <summary>
/// Enumerates PnP device nodes in the network class and materialises them as
/// <see cref="PnpDeviceRecord"/> instances, including the NDIS properties that live in the driver
/// key under <c>HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-...}</c>.
/// </summary>
public sealed class PnpDeviceInventory
{
    private readonly ILogger<PnpDeviceInventory> _logger;

    public PnpDeviceInventory(ILogger<PnpDeviceInventory>? logger = null)
    {
        _logger = logger ?? NullLogger<PnpDeviceInventory>.Instance;
    }

    /// <summary>Enumerates every network class device node, present or ghost.</summary>
    public IReadOnlyList<PnpDeviceRecord> Enumerate()
    {
        var results = new Dictionary<string, PnpDeviceRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in EnumerateInternal(requirePresent: true))
        {
            results[record.DeviceInstanceId] = record;
        }

        foreach (var record in EnumerateInternal(requirePresent: false))
        {
            if (!results.ContainsKey(record.DeviceInstanceId))
            {
                results[record.DeviceInstanceId] = record;
            }
        }

        return results.Values
            .OrderBy(r => r.DeviceInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<PnpDeviceRecord> EnumerateInternal(bool requirePresent)
    {
        var records = new List<PnpDeviceRecord>();
        var flags = requirePresent ? DIGCF_PRESENT : 0u;

        var set = SetupDiGetClassDevsW(in GuidDevClassNet, null, IntPtr.Zero, flags);
        if (set == SetupApiNative.InvalidHandleValue || set == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("SetupDiGetClassDevs(net) failed: {Error}",
                Win32Error.Describe(error));
            return records;
        }

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (var index = 0u; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var instanceId = ReadInstanceId(set, ref data);
                if (string.IsNullOrEmpty(instanceId))
                {
                    continue;
                }

                try
                {
                    records.Add(BuildRecord(instanceId, requirePresent));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read PnP properties for {InstanceId}", instanceId);
                }
            }

            var lastError = Marshal.GetLastWin32Error();
            if (lastError != 0 && lastError != Win32Error.ErrorNoMoreItems && records.Count == 0)
            {
                _logger.LogDebug("SetupDiEnumDeviceInfo ended with error {Error}",
                    Win32Error.Describe(lastError));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return records;
    }

    private static string ReadInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var size = 512u;
        var buffer = new char[size];
        if (SetupDiGetDeviceInstanceIdW(set, ref data, buffer, size, out var required))
        {
            return new string(buffer).TrimEnd('\0');
        }

        if (required == 0 || required > 4096)
        {
            return string.Empty;
        }

        buffer = new char[required];
        return SetupDiGetDeviceInstanceIdW(set, ref data, buffer, required, out _)
            ? new string(buffer).TrimEnd('\0')
            : string.Empty;
    }

    private PnpDeviceRecord BuildRecord(string instanceId, bool present)
    {
        var record = new PnpDeviceRecord
        {
            DeviceInstanceId = instanceId,
            IsPresent = present,
        };

        var status = CM_Locate_DevNodeW(out var devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
        if (status != CR_SUCCESS)
        {
            // Try the phantom form; disabled/ghost nodes are still locatable.
            status = CM_Locate_DevNodeW(out devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM);
        }

        if (status != CR_SUCCESS)
        {
            _logger.LogDebug("CM_Locate_DevNode({InstanceId}) returned {Status}",
                instanceId, Describe(status));
            return record with { EnumeratorName = DeriveEnumerator(instanceId) };
        }

        var statusResult = CM_Get_DevNode_Status(out var devStatus, out var problem, devInst, 0);
        var isStarted = statusResult == CR_SUCCESS && (devStatus & DN_STARTED) != 0;
        var hasProblem = statusResult == CR_SUCCESS && (devStatus & DN_HAS_PROBLEM) != 0;

        string? parentInstanceId = null;
        if (CM_Get_Parent(out var parentInst, devInst, 0) == CR_SUCCESS)
        {
            parentInstanceId = GetDeviceId(parentInst);
            if (string.IsNullOrEmpty(parentInstanceId))
            {
                parentInstanceId = null;
            }
        }

        var driverKey = GetStringProperty(devInst, CM_DRP_DRIVER);
        var netProperties = ReadNetRegistryProperties(driverKey);

        return record with
        {
            ParentInstanceId = parentInstanceId,
            FriendlyName = GetStringProperty(devInst, CM_DRP_FRIENDLYNAME),
            DeviceDescription = GetStringProperty(devInst, CM_DRP_DEVICEDESC),
            BusReportedDeviceDescription = GetStringProperty(devInst, CM_DRP_LOCATION_INFORMATION),
            EnumeratorName = GetStringProperty(devInst, CM_DRP_ENUMERATOR_NAME) ?? DeriveEnumerator(instanceId),
            BusTypeGuid = GetGuidProperty(devInst, CM_DRP_BUSTYPEGUID),
            ClassName = GetStringProperty(devInst, CM_DRP_CLASS),
            ClassGuid = ParseGuid(GetStringProperty(devInst, CM_DRP_CLASSGUID)),
            DriverKey = driverKey,
            Service = GetStringProperty(devInst, CM_DRP_SERVICE),
            LocationInfo = GetStringProperty(devInst, CM_DRP_LOCATION_INFORMATION),
            ManufacturerName = GetStringProperty(devInst, CM_DRP_MFG),
            HardwareIds = GetStringListProperty(devInst, CM_DRP_HARDWAREID),
            CompatibleIds = GetStringListProperty(devInst, CM_DRP_COMPATIBLEIDS),
            IsStarted = isStarted,
            IsPresent = present || hasProblem,
            ProblemCode = statusResult == CR_SUCCESS ? problem : 0,
            NetCfgInstanceId = netProperties.NetCfgInstanceId,
            PhysicalMediaType = netProperties.PhysicalMediaType,
            IfType = netProperties.IfType,
        };
    }

    private sealed record NetRegistryProperties(string? NetCfgInstanceId, int? PhysicalMediaType, int? IfType);

    private NetRegistryProperties ReadNetRegistryProperties(string? driverKey)
    {
        if (string.IsNullOrWhiteSpace(driverKey))
        {
            return new NetRegistryProperties(null, null, null);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}", writable: false);

            if (key is null)
            {
                return new NetRegistryProperties(null, null, null);
            }

            var netCfg = key.GetValue("NetCfgInstanceId") as string;
            var mediaType = key.GetValue("*PhysicalMediaType") as int?;
            var ifType = key.GetValue("*IfType") as int?;

            return new NetRegistryProperties(
                string.IsNullOrWhiteSpace(netCfg) ? null : netCfg,
                mediaType,
                ifType);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read net registry properties for driver key {DriverKey}", driverKey);
            return new NetRegistryProperties(null, null, null);
        }
    }

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, out var guid) ? guid : null;

    private static string DeriveEnumerator(string instanceId)
    {
        var separator = instanceId.IndexOf('\\');
        return separator <= 0 ? instanceId : instanceId[..separator];
    }

    /// <summary>Locates a device node and reports its current status without changing anything.</summary>
    public (bool Found, bool Started, uint ProblemCode, string? Error) QueryStatus(string deviceInstanceId)
    {
        var status = CM_Locate_DevNodeW(out var devInst, deviceInstanceId, CM_LOCATE_DEVNODE_NORMAL);
        if (status != CR_SUCCESS)
        {
            return (false, false, 0, Describe(status));
        }

        if (CM_Get_DevNode_Status(out var devStatus, out var problem, devInst, 0) != CR_SUCCESS)
        {
            return (true, false, 0, "CM_Get_DevNode_Status failed");
        }

        return (true, (devStatus & DN_STARTED) != 0, problem, null);
    }
}
