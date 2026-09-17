
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

/// <summary>
/// Decides whether a PnP network device node is a *real* physical adapter that NetworkGuardian is
/// allowed to manage. Virtual adapters must never be scanned, connected, enabled or disabled.
/// </summary>
/// <remarks>
/// The classifier deliberately requires positive evidence of a physical bus and positive evidence of
/// a wireless/wired LAN media type. Anything ambiguous is reported as not physical, because the
/// failure mode of touching a virtual adapter (Wi-Fi Direct, Hyper-V, VPN) is much worse than the
/// failure mode of ignoring a real one.
/// </remarks>
public sealed class NetworkDeviceClassifier
{
    /// <summary>Network adapters class GUID.</summary>
    public static readonly Guid NetClassGuid = new("4d36e972-e325-11ce-bfc1-08002be10318");

    /// <summary>PCI bus type GUID (GUID_BUS_TYPE_PCI).</summary>
    public static readonly Guid PciBusType = new("c8ebdfb0-b510-11d0-80e5-00a0c92542e3");

    /// <summary>USB bus type GUID (GUID_BUS_TYPE_USB).</summary>
    public static readonly Guid UsbBusType = new("9d7debbc-c85d-11d1-9eb4-006008c3a19a");

    /// <summary>Enumerator GUID of the Microsoft Virtual Wi-Fi (Wi-Fi Direct / hosted network) bus.</summary>
    public static readonly Guid VirtualWifiEnumerator = new("5d624f94-8850-40c3-a3fa-a4fd2080baf3");

    // NDIS_PHYSICAL_MEDIUM values.
    private const int PhysicalMediumUnspecified = 0;
    private const int PhysicalMediumWirelessLan = 1;
    private const int PhysicalMediumNative80211 = 9;
    private const int PhysicalMediumBluetooth = 10;
    private const int PhysicalMediumWirelessWan = 8;
    private const int PhysicalMedium802_3 = 14;

    private static readonly HashSet<string> PhysicalEnumerators = new(StringComparer.OrdinalIgnoreCase)
    {
        "PCI", "PCIE", "USB", "SD", "SDIO", "ACPI", "PCMCIA", "THUNDERBOLT",
    };

