# Queries or changes the per-interface Wi-Fi software radio state (what the individual Wi-Fi
# switches in Windows Settings -> Network & internet -> Wi-Fi map to).
#
# Usage:
#   pwsh -NoProfile -File tools/qa-wifi-radio.ps1 -Action query
#   pwsh -NoProfile -File tools/qa-wifi-radio.ps1 -Action off -InterfaceIndex 1
#   pwsh -NoProfile -File tools/qa-wifi-radio.ps1 -Action on  -InterfaceIndex 1
#
# The radio_state opcode of WlanQueryInterface/WlanSetInterface is per WLAN interface; each PHY of that
# interface carries a software and a hardware radio state.

param(
    [ValidateSet('query', 'on', 'off')]
    [string]$Action = 'query',
    [int]$InterfaceIndex = 0
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class NgWlanRadio
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_INTERFACE_INFO_HEAD
    {
        public Guid InterfaceGuid;
        public uint IsState;
    }

    [DllImport("wlanapi.dll", SetLastError = true)]
    public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll", SetLastError = true)]
    public static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll", SetLastError = true)]
    public static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll", SetLastError = true)]
    public static extern uint WlanQueryInterface(IntPtr handle, in Guid interfaceGuid, uint opcode,
        IntPtr reserved, out uint size, out IntPtr data, out uint opcodeValueType);

    [DllImport("wlanapi.dll", SetLastError = true)]
    public static extern uint WlanSetInterface(IntPtr handle, in Guid interfaceGuid, uint opcode,
        uint size, IntPtr data, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr memory);

    public const uint OpcodeRadioState = 4;
    public const int PhysOffset = 4;          // WLAN_RADIO_STATE.dwNumberOfPhys
    public const int PhyStride = 12;          // dwPhyIndex + software + hardware
    public const int MaxPhys = 64;

    public static Guid[] EnumInterfaces(IntPtr handle)
    {
        IntPtr list;
        if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) { throw new InvalidOperationException("WlanEnumInterfaces failed"); }
        try
        {
            var count = Marshal.ReadInt32(list);
            var result = new Guid[count];
            var head = Marshal.SizeOf<WLAN_INTERFACE_INFO_HEAD>();
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_HEAD>(IntPtr.Add(list, 8 + (i * 532))).InterfaceGuid;
            }
            _ = head;
            return result;
        }
        finally { WlanFreeMemory(list); }
    }

    /// <summary>Reads the software radio state of every PHY of one interface; null when unavailable.</summary>
    public static bool?[] ReadRadio(IntPtr handle, Guid interfaceGuid)
    {
        uint size;
        IntPtr data;
        uint valueType;
        if (WlanQueryInterface(handle, in interfaceGuid, OpcodeRadioState, IntPtr.Zero, out size, out data, out valueType) != 0)
        {
            return Array.Empty<bool?>();
        }

        try
        {
            var phys = Marshal.ReadInt32(data);
            var result = new bool?[phys];
            for (var i = 0; i < phys && i < MaxPhys; i++)
            {
                var software = Marshal.ReadInt32(IntPtr.Add(data, PhysOffset + (i * PhyStride) + 4));
                result[i] = software == 1;
            }
            return result;
        }
        finally { WlanFreeMemory(data); }
    }

    public static void SetRadio(IntPtr handle, Guid interfaceGuid, bool on)
    {
        uint size;
        IntPtr data;
        uint valueType;
        if (WlanQueryInterface(handle, in interfaceGuid, OpcodeRadioState, IntPtr.Zero, out size, out data, out valueType) != 0)
        {
            throw new InvalidOperationException("WlanQueryInterface(radio_state) failed");
        }

        try
        {
            var phys = Marshal.ReadInt32(data);
            Console.WriteLine($"  query: size={size} valueType={valueType} phys={phys}");
            if (phys <= 0 || phys > MaxPhys) { throw new InvalidOperationException($"unexpected PHY count {phys}"); }

            var full = PhysOffset + (MaxPhys * PhyStride);   // sizeof(WLAN_RADIO_STATE) = 772
            var buffer = Marshal.AllocHGlobal(full);
            try
            {
                // Read-modify-write: copy the whole driver structure and flip only dot11SoftwareRadioState,
                // so dwPhyIndex and dot11HardwareRadioState stay exactly as the driver reported them.
                for (var i = 0; i < Math.Min(size, (uint)full); i++)
                {
                    Marshal.WriteByte(buffer, i, Marshal.ReadByte(data, i));
                }

                for (var i = 0; i < phys; i++)
                {
                    Marshal.WriteInt32(IntPtr.Add(buffer, PhysOffset + (i * PhyStride) + 4), on ? 1 : 2);
                }

                var status = WlanSetInterface(handle, in interfaceGuid, OpcodeRadioState, (uint)full, buffer, IntPtr.Zero);
                Console.WriteLine($"  set: size={full} status={status}");
                if (status != 0)
                {
                    throw new InvalidOperationException($"WlanSetInterface failed with {status} (lastError {Marshal.GetLastWin32Error()})");
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { WlanFreeMemory(data); }
    }
}
'@

$negotiated = 0
$handle = [IntPtr]::Zero
if ([NgWlanRadio]::WlanOpenHandle(2, [IntPtr]::Zero, [ref]$negotiated, [ref]$handle) -ne 0) {
    throw 'WlanOpenHandle failed'
}

try {
    $guids = [NgWlanRadio]::EnumInterfaces($handle)
    $descriptions = @{}
    # netsh is the simplest way to name the interfaces without re-implementing the wide-string walk.
    $text = netsh wlan show interfaces
    $current = $null
    foreach ($line in $text) {
        if ($line -match '^\s{4}Name\s+:\s(.+?)\s*$') { $current = $Matches[1] }
        elseif ($line -match '^\s{4}Description\s+:\s(.+?)\s*$' -and $current) { $descriptions[$current] = $Matches[1] }
    }

    for ($i = 0; $i -lt $guids.Count; $i++) {
        $states = [NgWlanRadio]::ReadRadio($handle, $guids[$i])
        $pretty = ($states | ForEach-Object { if ($null -eq $_) { '?' } elseif ($_) { 'on' } else { 'off' } }) -join ','
        Write-Host ("[{0}] {1}  software={2}" -f $i, $guids[$i], $pretty)
    }

    if ($Action -ne 'query') {
        if ($InterfaceIndex -lt 0 -or $InterfaceIndex -ge $guids.Count) { throw "interface index out of range (0..$($guids.Count - 1))" }
        $target = $guids[$InterfaceIndex]
        Write-Host "setting interface $InterfaceIndex ($target) software radio -> $Action"
        [NgWlanRadio]::SetRadio($handle, $target, ($Action -eq 'on'))
        Start-Sleep -Milliseconds 800
        $after = [NgWlanRadio]::ReadRadio($handle, $target)
        Write-Host ("after: software=" + (($after | ForEach-Object { if ($null -eq $_) { '?' } elseif ($_) { 'on' } else { 'off' } }) -join ','))
    }
}
finally {
    [void][NgWlanRadio]::WlanCloseHandle($handle, [IntPtr]::Zero)
}
