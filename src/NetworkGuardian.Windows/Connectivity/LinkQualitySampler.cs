using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;

namespace NetworkGuardian.Windows.Connectivity;

/// <summary>Small fixed batches. No early success cancellation and no dependency on global routing.</summary>
public static class LinkQualitySampler
{
    public static async Task<LinkQualitySample> SampleAsync(InterfaceRuntimeState iface,
        IReadOnlyList<string> tcpTargets, CancellationToken token)
    {
        var source = NetworkQualityIdentity.Source(iface) ?? throw new ArgumentException("No usable probe source.", nameof(iface));
        var echo = source.AddressFamily == AddressFamily.InterNetwork
            ? Task.Run(() => EchoBatch(source, iface.PrimaryGateway, token), token)
            : Task.FromResult((GatewaySent: 0, GatewayReplies: 0, InternetSent: 0, InternetReplies: 0, Jitter: 0d, InternetLatency: (double?)null));
        var tcp = await SampleTcpBatchAsync(source,
            source.AddressFamily == AddressFamily.InterNetwork ? iface.InterfaceIndex : iface.Ipv6InterfaceIndex,
            SelectTargets(tcpTargets, source.AddressFamily), token).ConfigureAwait(false);
        var icmp = await echo.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var delays = tcp.Where(t => t.Success).Select(t => t.Ms).ToArray();
        var variations = tcp.Where(t => t.Success).GroupBy(t => t.Target).Where(g => g.Count() > 1)
            .Select(g => Math.Abs(g.Last().Ms - g.First().Ms)).ToArray();
        return new()
        {
            AtUtc = DateTimeOffset.UtcNow,
            GatewaySent = icmp.GatewaySent, GatewayReplies = icmp.GatewayReplies,
            InternetSent = icmp.InternetSent, InternetReplies = icmp.InternetReplies,
            TcpSent = tcp.Length, TcpReplies = tcp.Count(t => t.Success),
            LatencyMs = delays.Length > 0 ? delays.Average() : icmp.InternetLatency,
            JitterMs = icmp.GatewayReplies > 1 ? icmp.Jitter : variations.Length > 0 ? variations.Average() : 0,
        };
    }

    internal static string[] SelectTargets(IReadOnlyList<string> configured, AddressFamily family)
    {
        var targets = configured.Where(t => IPEndPoint.TryParse(t, out var endpoint) && endpoint.AddressFamily == family)
            .Distinct().Take(3).ToArray();
        // Explicit IPv6 targets take precedence; these fallback DNS endpoints keep IPv6-only links measurable.
        return targets.Length > 0 || family != AddressFamily.InterNetworkV6 ? targets
            : new[] { "[2606:4700:4700::1111]:53", "[2606:4700:4700::1001]:53" };
    }

    internal static async Task<(string Target, bool Success, double Ms)[]> SampleTcpBatchAsync(IPAddress source,
        uint interfaceIndex, IReadOnlyList<string> targets, CancellationToken token) =>
        await Task.WhenAll(targets.SelectMany(t => new[] { t, t }).Select(async target =>
        {
            using var socket = new Socket(source.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(source, 0));
            if (interfaceIndex != 0)
                socket.SetSocketOption(source.AddressFamily == AddressFamily.InterNetwork ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
                    (SocketOptionName)31, source.AddressFamily == AddressFamily.InterNetwork
                        ? IPAddress.HostToNetworkOrder((int)interfaceIndex) : (int)interfaceIndex);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(1200);
            var watch = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(IPEndPoint.Parse(target), timeout.Token).ConfigureAwait(false);
                return (Target: target, Success: true, Ms: watch.Elapsed.TotalMilliseconds);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // An RST proves a round trip, just like the raw Npcap TCP reachability check.
                return (Target: target, Success: true, Ms: watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                return (Target: target, Success: false, Ms: 0d);
            }
        })).ConfigureAwait(false);

    private static (int GatewaySent, int GatewayReplies, int InternetSent, int InternetReplies, double Jitter, double? InternetLatency)
        EchoBatch(IPAddress source, string? gateway, CancellationToken token)
    {
        var handle = IcmpCreateFile();
        if (handle == new IntPtr(-1)) return default;
        try
        {
            var gatewaySent = 0;
            var gatewayReplies = 0;
            var internetSent = 0;
            var internetReplies = 0;
            var delays = new List<double>();
            var internetDelays = new List<double>();
            var targets = new[] { gateway, gateway, gateway, gateway, "223.5.5.5", "119.29.29.29" };
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                if (!IPAddress.TryParse(target, out var destination) || destination.AddressFamily != AddressFamily.InterNetwork) continue;
                var reply = new byte[256];
                var count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    BitConverter.ToUInt32(source.GetAddressBytes()), BitConverter.ToUInt32(destination.GetAddressBytes()),
                    new byte[16], 16, IntPtr.Zero, reply, (uint)reply.Length, 600);
                var error = count == 0 ? Marshal.GetLastWin32Error() : (int)BitConverter.ToUInt32(reply, 4);
                if (error is 5 or 50 or 87) continue;
                var success = count > 0 && error == 0;
                if (target == gateway)
                {
                    gatewaySent++;
                    if (success)
                    {
                        gatewayReplies++;
                        delays.Add(BitConverter.ToUInt32(reply, 8));
                    }
                }
                else
                {
                    internetSent++;
                    if (success)
                    {
                        internetReplies++;
                        internetDelays.Add(BitConverter.ToUInt32(reply, 8));
                    }
                }
            }
            var jitter = delays.Count > 1 ? delays.Skip(1).Zip(delays, (a, b) => Math.Abs(a - b)).Average() : 0;
            return (gatewaySent, gatewayReplies, internetSent, internetReplies, jitter,
                internetDelays.Count > 0 ? internetDelays.Average() : null);
        }
        finally { IcmpCloseHandle(handle); }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();
    [DllImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IcmpCloseHandle(IntPtr handle);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(IntPtr handle, IntPtr @event, IntPtr apcRoutine, IntPtr apcContext,
        uint source, uint destination, byte[] requestData, ushort requestSize, IntPtr options,
        [Out] byte[] reply, uint replySize, uint timeout);
}
