using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace NetworkGuardian.Windows.Network;

/// <summary>Tracks only endpoints explicitly bound to an old address, never wildcard UDP binds.</summary>
internal static unsafe class OutletEndpointTracker
{
    internal readonly record struct Endpoint(byte Protocol, string Address, uint Port, uint Pid, string Remote);

    internal static HashSet<Endpoint> Read(IReadOnlyCollection<IPAddress> addresses)
    {
        var addressSet = addresses.Select(Canonical).ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<Endpoint>();
        foreach (var family in new[] { 2, 23 })
        {
            if (!addresses.Any(a => (int)a.AddressFamily == family)) continue;
            ReadTable(false, family, pointer =>
            {
                var count = Marshal.ReadInt32(pointer);
                var size = family == 2 ? 12 : 28;
                for (var i = 0; i < count; i++)
                {
                    var row = (byte*)pointer + 4 + i * size;
                    var address = family == 2 ? new IPAddress(*(uint*)row) : new IPAddress(new ReadOnlySpan<byte>(row, 16));
                    var key = Canonical(address);
                    if (addressSet.Contains(key))
                        result.Add(new(17, key, *(uint*)(row + (family == 2 ? 4 : 20)),
                            *(uint*)(row + (family == 2 ? 8 : 24)), ""));
                }
            });
            ReadTable(true, family, pointer =>
            {
                var count = Marshal.ReadInt32(pointer);
                var size = family == 2 ? 24 : 56;
                for (var i = 0; i < count; i++)
                {
                    var row = (byte*)pointer + 4 + i * size;
                    var state = *(uint*)(row + (family == 2 ? 0 : 48));
                    var pid = *(uint*)(row + (family == 2 ? 20 : 52));
                    if (!TcpConnectionMigrator.IsLiveOwnedConnection(state, pid)) continue;
                    var address = family == 2 ? new IPAddress(*(uint*)(row + 4)) : new IPAddress(new ReadOnlySpan<byte>(row, 16));
                    var key = Canonical(address);
                    if (!addressSet.Contains(key)) continue;
                    var remote = family == 2 ? new IPAddress(*(uint*)(row + 12)) : new IPAddress(new ReadOnlySpan<byte>(row + 24, 16));
                    result.Add(new(6, key, *(uint*)(row + (family == 2 ? 8 : 20)), pid,
                        $"{Canonical(remote)}:{*(uint*)(row + (family == 2 ? 16 : 44))}"));
                }
            });
        }
        return result;
    }

    internal static string Canonical(IPAddress address) => Convert.ToHexString(address.GetAddressBytes());

    private static void ReadTable(bool tcp, int family, Action<nint> read)
    {
        var size = 0;
        var first = GetTable(tcp, family, 0, ref size);
        if (first is not (0 or 122)) throw new Win32Exception((int)first);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (size < 4) return;
            var capacity = size;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                var status = GetTable(tcp, family, buffer, ref size);
                if (status == 122) continue;
                if (status != 0) throw new Win32Exception((int)status);
                var rowSize = tcp ? (family == 2 ? 24 : 56) : (family == 2 ? 12 : 28);
                var count = Marshal.ReadInt32(buffer);
                if (count < 0 || count > (capacity - 4) / rowSize) throw new InvalidDataException("Invalid endpoint table length.");
                read(buffer);
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Endpoint table changed repeatedly during enumeration.");
    }

    private static uint GetTable(bool tcp, int family, nint buffer, ref int size) => tcp
        ? GetExtendedTcpTable(buffer, ref size, false, family, 5, 0)
        : GetExtendedUdpTable(buffer, ref size, false, family, 1, 0);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(nint buffer, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint buffer, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);
}
