using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

public static class DeviceDisablePolicy
{
    public static bool IsDisabled(GuardianConfig config, string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) &&
        (config.DisabledDeviceInstanceIds?.Contains(deviceId, StringComparer.OrdinalIgnoreCase) ?? false);

    public static bool IsDisabled(GuardianConfig config, Guid adapter, IEnumerable<ManagedDevice> devices) =>
        devices.Any(d => IsDisabled(config, d.Record.DeviceInstanceId) &&
            Guid.TryParse(d.Record.NetCfgInstanceId, out var guid) && guid == adapter);

    public static bool NeedsDisable(GuardianConfig config, ManagedDevice device) =>
        IsDisabled(config, device.Record.DeviceInstanceId) && device.Record.IsPresent &&
        device.Classification.IsPhysical && device.Classification.Category is
            DeviceCategory.PhysicalWifi or DeviceCategory.PhysicalEthernet && !device.IsDisabled;

    public static GuardianInput Filter(GuardianInput input)
    {
        if (input.Config.DisabledDeviceInstanceIds is not { Count: > 0 }) return input;
        var blocked = input.Devices.Where(d => IsDisabled(input.Config, d.Record.DeviceInstanceId))
            .Select(d => d.Record.NetCfgInstanceId).Where(id => Guid.TryParse(id, out _))
            .Select(id => Guid.Parse(id!)).ToHashSet();
        foreach (var adapter in input.WifiAdapters.Where(a => IsDisabled(input.Config, a.DeviceInstanceId)))
            blocked.Add(adapter.InterfaceGuid);
        return input with
        {
            Devices = input.Devices.Where(d => !IsDisabled(input.Config, d.Record.DeviceInstanceId)).ToArray(),
            WifiAdapters = input.WifiAdapters.Where(a => !blocked.Contains(a.InterfaceGuid)).ToArray(),
            Interfaces = input.Interfaces.Where(i => !IsDisabled(input.Config, i.DeviceInstanceId) &&
                !(i.AdapterGuid is { } id && blocked.Contains(id)) &&
                !(i.WlanInterfaceGuid is { } wlan && blocked.Contains(wlan))).ToArray(),
        };
    }
}
