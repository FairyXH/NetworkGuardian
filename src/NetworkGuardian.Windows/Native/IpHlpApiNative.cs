using System.Net;
using System.Runtime.InteropServices;

namespace NetworkGuardian.Windows.Native;

/// <summary>P/Invoke declarations for the IP Helper API (<c>iphlpapi.dll</c>).</summary>
internal static class IpHlpApiNative
{
    internal const string Dll = "iphlpapi.dll";

    internal const uint AF_UNSPEC = 0;
    internal const uint AF_INET = 2;
    internal const uint AF_INET6 = 23;

    internal const uint GAA_FLAG_SKIP_UNICAST = 0x0001;
    internal const uint GAA_FLAG_SKIP_ANYCAST = 0x0002;
    internal const uint GAA_FLAG_SKIP_MULTICAST = 0x0004;
    internal const uint GAA_FLAG_SKIP_DNS_SERVER = 0x0008;
    internal const uint GAA_FLAG_INCLUDE_PREFIX = 0x0010;
    internal const uint GAA_FLAG_SKIP_FRIENDLY_NAME = 0x0020;
    internal const uint GAA_FLAG_INCLUDE_WINS_INFO = 0x0040;
    internal const uint GAA_FLAG_INCLUDE_GATEWAYS = 0x0080;
    internal const uint GAA_FLAG_INCLUDE_ALL_INTERFACES = 0x0100;
    internal const uint GAA_FLAG_INCLUDE_ALL_COMPARTMENTS = 0x0200;
    internal const uint GAA_FLAG_INCLUDE_TUNNEL_BINDINGORDER = 0x0400;

    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_BUFFER_OVERFLOW = 111;
    internal const int ERROR_NO_DATA = 232;
    internal const int ERROR_NOT_ENOUGH_MEMORY = 8;

    internal const uint IfTypeOther = 1;
    internal const uint IfTypeEthernetCsmacd = 6;
    internal const uint IfTypePpp = 23;
    internal const uint IfTypeSoftwareLoopback = 24;
    internal const uint IfTypeTunnel = 131;
    internal const uint IfTypeIeee80211 = 71;
    internal const uint IfTypeWwanpp = 243;

