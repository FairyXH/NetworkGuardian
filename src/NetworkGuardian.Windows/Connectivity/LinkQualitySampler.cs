using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Windows.Connectivity;

/// <summary>Small fixed batches. No early success cancellation and no dependency on global routing.</summary>
public static class LinkQualitySampler
{
    public static async Task<LinkQualitySample> SampleAsync(InterfaceRuntimeState iface,
        IReadOnlyList<string> tcpTargets, CancellationToken token)
    {
        var source = IPAddress.Parse(iface.PrimaryIpv4Address!);
        var echo = Task.Run(() => EchoBatch(source, iface.PrimaryGateway, token), token);
        var targets = tcpTargets.Where(t => IPEndPoint.TryParse(t, out _)).Distinct().Take(3).ToArray();
        var tcp = await Task.WhenAll(targets.Select(async target =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(source, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31,
                IPAddress.HostToNetworkOrder((int)iface.InterfaceIndex));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(1200);
            var watch = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(IPEndPoint.Parse(target), timeout.Token).ConfigureAwait(false);
                return (Success: true, Ms: watch.Elapsed.TotalMilliseconds);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // An RST proves a round trip, just like the raw Npcap TCP reachability check.
                return (Success: true, Ms: watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                return (Success: false, Ms: 0d);
            }
        })).ConfigureAwait(false);
        var icmp = await echo.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var delays = tcp.Where(t => t.Success).Select(t => t.Ms).ToArray();
        return new()
        {
            AtUtc = DateTimeOffset.UtcNow,
            GatewaySent = icmp.GatewaySent, GatewayReplies = icmp.GatewayReplies,
            InternetSent = icmp.InternetSent, InternetReplies = icmp.InternetReplies,
            TcpSent = tcp.Length, TcpReplies = tcp.Count(t => t.Success),
            LatencyMs = delays.Length > 0 ? delays.Average() : icmp.InternetLatency,
            JitterMs = icmp.Jitter,
        };
    }

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
