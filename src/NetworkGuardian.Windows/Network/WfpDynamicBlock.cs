using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetworkGuardian.Windows.Network;

/// <summary>All filters and the private sublayer belong to one nonpersistent engine session.</summary>
internal sealed unsafe class WfpDynamicBlock : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly Guid _sublayer = Guid.NewGuid();
    internal List<ulong> FilterIds { get; } = new();

    private static readonly Guid LocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid Protocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid LocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    private static readonly Guid[] LayersV4 =
    {
        new("5926dfc8-e3cf-4426-a283-dc393f5d0f9d"), // inbound transport
        new("09e61aea-d214-46e2-9b21-b26b0b2f28c8"), // outbound transport
        new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), // ALE receive/accept
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), // ALE connect
    };
    private static readonly Guid[] LayersV6 =
    {
        new("634a869f-fc23-4b90-b0c1-bf620a36ae6f"),
        new("e1735bde-013f-4655-b351-a49e15762df0"),
        new("a3b42c97-9f04-4672-b87e-cee9c483257f"),
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"),
    };

    internal WfpDynamicBlock(IReadOnlyCollection<IPAddress> addresses, ushort? testLocalPort = null)
    {
        if (addresses.Count is 0 or > 32) throw new ArgumentException("Expected 1..32 exact local addresses.", nameof(addresses));
        fixed (char* name = "NetworkGuardian outlet migration (dynamic)")
        {
            var session = new Session { Key = Guid.NewGuid(), Display = new() { Name = (nint)name },
                Flags = 1, TransactionTimeout = 1000 }; // FWPM_SESSION_FLAG_DYNAMIC
            Check(FwpmEngineOpen0(0, 10, 0, ref session, out var engine));
            _engine = engine;
            try
            {
                Check(FwpmTransactionBegin0(_engine, 0));
                var sublayer = new Sublayer { Key = _sublayer, Display = new() { Name = (nint)name }, Weight = 0x7fff };
                Check(FwpmSubLayerAdd0(_engine, ref sublayer, 0));
                foreach (var address in addresses)
                {
                    if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
                        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                        throw new ArgumentException("Wildcard addresses are never valid filter targets.", nameof(addresses));
                    foreach (var protocol in new byte[] { 6, 17 })
                        foreach (var layer in address.AddressFamily == AddressFamily.InterNetwork ? LayersV4 : LayersV6)
                            Add(address, protocol, layer, (nint)name, testLocalPort);
                }
                Check(FwpmTransactionCommit0(_engine));
            }
            catch
            {
                // Closing a dynamic session rolls back both pending and committed objects.
                _engine.Dispose();
                throw;
            }
        }
    }

    private void Add(IPAddress address, byte protocol, Guid layer, nint name, ushort? port)
    {
        var bytes = address.GetAddressBytes();
        fixed (byte* addressBytes = bytes)
        {
            var conditions = stackalloc Condition[3];
            conditions[0] = new() { Field = LocalAddress, Value = address.AddressFamily == AddressFamily.InterNetwork
                ? new() { Type = 3, UInt32 = BinaryPrimitives.ReadUInt32BigEndian(bytes) }
                : new() { Type = 11, Pointer = (nint)addressBytes } };
            conditions[1] = new() { Field = Protocol, Value = new() { Type = 1, UInt8 = protocol } };
            conditions[2] = new() { Field = LocalPort, Value = new() { Type = 2, UInt16 = port ?? 0 } };
            var filter = new Filter { Key = Guid.NewGuid(), Display = new() { Name = name },
                Layer = layer, Sublayer = _sublayer, ConditionCount = port.HasValue ? 3u : 2u,
                Conditions = (nint)conditions, Action = new() { Type = 0x1001 } }; // FWP_ACTION_BLOCK
            Check(FwpmFilterAdd0(_engine, ref filter, 0, out var id));
            FilterIds.Add(id);
        }
    }

    internal static bool FilterExists(ulong id)
    {
        Check(FwpmEngineOpen0(0, 10, 0, 0, out var engine));
        using (engine)
        {
            var result = FwpmFilterGetById0(engine, id, out var filter);
            if (filter != 0) FwpmFreeMemory0(ref filter);
            if (result == 0x80320003) return false; // FWP_E_FILTER_NOT_FOUND
            Check(result);
            return true;
        }
    }

    public void Dispose() => _engine.Dispose();

    private static void Check(uint result)
    {
        if (result != 0) throw new Win32Exception(unchecked((int)result), $"WFP failed: 0x{result:X8}");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayData { public nint Name; public nint Description; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Blob { public uint Size; public nint Data; }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct Value
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public byte UInt8;
        [FieldOffset(8)] public ushort UInt16;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public nint Pointer;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Condition { public Guid Field; public uint Match; public Value Value; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Action { public uint Type; public Guid Key; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Filter
    {
        public Guid Key; public DisplayData Display; public uint Flags; public nint Provider;
        public Blob ProviderData; public Guid Layer; public Guid Sublayer; public Value Weight;
        public uint ConditionCount; public nint Conditions; public Action Action;
        // The native union is 16 bytes with 8-byte alignment.
        public ulong ContextLow; public ulong ContextHigh; public nint Reserved; public ulong Id; public Value EffectiveWeight;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Sublayer
    {
        public Guid Key; public DisplayData Display; public uint Flags; public nint Provider;
        public Blob ProviderData; public ushort Weight;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Session
    {
        public Guid Key; public DisplayData Display; public uint Flags; public uint TransactionTimeout;
        public uint ProcessId; public nint Sid; public nint Username; public int KernelMode;
    }
    internal sealed class EngineHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public EngineHandle() : base(true) { }
        protected override bool ReleaseHandle() => FwpmEngineClose0(handle) == 0;
    }

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmEngineOpen0(nint server, uint auth, nint identity, ref Session session, out EngineHandle engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmEngineOpen0(nint server, uint auth, nint identity, nint session, out EngineHandle engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmEngineClose0(nint engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmSubLayerAdd0(EngineHandle engine, ref Sublayer sublayer, nint security);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmFilterAdd0(EngineHandle engine, ref Filter filter, nint security, out ulong id);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmTransactionBegin0(EngineHandle engine, uint flags);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmTransactionCommit0(EngineHandle engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmFilterGetById0(EngineHandle engine, ulong id, out nint filter);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern void FwpmFreeMemory0(ref nint memory);
}