    private static readonly HashSet<string> SoftwareEnumerators = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROOT", "SWD", "SW", "VIRTUAL", "VMBUS", "UMBUSBUS", "BTHENUM", "BTHMS", "BTHPAN", "KDNIC",
    };

    private static readonly HashSet<string> VirtualServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "vwifibus", "vwifimp", "vms_mp", "vmsmp", "netvsc", "vmxnet3", "vmxnet2", "vboxnetadp",
        "vboxnetlwf", "tap0901", "tap6", "tapoas", "wintun", "wireguard", "wg", "npcap", "npf",
        "msloopback", "kdnic", "openvpn", "tap-windows6", "sonicwall", "fortinet", "pangp",
        "wgvirtualadapter", "microsoftvirtualwifi",
    };

    /// <summary>
    /// Virtual-vendor tokens matched inside hardware ids: a token only counts when the character
    /// before and after it is not an ASCII letter or digit, so <c>npf</c> does not match inside
    /// another word. <c>tap</c> additionally swallows a trailing digit run (<c>tap0901</c>).
    /// </summary>
    /// <remarks>
    /// This used to be a compiled regular expression; a hand written scan keeps the same semantics
    /// without pulling the regex engine (~0.5 MB) and its reflection paths into a Native AOT build.
    /// </remarks>
    private static readonly string[] VirtualHardwareIdTokens =
    {
        "root", "swd", "vwifimp", "vms_mp", "vmbus", "vmxnet", "vboxnet", "tap", "tun", "wintun",
        "wireguard", "npcap", "npf", "openvpn", "netvsc", "hyperv", "pseudo", "virtual", "loopback",
        "kdnic", "teredo", "isatap", "6to4",
    };

    private static readonly string[] VirtualNameKeywords =
    {
        "virtual", "hyper-v", "vmware", "virtualbox", "vbox", "loopback", "km-test", "tap-windows",
        "wintun", "wireguard", "npcap", "openvpn", "softether", "tunnel", "teredo", "isatap", "6to4",
        "wi-fi direct", "wifi direct", "hosted network", "bluetooth", "wan miniport", "kernel debug",
        "pseudo-interface", "vpn", "tap adapter", "proxy",
    };

    private static readonly string[] WifiNameKeywords =
    {
        "wi-fi", "wifi", "wireless", "wlan", "802.11", "80211", "wlan adapter",
    };

    private static readonly string[] EthernetNameKeywords =
    {
        "ethernet", "gigabit", "2.5g", "10g", "lan", "pcie", "nic",
    };

    // PCI vendors that exist only inside a virtual machine.
    private static readonly HashSet<string> VirtualPciVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "VEN_15AD", // VMware
        "VEN_1AF4", // virtio
        "VEN_1B36", // QEMU / Red Hat
        "VEN_1AB8", // Parallels
        "VEN_80EE", // VirtualBox
        "VEN_0B5B", // Parallels
    };

    /// <summary>
    /// Classifies one device node.
    /// </summary>
    /// <param name="record">Raw PnP + netcfg properties.</param>
    /// <param name="wlanInterfaceGuids">
    /// GUIDs returned by <c>WlanEnumInterfaces</c>. A netcfg instance GUID present in this set is the
    /// strongest possible proof that the device node backs a real WLAN interface.
    /// </param>
    /// <param name="denyList">Optional wildcard deny patterns matched against instance id and name.</param>
    public DeviceClassification Classify(
        PnpDeviceRecord record,
        IReadOnlyCollection<Guid>? wlanInterfaceGuids = null,
        IReadOnlyCollection<string>? denyList = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (MatchesDenyList(record, denyList))
        {
            return Verdict(DeviceCategory.Virtual, false, "deny-list",
                $"Device matches the configured deny list ({record.DeviceInstanceId}).");
        }

        // 1. Not a network device at all.
        if (record.ClassGuid is { } classGuid && classGuid != NetClassGuid && !IsUnknown(classGuid))
        {
            return Verdict(DeviceCategory.Other, false, "class-not-net",
                $"Device class {classGuid} is not the network adapter class.");
        }

        var instanceId = record.DeviceInstanceId ?? string.Empty;
        var enumerator = (record.EnumeratorName ?? DeriveEnumerator(instanceId)).Trim();
        var hardwareIds = record.HardwareIds ?? Array.Empty<string>();
        var compatibleIds = record.CompatibleIds ?? Array.Empty<string>();
        var allIds = hardwareIds.Concat(compatibleIds).ToList();
        var name = string.Join(' ', new[] { record.FriendlyName, record.DeviceDescription, record.BusReportedDeviceDescription }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        // 2. Virtual Wi-Fi bus (Microsoft Wi-Fi Direct Virtual Adapter / Hosted Network) is the most
        //    dangerous false positive: it also reports physical media type 9.
        if (ContainsVirtualWifiMarker(instanceId, allIds, enumerator, record.Service))
        {
            return Verdict(DeviceCategory.Virtual, false, "virtual-wifi-bus",
                "Device is the Microsoft Virtual Wi-Fi (Wi-Fi Direct / hosted network) adapter, " +
                "which must never be managed.");
        }

        // 3. Software enumerators.
        if (SoftwareEnumerators.Contains(enumerator))
        {
            var category = enumerator.Equals("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                           enumerator.Equals("BTHMS", StringComparison.OrdinalIgnoreCase) ||
                           enumerator.Equals("BTHPAN", StringComparison.OrdinalIgnoreCase)
                ? DeviceCategory.Other
                : DeviceCategory.Virtual;

            return Verdict(category, false, "software-enumerator",
                $"Enumerator '{enumerator}' is not a physical bus; the device is not managed.");
        }

        // 4. Known virtual driver services.
        if (!string.IsNullOrWhiteSpace(record.Service) && VirtualServices.Contains(record.Service))
        {
            return Verdict(DeviceCategory.Virtual, false, "virtual-service",
                $"Driver service '{record.Service}' is a virtual/software networking driver.");
        }

        // 5. Virtual hardware identifiers.
        if (MatchesVirtualHardwareId(allIds, out var matched))
        {
            return Verdict(DeviceCategory.Virtual, false, "virtual-hardware-id",
                $"Hardware id '{matched}' identifies a virtual/software adapter.");
        }

        if (MatchesVirtualPciVendor(allIds, out var vendor))
        {
            return Verdict(DeviceCategory.Virtual, false, "virtual-pci-vendor",
                $"PCI vendor '{vendor}' only exists inside a virtual machine.");
        }

        // 6. Name based virtual signals (used only as corroboration, never as the sole rule).
        var virtualName = VirtualNameKeywords.FirstOrDefault(k =>
            name.Contains(k, StringComparison.OrdinalIgnoreCase));

        // 7. Physical bus evidence.
        var onPhysicalBus = PhysicalEnumerators.Contains(enumerator) ||
                            record.BusTypeGuid is { } busGuid && (busGuid == PciBusType || busGuid == UsbBusType);

        // 8. Wireless interface correlation: netcfg GUID == WlanEnumInterfaces GUID.
        var wlanMatch = false;
        if (Guid.TryParse(record.NetCfgInstanceId, out var netCfgGuid))
        {
            if (wlanInterfaceGuids is { Count: > 0 } && wlanInterfaceGuids.Contains(netCfgGuid))
            {
                wlanMatch = true;
            }
        }

        var medium = record.PhysicalMediaType ?? PhysicalMediumUnspecified;
        var looksWifi = wlanMatch || medium is PhysicalMediumWirelessLan or PhysicalMediumNative80211;
        var looksEthernet = medium == PhysicalMedium802_3;

        if (!looksWifi && !looksEthernet)
        {
            // NDIS media type missing: fall back to names, and only when the device sits on a
            // physical bus (otherwise a virtual adapter with a Wi-Fi sounding name would slip in).
            var wifiName = WifiNameKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
            var ethName = EthernetNameKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

            if (onPhysicalBus && virtualName is null && wifiName)
            {
                looksWifi = true;
            }
            else if (onPhysicalBus && virtualName is null && ethName)
            {
                looksEthernet = true;
            }
        }

        if (!looksWifi && !looksEthernet)
        {
            var category = medium switch
            {
                PhysicalMediumBluetooth => DeviceCategory.Other,
                PhysicalMediumWirelessWan => DeviceCategory.Other,
                _ => DeviceCategory.Unknown,
            };

            return Verdict(category, false, "no-lan-media-type",
                $"Device does not report a Wi-Fi or Ethernet media type " +
                $"(physicalMediaType={(record.PhysicalMediaType?.ToString() ?? "n/a")}).");
        }

        // 9. Require the device to sit on a real bus unless the WLAN interface correlation proves it.
        if (!onPhysicalBus && !wlanMatch)
        {
            return Verdict(DeviceCategory.Virtual, false, "not-on-physical-bus",
                $"Enumerator '{enumerator}' is not recognised as a physical bus and the device is not " +
                "correlated with an active WLAN interface. Treated as not manageable on purpose.");
        }

        if (virtualName is not null)
        {
            return Verdict(DeviceCategory.Virtual, false, "virtual-name",
                $"Device name contains the virtual-adapter keyword '{virtualName}'.");
        }

        return looksWifi
            ? Verdict(DeviceCategory.PhysicalWifi, true, wlanMatch ? "wlansvc-correlation" : "physical-media-type",
                wlanMatch
                    ? "netcfg instance GUID matches an interface reported by WlanEnumInterfaces."
                    : $"Physical bus '{enumerator}' with wireless media type " +
                      $"{(record.PhysicalMediaType?.ToString() ?? "name-heuristic")}.")
            : Verdict(DeviceCategory.PhysicalEthernet, true, "physical-media-type",
                $"Physical bus '{enumerator}' with wired Ethernet media type " +
                $"{(record.PhysicalMediaType?.ToString() ?? "name-heuristic")}.");
    }

    public static bool IsUnknown(Guid guid) => guid == Guid.Empty;

    private static bool ContainsVirtualWifiMarker(
        string instanceId,
        IReadOnlyList<string> ids,
        string enumerator,
        string? service)
    {
        if (instanceId.Contains("vwifimp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Guid.TryParse(enumerator, out var enumeratorGuid) && enumeratorGuid == VirtualWifiEnumerator)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(service) &&
            (service.Equals("vwifibus", StringComparison.OrdinalIgnoreCase) ||
             service.Equals("vwifimp", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ids.Any(id => id.Contains("vwifimp", StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesVirtualHardwareId(IReadOnlyList<string> ids, out string matched)
    {
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (LooksVirtualHardwareId(id))
            {
                matched = id;
                return true;
            }
        }

        matched = string.Empty;
        return false;
    }

    private static bool MatchesVirtualPciVendor(IReadOnlyList<string> ids, out string vendor)
    {
        foreach (var id in ids)
        {
            var index = id.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var candidate = id.Substring(index, Math.Min(8, id.Length - index));
            if (VirtualPciVendors.Contains(candidate))
            {
                vendor = candidate;
                return true;
            }
        }

        vendor = string.Empty;
        return false;
    }

    private static bool MatchesDenyList(PnpDeviceRecord record, IReadOnlyCollection<string>? denyList)
    {
        if (denyList is null || denyList.Count == 0)
        {
            return false;
        }

        foreach (var pattern in denyList)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (WildcardMatch(record.DeviceInstanceId, pattern) ||
                WildcardMatch(record.FriendlyName, pattern) ||
                WildcardMatch(record.DeviceDescription, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WildcardMatch(string? value, string pattern)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        // Linear glob match: '*' matches any run (including empty), '?' matches exactly one
        // character, everything else is literal and compared case insensitively. Regex was used
        // before, but the pattern language is tiny and the regex engine costs ~0.5 MB in a Native
        // AOT build (and needs reflection for its compiled paths).
        var valueIndex = 0;
        var patternIndex = 0;
        var starPatternIndex = -1;
        var starValueIndex = 0;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == '?' || SameChar(pattern[patternIndex], value[valueIndex])))
            {
                valueIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starPatternIndex = patternIndex;
                starValueIndex = valueIndex;
                patternIndex++;
                continue;
            }

            if (starPatternIndex >= 0)
            {
                // Backtrack: let the last '*' swallow one more character.
                patternIndex = starPatternIndex + 1;
                valueIndex = ++starValueIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static bool SameChar(char left, char right) =>
        left == right || char.ToUpperInvariant(left) == char.ToUpperInvariant(right);

    /// <summary>Token scan used in place of the previous regex (see <see cref="VirtualHardwareIdTokens"/>).</summary>
    private static bool LooksVirtualHardwareId(string? hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId))
        {
            return false;
        }

        foreach (var token in VirtualHardwareIdTokens)
        {
            var from = 0;
            while (from <= hardwareId.Length - token.Length)
            {
                var index = hardwareId.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                var startOk = index == 0 || !IsAsciiAlphanumeric(hardwareId[index - 1]);
                var end = index + token.Length;

                // "tap\d*": a trailing digit run is part of the token (tap0901, tap6, ...).
                if (token == "tap")
                {
                    while (end < hardwareId.Length && IsAsciiDigit(hardwareId[end]))
                    {
                        end++;
                    }
                }

                var endOk = end >= hardwareId.Length || !IsAsciiAlphanumeric(hardwareId[end]);
                if (startOk && endOk)
                {
                    return true;
                }

                from = index + 1;
            }
        }

        return false;
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static bool IsAsciiAlphanumeric(char value) =>
        IsAsciiDigit(value) || value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// <summary>Derives the enumerator from an instance id such as <c>PCI\VEN_8086&amp;DEV_...</c>.</summary>
    public static string DeriveEnumerator(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return string.Empty;
        }

        var separator = instanceId.IndexOf('\\');
        return separator <= 0 ? instanceId : instanceId[..separator];
    }

    private static DeviceClassification Verdict(DeviceCategory category, bool physical, string rule, string reason) =>
        new() { Category = category, IsPhysical = physical, Rule = rule, Reason = reason };
}
