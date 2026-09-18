# Dumps a WLAN profile XML read straight from the WLAN service (WlanGetProfile).
#
# Why this exists: the only authoritative reference for the profile schema is a profile the WLAN
# service itself accepted. When a generated document is rejected with reason code 524289 ("invalid
# according to the schema"), diffing against a profile Windows stored on the same machine is the
# fastest way to find the missing or misplaced element.
#
# Usage:
#   pwsh -NoProfile -File tools/dump-wlan-profile.ps1 -Profile HXXY-WiFi
#   pwsh -NoProfile -File tools/dump-wlan-profile.ps1 -Profile HXXY-WiFi -OutFile C:\temp\hxxy.xml
#
# Credentials inside a stored profile are DPAPI blobs owned by the WLAN service, never clear text,
# but the output is still treated as sensitive: it is only written where the caller asks for it.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Profile,
    [string]$InterfaceGuid = '',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'

$source = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WlanDump
{
    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(IntPtr handle, ref Guid guid, string name, IntPtr reserved,
        out IntPtr xml, ref uint flags, out uint grantedAccess);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanReasonCodeToString(uint code, uint size, char[] buffer, IntPtr reserved);

    public static string[] EnumerateInterfaces(out string failure)
    {
        failure = null;
        var results = new System.Collections.Generic.List<string>();
        uint negotiated;
        IntPtr handle;

        if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle) != 0)
        {
            failure = "WlanOpenHandle failed";
            return results.ToArray();
        }

        try
        {
            IntPtr list;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0)
            {
                failure = "WlanEnumInterfaces failed";
                return results.ToArray();
            }

            try
            {
                var count = Marshal.ReadInt32(list);
                for (var i = 0; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO: GUID (16) + 512 byte description + 4 byte state = 532.
                    var entry = IntPtr.Add(list, 8 + (i * 532));
                    var guid = (Guid)Marshal.PtrToStructure(entry, typeof(Guid));
                    var description = Marshal.PtrToStringUni(IntPtr.Add(entry, 16), 256).TrimEnd('\0');
                    results.Add(guid.ToString("D") + "|" + description);
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }

        return results.ToArray();
    }

    public static string GetProfile(Guid guid, string profileName, out string failure)
    {
        failure = null;
        uint negotiated;
        IntPtr handle;

        if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle) != 0)
        {
            failure = "WlanOpenHandle failed";
            return null;
        }

        try
        {
            IntPtr xml;
            uint flags = 0;
            uint granted;
            var result = WlanGetProfile(handle, ref guid, profileName, IntPtr.Zero, out xml, ref flags, out granted);

            if (result != 0)
            {
                var buffer = new char[512];
                var reason = WlanReasonCodeToString(result, (uint)buffer.Length, buffer, IntPtr.Zero) == 0
                    ? new string(buffer).TrimEnd('\0')
                    : "(no description)";
                failure = "WlanGetProfile returned " + result + " (" + reason + ")";
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(xml);
            }
            finally
            {
                WlanFreeMemory(xml);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp | Out-Null

$failure = ''
$interfaces = [WlanDump]::EnumerateInterfaces([ref]$failure)
if ($failure) { throw $failure }

if (-not $interfaces) { throw 'no WLAN interface is present' }

$targets = @()
foreach ($entry in $interfaces) {
    $parts = $entry.Split('|')
    if ($InterfaceGuid -and $parts[0] -ne $InterfaceGuid) { continue }
    $targets += $parts
}

$found = $false
for ($i = 0; $i -lt $targets.Count; $i += 2) {
    $guid = [Guid]$targets[$i]
    $description = $targets[$i + 1]
    $xml = [WlanDump]::GetProfile($guid, $Profile, [ref]$failure)
    if (-not $xml) {
        Write-Host "interface $description ($guid): $Profile not found ($failure)"
        continue
    }

    $found = $true
    Write-Host "== interface $description ($guid), $($xml.Length) chars"
    if ($OutFile) {
        [System.IO.File]::WriteAllText($OutFile, $xml, [System.Text.UTF8Encoding]::new($false))
        Write-Host "   written to $OutFile"
    }
    else {
        Write-Host $xml
    }
}

if (-not $found) { Write-Host "profile '$Profile' was not found on any interface" }
