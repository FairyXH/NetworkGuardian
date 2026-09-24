using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Windows.Native;
using static NetworkGuardian.Windows.Native.IpHlpApiNative;

namespace NetworkGuardian.Windows.Network;

/// <summary>
/// Interface and route inventory built on <c>GetAdaptersAddresses</c> and <c>GetIpForwardTable2</c>.
/// The provider only reads; it never changes user routing unless metric management is explicitly
/// enabled in configuration.
/// </summary>
public sealed class NetworkInterfaceProvider : INetworkInterfaceProvider
{
    private readonly ILogger<NetworkInterfaceProvider> _logger;
    private readonly Func<Guid, PnpDeviceRecord?> _deviceLookup;
    private readonly Func<IReadOnlyCollection<Guid>> _wlanGuidProvider;
    private readonly NetworkDeviceClassifier _classifier;
    private readonly object _metricGate = new();

    public NetworkInterfaceProvider(
        Func<Guid, PnpDeviceRecord?>? deviceLookup = null,
        Func<IReadOnlyCollection<Guid>>? wlanGuidProvider = null,
        ILogger<NetworkInterfaceProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<NetworkInterfaceProvider>.Instance;
        _deviceLookup = deviceLookup ?? (_ => null);
        _wlanGuidProvider = wlanGuidProvider ?? (() => Array.Empty<Guid>());
        _classifier = new NetworkDeviceClassifier();
    }

