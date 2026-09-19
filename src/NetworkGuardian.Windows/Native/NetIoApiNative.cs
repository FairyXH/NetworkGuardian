using System.Runtime.InteropServices;

namespace NetworkGuardian.Windows.Native;

/// <summary>
/// P/Invoke declarations for <c>netioapi.h</c> interface management functions.
/// <c>MIB_IPINTERFACE_ROW</c> was transcribed field by field from
/// <c>Windows Kits\10\Include\10.0.26100.0\shared\netioapi.h</c> and is size asserted at runtime
/// before any setter is allowed to run.
/// </summary>
internal static class NetIoApiNative
{
    internal const string Dll = "iphlpapi.dll";

    internal const int ScopeLevelCount = 16;

    /// <summary>sizeof(MIB_IPINTERFACE_ROW) on both x86 and x64 (8-byte alignment of InterfaceLuid).</summary>
    internal const int MibIpInterfaceRowSize = 168;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NL_INTERFACE_OFFLOAD_ROD
    {
        // Eight one-bit BOOLEAN fields in nldef.h share one byte.
        public byte Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_IPINTERFACE_ROW
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;

        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;

        public int RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;

        public int LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;

        public unsafe fixed uint ZoneIndices[ScopeLevelCount];

        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;

        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;

        public uint ReachableTime;
        public NL_INTERFACE_OFFLOAD_ROD TransmitOffload;
        public NL_INTERFACE_OFFLOAD_ROD ReceiveOffload;
        public byte DisableDefaultRoutes;
    }

    [DllImport(Dll, SetLastError = true)]
    internal static extern void InitializeIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    [DllImport(Dll, SetLastError = true)]
    internal static extern uint GetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    [DllImport(Dll, SetLastError = true)]
    internal static extern uint SetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);
}
