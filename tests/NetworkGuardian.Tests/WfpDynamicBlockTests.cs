using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetworkGuardian.Windows.Devices;
using NetworkGuardian.Windows.Network;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class WfpDynamicBlockTests
{
    [Fact]
    public void NativeStructuresMatchTheX64WindowsSdk()
    {
        Assert.Equal(72, Marshal.SizeOf<WfpDynamicBlock.Session>());
        Assert.Equal(72, Marshal.SizeOf<WfpDynamicBlock.Sublayer>());
        Assert.Equal(40, Marshal.SizeOf<WfpDynamicBlock.Condition>());
        Assert.Equal(200, Marshal.SizeOf<WfpDynamicBlock.Filter>());
        Assert.Equal(152, Marshal.OffsetOf<WfpDynamicBlock.Filter>(nameof(WfpDynamicBlock.Filter.ContextLow)).ToInt32());
    }

    [ElevatedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicFiltersBlockOnlyTheDedicatedUdpPortAndDisappearOnDispose(bool ipv6)
    {
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var receiver = new UdpClient(new IPEndPoint(address, 0));
        using var control = new UdpClient(new IPEndPoint(address, 0));
        using var sender = new UdpClient(address.AddressFamily);
        var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
        await Exchange(sender, receiver, endpoint);
        ulong[] ids;
        using (var block = new WfpDynamicBlock(new[] { address }, (ushort)endpoint.Port))
        {
            ids = block.FilterIds.ToArray();
            Assert.Equal(8, ids.Length);
            Assert.All(ids, id => Assert.True(WfpDynamicBlock.FilterExists(id)));
            // A second socket on the same address remains usable.
            await Exchange(sender, control, (IPEndPoint)control.Client.LocalEndPoint!);
            try { await sender.SendAsync(new byte[] { 42 }, endpoint); }
            catch (SocketException) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await receiver.ReceiveAsync(timeout.Token));
        }
        Assert.All(ids, id => Assert.False(WfpDynamicBlock.FilterExists(id)));
        await Exchange(sender, receiver, endpoint);
    }

    [ElevatedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpAcceptIsBlockedAndRecoversWithoutRestartingAnyAdapter(bool ipv6)
    {
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var listener = new TcpListener(address, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var established = new TcpClient(address.AddressFamily);
        await established.ConnectAsync(address, endpoint.Port);
        using var originalServer = await listener.AcceptTcpClientAsync();
        using (var block = new WfpDynamicBlock(new[] { address }, (ushort)endpoint.Port))
        {
            try { await established.Client.SendAsync(new byte[] { 42 }); }
            catch (SocketException) { }
            using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            var received = 0;
            var readError = await Record.ExceptionAsync(async () =>
                received = await originalServer.Client.ReceiveAsync(new byte[1], SocketFlags.None, receiveTimeout.Token));
            Assert.Equal(0, received);
            Assert.True(readError is null or SocketException or OperationCanceledException, readError?.ToString());
            using var rejected = new TcpClient(address.AddressFamily);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var error = await Record.ExceptionAsync(async () => await rejected.ConnectAsync(address, endpoint.Port, timeout.Token));
            Assert.True(error is SocketException or OperationCanceledException, error?.ToString() ?? "Unexpected TCP success");
        }
        using var client = new TcpClient(address.AddressFamily);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(address, endpoint.Port, connectTimeout.Token);
        using var accepted = await listener.AcceptTcpClientAsync(connectTimeout.Token);
        Assert.True(accepted.Connected);
    }

    [ElevatedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialInstallationFailureRollsBackFiltersAlreadyAdded(bool ipv6)
    {
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var receiver = new UdpClient(new IPEndPoint(address, 0));
        using var sender = new UdpClient(address.AddressFamily);
        var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
        Assert.Throws<ArgumentException>(() => new WfpDynamicBlock(new[] { address, IPAddress.Any }, (ushort)endpoint.Port));
        await Exchange(sender, receiver, endpoint);
    }

    private static async Task Exchange(UdpClient sender, UdpClient receiver, IPEndPoint target)
    {
        await sender.SendAsync(new byte[] { 42 }, target);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 42 }, (await receiver.ReceiveAsync(timeout.Token)).Buffer);
    }

    private sealed class ElevatedTheoryAttribute : TheoryAttribute
    {
        public ElevatedTheoryAttribute()
        {
            if (!HelperClient.IsProcessElevated()) Skip = "WFP tests require an elevated test process.";
        }
    }
}
