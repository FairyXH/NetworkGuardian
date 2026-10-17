using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Helper;
using NetworkGuardian.Windows.Devices;
using System.ComponentModel;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;

namespace NetworkGuardian.Windows.Network;

/// <summary>Closes IPv4 TCP connections that are still bound to a superseded outlet address.</summary>
public sealed class TcpConnectionMigrator
{
    private const int AfInet = 2;
    private const int OwnerPidAll = 5;
    private const uint DeleteTcb = 12;
    private readonly ILogger<TcpConnectionMigrator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _retryElevationAfter;

    public TcpConnectionMigrator(ILogger<TcpConnectionMigrator>? logger = null)
    {
        _logger = logger ?? NullLogger<TcpConnectionMigrator>.Instance;
    }

    public async Task<int> CloseAsync(
        IReadOnlyCollection<string> oldLocalAddresses,
        ConnectionMigrationSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Mode == ConnectionCutMode.Disabled || oldLocalAddresses.Count == 0) return 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!AreSupersededPhysicalAddresses(oldLocalAddresses))
            {
                _logger.LogDebug("Skipping TCP cleanup: the old interface/address is no longer superseded");
                return 0;
            }
            if (HelperClient.IsProcessElevated())
                return (await Task.Run(() => Close(oldLocalAddresses, settings, cancellationToken), cancellationToken)
                    .ConfigureAwait(false)).Closed;
            if (DateTimeOffset.UtcNow < _retryElevationAfter) return 0;
            var addressSet = oldLocalAddresses
                .Where(value => IsEligibleOldAddress(value, out _))
                .Select(value => BitConverter.ToUInt32(IPAddress.Parse(value).GetAddressBytes())).ToHashSet();
            if (!ReadRows().Any(row => addressSet.Contains(row.LocalAddress) && IsLiveOwnedConnection(row.State, row.ProcessId) &&
                ShouldClose(ReadProcessIdentity(row.ProcessId).Name, ReadProcessIdentity(row.ProcessId).Path,
                    settings.Mode, settings.ProcessPatterns))) return 0;
            var response = await new HelperClient().SendAsync(new HelperRequest
            {
                Operation = HelperOperations.CloseStaleTcp,
                OldLocalAddresses = oldLocalAddresses.ToArray(),
                ConnectionMigration = settings,
                RequestedBy = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                // A rejected UAC prompt must not be repeated on every outlet fluctuation.
                _retryElevationAfter = DateTimeOffset.UtcNow.AddMinutes(5);
                _logger.LogWarning("Elevated TCP cleanup failed: {Detail}; retry deferred for five minutes",
                    response.Detail ?? response.Message);
            }
            return response.ClosedConnections;
        }
        finally { _gate.Release(); }
    }

    internal (int Closed, int Failed) Close(
        IReadOnlyCollection<string> oldLocalAddresses,
        ConnectionMigrationSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Mode == ConnectionCutMode.Disabled || oldLocalAddresses.Count == 0)
        {
            return (0, 0);
        }

        var oldAddresses = oldLocalAddresses
            .Select(value => IsEligibleOldAddress(value, out var address) ? address : null)
            .Where(address => address is not null)
            .Select(address => BitConverter.ToUInt32(address!.GetAddressBytes()))
            .ToHashSet();
        if (oldAddresses.Count == 0)
        {
            return (0, 0);
        }

        var rows = ReadRows();
        var closed = 0;
        var failed = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!oldAddresses.Contains(row.LocalAddress) || !IsLiveOwnedConnection(row.State, row.ProcessId))
            {
                continue;
            }

            var identity = ReadProcessIdentity(row.ProcessId);
            if (!ShouldClose(identity.Name, identity.Path, settings.Mode, settings.ProcessPatterns))
            {
                continue;
            }

            var result = DeleteConnection(row);
            if (result == 0)
            {
                closed++;
                _logger.LogInformation("Closed stale TCP connection for {Process} ({Pid}) on {Address}",
                    identity.Name, row.ProcessId, new IPAddress(BitConverter.GetBytes(row.LocalAddress)));
            }
            else
            {
                // The connection may have disappeared between enumeration and deletion.
                if (!ReadRows().Any(current => IsSameConnection(row, current)))
                {
                    _logger.LogDebug("TCP connection disappeared before cleanup ({Pid}, result={Error})", row.ProcessId, result);
                    continue;
                }
                failed++;
                _logger.LogWarning("Could not close stale TCP connection for {Process} ({Pid}); Win32={Error}",
                    identity.Name, row.ProcessId, result);
                if (result is 5 or 317) break;
            }
        }

        return (closed, failed);
    }

    internal static bool IsLiveOwnedConnection(uint state, uint processId) =>
        processId != 0 && state is >= 3 and <= 10;

    internal static bool AreSupersededPhysicalAddresses(IReadOnlyCollection<string> addresses)
    {
        var devices = new PnpDeviceInventory().Enumerate().Where(d => Guid.TryParse(d.NetCfgInstanceId, out _))
            .GroupBy(d => Guid.Parse(d.NetCfgInstanceId!)).ToDictionary(g => g.Key, g => g.First());
        var provider = new NetworkInterfaceProvider(guid => devices.GetValueOrDefault(guid));
        return AreSupersededPhysicalAddresses(addresses, provider.GetInterfaces(),
            DefaultRouteSelector.Order(provider.GetDefaultRoutes()).FirstOrDefault());
    }

    internal static bool AreSupersededPhysicalAddresses(IReadOnlyCollection<string> addresses,
        IReadOnlyList<InterfaceRuntimeState> interfaces, DefaultRouteInfo? outlet)
    {
        if (outlet is null || addresses.Count is 0 or > 16) return false;
        var eligible = interfaces.Where(i => i.IsPhysicalDevice == true &&
            i.Kind is InterfaceKind.Ethernet or InterfaceKind.Wifi &&
            !DefaultRouteSelector.MatchesInterface(outlet, i))
            .SelectMany(i => i.Ipv4Addresses).ToHashSet(StringComparer.Ordinal);
        return addresses.All(address => IsEligibleOldAddress(address, out _) && eligible.Contains(address));
    }

    internal static uint DeleteConnection(TcpOwnerPidRow row)
    {
        var delete = new TcpRow { State = DeleteTcb, LocalAddress = row.LocalAddress,
            LocalPort = row.LocalPort, RemoteAddress = row.RemoteAddress, RemotePort = row.RemotePort };
        return SetTcpEntry(ref delete);
    }

    private static bool IsSameConnection(TcpOwnerPidRow first, TcpOwnerPidRow second) =>
        first.ProcessId == second.ProcessId && IsLiveOwnedConnection(second.State, second.ProcessId) &&
        first.LocalAddress == second.LocalAddress && first.LocalPort == second.LocalPort &&
        first.RemoteAddress == second.RemoteAddress && first.RemotePort == second.RemotePort;

    internal static bool IsEligibleOldAddress(string value, out IPAddress? address)
    {
        if (!IPAddress.TryParse(value, out address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
        {
            address = null;
            return false;
        }

        return true;
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

    internal static IReadOnlyList<TcpOwnerPidRow> ReadRows()
    {
        var size = 0;
        var initial = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, OwnerPidAll, 0);
        if (initial is not 0 and not 122) throw new Win32Exception((int)initial);
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
                throw new Win32Exception((int)result, "Could not read TCP connection table.");
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
    internal struct TcpOwnerPidRow
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
