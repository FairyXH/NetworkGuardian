using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Configuration;

namespace NetworkGuardian.Windows.Network;

/// <summary>Closes IPv4 TCP connections that are still bound to a superseded outlet address.</summary>
public sealed class TcpConnectionMigrator
{
    private const int AfInet = 2;
    private const int OwnerPidAll = 5;
    private const uint DeleteTcb = 12;
    private readonly ILogger<TcpConnectionMigrator> _logger;

    public TcpConnectionMigrator(ILogger<TcpConnectionMigrator>? logger = null)
    {
        _logger = logger ?? NullLogger<TcpConnectionMigrator>.Instance;
    }

    public Task<int> CloseAsync(
        IReadOnlyCollection<string> oldLocalAddresses,
        ConnectionMigrationSettings settings,
        CancellationToken cancellationToken) => Task.Run(() => Close(oldLocalAddresses, settings, cancellationToken), cancellationToken);

    private int Close(
        IReadOnlyCollection<string> oldLocalAddresses,
        ConnectionMigrationSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Mode == ConnectionCutMode.Disabled || oldLocalAddresses.Count == 0)
        {
            return 0;
        }

        var oldAddresses = oldLocalAddresses
            .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(address => address?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(address => BitConverter.ToUInt32(address!.GetAddressBytes()))
            .ToHashSet();
        if (oldAddresses.Count == 0)
        {
            return 0;
        }

        var rows = ReadRows();
        var closed = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!oldAddresses.Contains(row.LocalAddress) || row.State is 1 or 2 or DeleteTcb)
            {
                continue;
            }

            var identity = ReadProcessIdentity(row.ProcessId);
            if (!ShouldClose(identity.Name, identity.Path, settings.Mode, settings.ProcessPatterns))
            {
                continue;
            }

            var delete = new TcpRow
            {
                State = DeleteTcb,
                LocalAddress = row.LocalAddress,
                LocalPort = row.LocalPort,
                RemoteAddress = row.RemoteAddress,
                RemotePort = row.RemotePort,
            };
            var result = SetTcpEntry(ref delete);
            if (result == 0)
            {
                closed++;
                _logger.LogInformation("Closed stale TCP connection for {Process} ({Pid}) on {Address}",
                    identity.Name, row.ProcessId, new IPAddress(BitConverter.GetBytes(row.LocalAddress)));
            }
            else
            {
                _logger.LogWarning("Could not close stale TCP connection for {Process} ({Pid}); Win32={Error}",
                    identity.Name, row.ProcessId, result);
            }
        }

        return closed;
    }

    internal static bool ShouldClose(
        string processName,
        string? processPath,
        ConnectionCutMode mode,
        IReadOnlyCollection<string> patterns)
    {
        if (mode == ConnectionCutMode.Disabled)
        {
            return false;
        }

        if (mode == ConnectionCutMode.All)
        {
            return true;
        }

        var matched = patterns.Any(pattern => GlobMatches(processName, processPath, pattern));
        return mode == ConnectionCutMode.AllowList ? matched : !matched;
    }

    internal static bool GlobMatches(string processName, string? processPath, string pattern)
    {
        var normalized = pattern.Trim().Replace('/', '\\');
        if (normalized.Length == 0)
        {
            return false;
        }

        var regex = Regex.Escape(normalized).Replace("\\*", ".*").Replace("\\?", ".");
        if (!normalized.Contains('\\'))
        {
            return Regex.IsMatch(processName, $"^{regex}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var path = processPath?.Replace('/', '\\');
        return path is not null && Regex.IsMatch(path,
            Path.IsPathRooted(normalized) ? $"^{regex}$" : $"^.*(?:\\\\|^){regex}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static (string Name, string? Path) ReadProcessIdentity(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            var name = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : process.ProcessName + ".exe";
            try
            {
                return (name, process.MainModule?.FileName);
            }
            catch
            {
                return (name, null);
            }
        }
        catch
        {
            return ($"pid-{processId}", null);
        }
    }

    private static IReadOnlyList<TcpOwnerPidRow> ReadRows()
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, OwnerPidAll, 0);
        if (size <= sizeof(uint))
        {
            return Array.Empty<TcpOwnerPidRow>();
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, OwnerPidAll, 0);
            if (result != 0)
            {
                return Array.Empty<TcpOwnerPidRow>();
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpOwnerPidRow>();
            var rows = new List<TcpOwnerPidRow>(count);
            var rowPointer = IntPtr.Add(buffer, sizeof(uint));
            for (var index = 0; index < count; index++)
            {
                rows.Add(Marshal.PtrToStructure<TcpOwnerPidRow>(IntPtr.Add(rowPointer, index * rowSize)));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpOwnerPidRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int family,
        int tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref TcpRow row);
}