    internal const int IfOperStatusUp = 1;
    internal const int IfOperStatusDown = 2;
    internal const int IfOperStatusTesting = 3;
    internal const int IfOperStatusUnknown = 4;
    internal const int IfOperStatusDormant = 5;
    internal const int IfOperStatusNotPresent = 6;
    internal const int IfOperStatusLowerLayerDown = 7;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SOCKET_ADDRESS
    {
        public IntPtr lpSockaddr;
        public int iSockaddrLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SOCKADDR_IN
    {
        public ushort sin_family;
        public ushort sin_port;
        public uint sin_addr;
        public ulong sin_zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SOCKADDR_IN6
    {
        public ushort sin6_family;
        public ushort sin6_port;
        public uint sin6_flowinfo;
        public fixed byte sin6_addr[16];
        public uint sin6_scope_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IP_ADAPTER_UNICAST_ADDRESS
    {
        public ulong Alignment;
        public IntPtr Next;
        public SOCKET_ADDRESS Address;
        public int PrefixOrigin;
        public int SuffixOrigin;
        public int DadState;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint LeaseLifetime;
        public byte OnLinkPrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IP_ADAPTER_GATEWAY_ADDRESS
    {
        public ulong Alignment;
        public IntPtr Next;
        public SOCKET_ADDRESS Address;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IP_ADAPTER_DNS_SERVER_ADDRESS
    {
        public ulong Alignment;
        public IntPtr Next;
        public SOCKET_ADDRESS Address;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IP_ADAPTER_ADDRESSES
    {
        /// <summary>Union of ULONGLONG Alignment / (ULONG Length + IF_INDEX IfIndex).</summary>
        public ulong Alignment;

        public IntPtr Next;
        public IntPtr AdapterName;
        public IntPtr FirstUnicastAddress;
        public IntPtr FirstAnycastAddress;
        public IntPtr FirstMulticastAddress;
        public IntPtr FirstDnsServerAddress;
        public IntPtr DnsSuffix;
        public IntPtr Description;
        public IntPtr FriendlyName;

        public unsafe fixed byte PhysicalAddress[8];
        public uint PhysicalAddressLength;
        public uint Flags;
        public uint Mtu;
        public uint IfType;
        public uint OperStatus;
        public uint Ipv6IfIndex;

        public unsafe fixed uint ZoneIndices[16];
        public IntPtr FirstPrefix;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public IntPtr FirstWinsServerAddress;
        public IntPtr FirstGatewayAddress;
        public uint Ipv4Metric;
        public uint Ipv6Metric;
        public ulong Luid;
        public SOCKET_ADDRESS Dhcpv4Server;
        public uint CompartmentId;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public uint TunnelType;
        public SOCKET_ADDRESS Dhcpv6Server;

        public unsafe fixed byte Dhcpv6ClientDuid[16];
        public uint Dhcpv6ClientDuidLength;
        public uint Dhcpv6Iaid;
        public IntPtr FirstDnsSuffix;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IP_ADDRESS_PREFIX
    {
        public SOCKADDR_INET Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct SOCKADDR_INET
    {
        [FieldOffset(0)] public SOCKADDR_IN Ipv4;
        [FieldOffset(0)] public SOCKADDR_IN6 Ipv6;
        [FieldOffset(0)] public ushort si_family;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_IPFORWARD_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IP_ADDRESS_PREFIX DestinationPrefix;
        public SOCKADDR_INET NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public uint Origin;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_IPFORWARD_TABLE2_HEADER
    {
        public uint NumEntries;

        /// <summary>Padding so that the following row array starts at offset 8 as in the C header.</summary>
        public uint Reserved;
    }

    [DllImport(Dll, SetLastError = true)]
    internal static extern uint GetAdaptersAddresses(
        uint family,
        uint flags,
        IntPtr reserved,
        IntPtr addresses,
        ref uint sizePointer);

    [DllImport(Dll, SetLastError = true)]
    internal static extern uint GetIpForwardTable2(uint family, out IntPtr table);

    [DllImport(Dll, SetLastError = true)]
    internal static extern uint GetBestRoute2(
        IntPtr interfaceLuid,
        uint interfaceIndex,
        IntPtr sourceAddress,
        IntPtr destinationAddress,
        uint addressSortOptions,
        IntPtr bestRoute,
        IntPtr bestSourceAddress);

    [DllImport(Dll)]
    internal static extern void FreeMibTable(IntPtr memory);

    /// <summary>Converts a native SOCKET_ADDRESS into a managed <see cref="IPEndPoint"/>.</summary>
    internal static IPAddress? ToIpAddress(SOCKET_ADDRESS address)
    {
        if (address.lpSockaddr == IntPtr.Zero || address.iSockaddrLength <= 0)
        {
            return null;
        }

        var family = (ushort)Marshal.ReadInt16(address.lpSockaddr);

        if (family == AF_INET)
        {
            var native = Marshal.PtrToStructure<SOCKADDR_IN>(address.lpSockaddr);
            var bytes = BitConverter.GetBytes(native.sin_addr);
            return new IPAddress(bytes);
        }

        if (family == AF_INET6)
        {
            var native = Marshal.PtrToStructure<SOCKADDR_IN6>(address.lpSockaddr);
            var bytes = new byte[16];
            unsafe
            {
                Marshal.Copy((IntPtr)native.sin6_addr, bytes, 0, 16);
            }

            return new IPAddress(bytes, (long)native.sin6_scope_id);
        }

        return null;
    }

    internal static IPAddress? ToIpAddress(SOCKADDR_INET address)
    {
        switch (address.si_family)
        {
            case (ushort)AF_INET:
                return new IPAddress(BitConverter.GetBytes(address.Ipv4.sin_addr));
            case (ushort)AF_INET6:
                var bytes = new byte[16];
                unsafe
                {
                    // IPv6 sin6_addr is a fixed size buffer of an already fixed local struct.
                    Marshal.Copy((IntPtr)address.Ipv6.sin6_addr, bytes, 0, 16);
                }

                return new IPAddress(bytes, (long)address.Ipv6.sin6_scope_id);
            default:
                return null;
        }
    }
}