    public IReadOnlyList<InterfaceRuntimeState> GetInterfaces()
    {
        var results = new List<InterfaceRuntimeState>();
        var addresses = ReadAdapterAddresses();
        if (addresses == IntPtr.Zero)
        {
            return results;
        }

        try
        {
            var wlanGuids = _wlanGuidProvider().ToHashSet();
            var pointer = addresses;

            while (pointer != IntPtr.Zero)
            {
                var adapter = Marshal.PtrToStructure<IP_ADAPTER_ADDRESSES>(pointer);

                try
                {
                    results.Add(Build(adapter, wlanGuids));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to project adapter at 0x{Pointer:X}", pointer.ToInt64());
                }

                pointer = adapter.Next;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(addresses);
        }

        return results;
    }

    private InterfaceRuntimeState Build(IP_ADAPTER_ADDRESSES adapter, HashSet<Guid> wlanGuids)
    {
        var adapterName = adapter.AdapterName == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringAnsi(adapter.AdapterName) ?? string.Empty;

        var netCfgGuid = ParseAdapterGuid(adapterName);
        var friendlyName = ReadString(adapter.FriendlyName);
        var description = ReadString(adapter.Description);
        var mac = FormatPhysicalAddress(adapter);

        var deviceRecord = netCfgGuid is { } guid ? _deviceLookup(guid) : null;
        var classification = deviceRecord is null
            ? null
            : _classifier.Classify(deviceRecord, wlanGuids);

        var ipv4 = new List<string>();
        var ipv6 = new List<string>();
        var gateways = new List<string>();
        var dns = new List<string>();

        WalkUnicast(adapter.FirstUnicastAddress, ipv4, ipv6);
        WalkAddresses(adapter.FirstGatewayAddress, gateways, ipv6Only: false);
        WalkAddresses(adapter.FirstDnsServerAddress, dns, ipv6Only: false);

        var kind = DetermineKind(adapter, classification, netCfgGuid, wlanGuids);

        var isUp = adapter.OperStatus == IfOperStatusUp;

        return new InterfaceRuntimeState
        {
            Id = adapter.Luid != 0 ? $"luid:{adapter.Luid}" : $"guid:{netCfgGuid ?? Guid.Empty}",
            Name = string.IsNullOrWhiteSpace(friendlyName) ? description : friendlyName,
            Description = description,
            Kind = kind,
            WlanInterfaceGuid = kind == InterfaceKind.Wifi ? netCfgGuid : null,
            InterfaceIndex = adapter.IfIndex,
            DeviceInstanceId = deviceRecord?.DeviceInstanceId,
            IsUp = isUp,
            IsPhysicalDevice = classification?.IsPhysical,
            IsPresent = adapter.OperStatus != IfOperStatusNotPresent,
            HasUsableIpv4 = ipv4.Count > 0 && !ipv4.All(IsApipa),
            HasIpv6 = ipv6.Count > 0,
            HasDefaultGateway = gateways.Count > 0,
            InterfaceMetric = (int)adapter.Ipv4Metric,
            RouteMetric = null,
            IsDefaultRoute = false,
            Ipv4Addresses = ipv4,
            Ipv6Addresses = ipv6,
            Ipv4Gateways = gateways,
            DnsServers = dns,
            MacAddress = mac,
            SpeedBitsPerSecond = adapter.TransmitLinkSpeed,
            ObservedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static InterfaceKind DetermineKind(
        IP_ADAPTER_ADDRESSES adapter,
        DeviceClassification? classification,
        Guid? netCfgGuid,
        HashSet<Guid> wlanGuids)
    {
        if (classification is { } verdict)
        {
            switch (verdict.Category)
            {
                case DeviceCategory.Virtual:
                    return InterfaceKind.Virtual;
                case DeviceCategory.PhysicalWifi:
                    return InterfaceKind.Wifi;
                case DeviceCategory.PhysicalEthernet:
                    return InterfaceKind.Ethernet;
            }
        }

        if (netCfgGuid is { } guid && wlanGuids.Contains(guid))
        {
            return InterfaceKind.Wifi;
        }

        return adapter.IfType switch
        {
            IfTypeIeee80211 => InterfaceKind.Wifi,
            IfTypeEthernetCsmacd => InterfaceKind.Ethernet,
            IfTypeSoftwareLoopback => InterfaceKind.Loopback,
            IfTypeTunnel => InterfaceKind.Tunnel,
            IfTypePpp => InterfaceKind.Tunnel,
            _ => InterfaceKind.Other,
        };
    }

    public IReadOnlyList<DefaultRouteInfo> GetDefaultRoutes()
    {
        var results = new List<DefaultRouteInfo>();
        var adapterInfo = ReadAdapterAliases();
        var unmatchedRoutes = 0;
        var status = GetIpForwardTable2(AF_INET, out var table);
        if (status != ERROR_SUCCESS || table == IntPtr.Zero)
        {
            _logger.LogDebug("GetIpForwardTable2 failed: {Error}", Win32Error.Describe((int)status));
            return results;
        }

        try
        {
            var header = Marshal.PtrToStructure<MIB_IPFORWARD_TABLE2_HEADER>(table);
            var rowSize = Marshal.SizeOf<MIB_IPFORWARD_ROW2>();
            var rowOffset = 8; // NumEntries + 4 bytes padding to satisfy 8-byte row alignment

            for (var i = 0; i < header.NumEntries; i++)
            {
                var rowPointer = IntPtr.Add(table, rowOffset + (i * rowSize));
                var row = Marshal.PtrToStructure<MIB_IPFORWARD_ROW2>(rowPointer);

                if (row.DestinationPrefix.PrefixLength != 0)
                {
                    continue;
                }

                var nextHop = ToIpAddress(row.NextHop);
                // Both keys are documented identifiers for the same interface; the LUID is preferred
                // because it is unambiguous across address families.
                var hasAdapter = adapterInfo.ByLuid.TryGetValue(row.InterfaceLuid, out var adapter);
                if (!hasAdapter)
                {
                    hasAdapter = adapterInfo.ByIndex.TryGetValue(row.InterfaceIndex, out adapter);
                }

                results.Add(new DefaultRouteInfo
                {
                    AddressFamily = row.NextHop.si_family,
                    InterfaceIndex = row.InterfaceIndex,
                    InterfaceLuid = row.InterfaceLuid,
                    NextHop = nextHop?.ToString() ?? "0.0.0.0",
                    RouteMetric = (int)row.Metric,
                    InterfaceMetric = hasAdapter ? adapter.Metric : null,
                    InterfaceAlias = hasAdapter ? adapter.Alias : null,
                });

                if (!hasAdapter)
                {
                    unmatchedRoutes++;
                }
            }

            if (unmatchedRoutes > 0)
            {
                _logger.LogDebug(
                    "{Unmatched} of {Total} default route(s) could not be mapped to an adapter name",
                    unmatchedRoutes, results.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate the IP forward table");
        }
        finally
        {
            FreeMibTable(table);
        }

        return results;
    }

    /// <summary>
    /// Maps interface LUIDs to the friendly name and IPv4 metric reported by GetAdaptersAddresses, so
    /// the route table can be presented with interface names instead of raw LUIDs.
    /// </summary>
    private (Dictionary<ulong, (string Alias, int Metric)> ByLuid, Dictionary<uint, (string Alias, int Metric)> ByIndex)
        ReadAdapterAliases()
    {
        var byLuid = new Dictionary<ulong, (string Alias, int Metric)>();
        var byIndex = new Dictionary<uint, (string Alias, int Metric)>();
        var result = (byLuid, byIndex);

        var addresses = ReadAdapterAddresses();
        if (addresses == IntPtr.Zero)
        {
            return result;
        }

        try
        {
            var pointer = addresses;
            var guard = 0;

            while (pointer != IntPtr.Zero && guard++ < 512)
            {
                var adapter = Marshal.PtrToStructure<IP_ADAPTER_ADDRESSES>(pointer);
                var friendlyName = ReadString(adapter.FriendlyName);

                if (!string.IsNullOrEmpty(friendlyName))
                {
                    var entry = (friendlyName, (int)adapter.Ipv4Metric);

                    if (adapter.Luid != 0)
                    {
                        byLuid[adapter.Luid] = entry;
                    }

                    if (adapter.IfIndex != 0)
                    {
                        byIndex[adapter.IfIndex] = entry;
                    }
                }

                pointer = adapter.Next;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(addresses);
        }

        return result;
    }

    /// <summary>
    /// Applies the configured interface metrics. Requires administrator rights and is disabled by
    /// default because changing metrics alters the user's routing preference.
    /// </summary>
    public Task<IReadOnlyList<string>> ApplyInterfaceMetricsAsync(GuardianConfig config, CancellationToken cancellationToken)
    {
        if (!config.General.ManageInterfaceMetrics)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                new[] { "Interface metric management is disabled in configuration." });
        }

        return ApplyInterfaceMetricsAsync(
            config.General.PreferredEthernetMetric,
            config.General.PreferredWifiMetric,
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> ApplyInterfaceMetricsAsync(
        int ethernetMetric,
        int wifiMetric,
        CancellationToken cancellationToken)
    {
        var metrics = GetInterfaces()
            .Where(state => state.Kind is InterfaceKind.Ethernet or InterfaceKind.Wifi)
            .ToDictionary(
                state => state.Id,
                state => state.Kind == InterfaceKind.Ethernet ? ethernetMetric : wifiMetric,
                StringComparer.OrdinalIgnoreCase);
        return ApplyInterfaceMetricsAsync(metrics, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ApplyInterfaceMetricsAsync(
        IReadOnlyDictionary<string, int> metricsByInterfaceId,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var notes = new List<string>();

            lock (_metricGate)
            {
                var actualSize = Marshal.SizeOf<NetIoApiNative.MIB_IPINTERFACE_ROW>();
                if (actualSize != NetIoApiNative.MibIpInterfaceRowSize)
                {
                    var message =
                        $"Refusing to change interface metrics: MIB_IPINTERFACE_ROW managed size {actualSize} " +
                        $"does not match the expected native size {NetIoApiNative.MibIpInterfaceRowSize}.";
                    _logger.LogError("{Message}", message);
                    notes.Add(message);
                    return notes;
                }

                foreach (var state in GetInterfaces())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (state.Kind is not (InterfaceKind.Ethernet or InterfaceKind.Wifi) ||
                        state.IsPhysicalDevice != true)
                    {
                        continue;
                    }

                    var luid = ParseLuid(state.Id);
                    if (luid is null)
                    {
                        continue;
                    }

                    if (!metricsByInterfaceId.TryGetValue(state.Id, out var desired))
                    {
                        continue;
                    }

                    if (state.InterfaceMetric == desired)
                    {
                        continue;
                    }

                    var row = new NetIoApiNative.MIB_IPINTERFACE_ROW();
                    // SetIpInterfaceEntry requires the initializer's "unchanged" sentinel values.
                    // Feeding the complete row returned by GetIpInterfaceEntry writes read-only fields
                    // back to Windows and is rejected with ERROR_INVALID_PARAMETER.
                    NetIoApiNative.InitializeIpInterfaceEntry(ref row);
                    row.Family = (ushort)AF_INET;
                    row.InterfaceLuid = luid.Value;
                    row.UseAutomaticMetric = 0;
                    row.Metric = (uint)desired;

                    var setStatus = NetIoApiNative.SetIpInterfaceEntry(ref row);
                    if (setStatus == ERROR_SUCCESS)
                    {
                        notes.Add($"{state.Name}: metric set to {desired}");
                        _logger.LogInformation("Interface metric for {Name} set to {Metric}", state.Name, desired);
                    }
                    else
                    {
                        notes.Add($"{state.Name}: SetIpInterfaceEntry failed ({Win32Error.Describe((int)setStatus)})");
                        _logger.LogWarning("SetIpInterfaceEntry failed for {Name}: {Error}",
                            state.Name, Win32Error.Describe((int)setStatus));
                    }
                }

                ApplyDefaultRouteMetrics(metricsByInterfaceId, notes, cancellationToken);
            }

            return notes;
        }, cancellationToken);
    }

    private void ApplyDefaultRouteMetrics(
        IReadOnlyDictionary<string, int> metricsByInterfaceId,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var managedLuids = metricsByInterfaceId.Keys
            .Select(ParseLuid)
            .OfType<ulong>()
            .ToHashSet();
        if (managedLuids.Count == 0)
        {
            return;
        }

        var status = GetIpForwardTable2(AF_INET, out var table);
        if (status != ERROR_SUCCESS || table == IntPtr.Zero)
        {
            notes.Add($"Default route metric read failed: {Win32Error.Describe((int)status)}");
            return;
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MIB_IPFORWARD_ROW2>();
            var firstRow = IntPtr.Add(table, Marshal.SizeOf<MIB_IPFORWARD_TABLE2_HEADER>());

            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<MIB_IPFORWARD_ROW2>(IntPtr.Add(firstRow, index * rowSize));
                if (row.DestinationPrefix.PrefixLength != 0 ||
                    row.DestinationPrefix.Prefix.si_family != AF_INET ||
                    !managedLuids.Contains(row.InterfaceLuid) ||
                    row.Metric == 1)
                {
                    continue;
                }

                var oldMetric = row.Metric;
                row.Metric = 1;
                var setStatus = SetIpForwardEntry2(ref row);
                if (setStatus == ERROR_SUCCESS)
                {
                    notes.Add($"Default route luid:{row.InterfaceLuid}: route metric {oldMetric} -> 1");
                }
                else
                {
                    notes.Add($"Default route luid:{row.InterfaceLuid}: {Win32Error.Describe((int)setStatus)}");
                    _logger.LogWarning(
                        "SetIpForwardEntry2 failed for LUID {Luid}: {Error}",
                        row.InterfaceLuid,
                        Win32Error.Describe((int)setStatus));
                }
            }
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>Reads the primary IPv4 default route target so the probe can use the right source.</summary>
    public DefaultRouteInfo? GetBestDefaultRoute()
    {
        var routes = GetDefaultRoutes();
        if (routes.Count == 0)
        {
            return null;
        }

        var interfaces = GetInterfaces();
        return routes
            .Select(route => route with
            {
                InterfaceAlias = interfaces.FirstOrDefault(i => i.Id == $"luid:{route.InterfaceLuid}")?.Name,
                InterfaceMetric = interfaces.FirstOrDefault(i => i.Id == $"luid:{route.InterfaceLuid}")?.InterfaceMetric,
            })
            .OrderBy(route => route.EffectiveMetric ?? int.MaxValue)
            .FirstOrDefault();
    }

    private static ulong? ParseLuid(string id)
    {
        if (id.StartsWith("luid:", StringComparison.Ordinal) &&
            ulong.TryParse(id.AsSpan(5), out var luid))
        {
            return luid;
        }

        return null;
    }

    private static Guid? ParseAdapterGuid(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return null;
        }

        var trimmed = adapterName.Trim('{', '}');
        return Guid.TryParse(trimmed, out var guid) ? guid : null;
    }

    private static bool IsApipa(string address) => address.StartsWith("169.254.", StringComparison.Ordinal);

    private static string ReadString(IntPtr pointer) =>
        pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(pointer) ?? string.Empty;

    private static unsafe string FormatPhysicalAddress(IP_ADAPTER_ADDRESSES adapter)
    {
        var length = (int)Math.Min(adapter.PhysicalAddressLength, 8u);
        if (length <= 0)
        {
            return string.Empty;
        }

        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = adapter.PhysicalAddress[i];
        }

        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    private static void WalkUnicast(IntPtr head, List<string> ipv4, List<string> ipv6)
    {
        var pointer = head;
        var guard = 0;
        while (pointer != IntPtr.Zero && guard++ < 256)
        {
            var entry = Marshal.PtrToStructure<IP_ADAPTER_UNICAST_ADDRESS>(pointer);
            var address = ToIpAddress(entry.Address);
            if (address is not null)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    ipv4.Add(address.ToString());
                }
                else if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    // Link local addresses are recorded but not treated as usable IPv6.
                    ipv6.Add(address.ToString());
                }
            }

            pointer = entry.Next;
        }
    }

    private static void WalkAddresses(IntPtr head, List<string> output, bool ipv6Only)
    {
        _ = ipv6Only;
        var pointer = head;
        var guard = 0;
        while (pointer != IntPtr.Zero && guard++ < 256)
        {
            var entry = Marshal.PtrToStructure<IP_ADAPTER_GATEWAY_ADDRESS>(pointer);
            var address = ToIpAddress(entry.Address);
            if (address is IPAddress value && value.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                output.Add(value.ToString());
            }

            pointer = entry.Next;
        }
    }

    private IntPtr ReadAdapterAddresses()
    {
        var size = 16 * 1024u;
        var buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var flags = GAA_FLAG_INCLUDE_GATEWAYS | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST |
                            GAA_FLAG_INCLUDE_PREFIX;

                var status = GetAdaptersAddresses(AF_UNSPEC, flags, IntPtr.Zero, buffer, ref size);
                if (status == ERROR_SUCCESS)
                {
                    return buffer;
                }

                if (status == ERROR_BUFFER_OVERFLOW)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal((int)size);
                    continue;
                }

                _logger.LogWarning("GetAdaptersAddresses failed: {Error}", Win32Error.Describe((int)status));
                Marshal.FreeHGlobal(buffer);
                return IntPtr.Zero;
            }

            _logger.LogWarning("GetAdaptersAddresses kept reporting ERROR_BUFFER_OVERFLOW");
            Marshal.FreeHGlobal(buffer);
            return IntPtr.Zero;
        }
        catch
        {
            Marshal.FreeHGlobal(buffer);
            throw;
        }
    }
}
