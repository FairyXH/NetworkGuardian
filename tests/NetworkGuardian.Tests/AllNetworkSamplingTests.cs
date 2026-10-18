using System.Net;
using System.Net.Sockets;
using NetworkGuardian.Windows.Connectivity;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class AllNetworkSamplingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpSamplesAreBoundToTheRequestedAddressForBothFamilies(bool ipv6)
    {
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var listener = new TcpListener(address, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var samples = await LinkQualitySampler.SampleTcpBatchAsync(address, 0,
            new[] { listener.LocalEndpoint.ToString()! }, timeout.Token);
        Assert.Equal(2, samples.Length);
        Assert.All(samples, sample => Assert.True(sample.Success));
        using var first = await listener.AcceptTcpClientAsync(timeout.Token);
        using var second = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal(address, ((IPEndPoint)first.Client.RemoteEndPoint!).Address);
        Assert.Equal(address, ((IPEndPoint)second.Client.RemoteEndPoint!).Address);
    }

    [Fact]
    public void Ipv6OnlyLinksDoNotAttemptIpv4Targets()
    {
        var configured = new[] { "223.5.5.5:53", "[2001:db8::1]:53" };
        Assert.Equal(new[] { "[2001:db8::1]:53" }, LinkQualitySampler.SelectTargets(configured, AddressFamily.InterNetworkV6));
        Assert.All(LinkQualitySampler.SelectTargets(new[] { "223.5.5.5:53" }, AddressFamily.InterNetworkV6),
            target => Assert.Equal(AddressFamily.InterNetworkV6, IPEndPoint.Parse(target).AddressFamily));
    }
}
