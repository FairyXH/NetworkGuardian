using NetworkGuardian.Windows.Connectivity;
using NetworkGuardian.Windows.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class NpcapProbeVerifierTests
{
    private static readonly byte[] Source = [192, 0, 2, 10];
    private static readonly byte[] Remote = [198, 51, 100, 20];

    [Fact]
    public async Task RawIcmpProbe_ReachesPublicTargetsOnARealAdapter_WhenOptedIn()
    {
        if (Environment.GetEnvironmentVariable("NETWORKGUARDIAN_NPCAP_HARDWARE_TESTS") != "1")
        {
            return;
        }

        var candidate = new NetworkInterfaceProvider().GetInterfaces().FirstOrDefault(item =>
            item.IsUp && item.HasUsableIpv4 && item.AdapterGuid is not null &&
            item.PrimaryGateway is not null && item.MacAddress is not null);
        Assert.NotNull(candidate);

        using var verifier = new NpcapProbeVerifier(NullLogger<NpcapProbeVerifier>.Instance);
        Assert.True(verifier.Status.IsAvailable, verifier.Status.Detail);
        var result = await verifier.ProbeRawIcmpAsync(
            candidate.AdapterGuid,
            candidate.PrimaryIpv4Address,
            candidate.MacAddress,
            candidate.PrimaryGateway,
            new[] { "223.5.5.5", "119.29.29.29" },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Success, result.Detail);
    }

    [Fact]
    public void ClassifyIpv4DirectionRecognizesOutboundFrame()
    {
        var frame = CreateIpv4Frame(Source, Remote);

        var direction = NpcapCaptureSession.ClassifyIpv4Direction(frame, Source);

        Assert.Equal(CapturedPacketDirection.Outbound, direction);
    }

    [Fact]
    public void ClassifyIpv4DirectionRecognizesInboundFrameWithIpOptions()
    {
        var frame = CreateIpv4Frame(Remote, Source, ipHeaderLength: 24);

        var direction = NpcapCaptureSession.ClassifyIpv4Direction(frame, Source);

        Assert.Equal(CapturedPacketDirection.Inbound, direction);
    }

    [Fact]
    public void ClassifyIpv4DirectionRecognizesVlanTaggedFrames()
    {
        var frame = CreateIpv4Frame(Source, Remote, vlanTagged: true);

        var direction = NpcapCaptureSession.ClassifyIpv4Direction(frame, Source);

        Assert.Equal(CapturedPacketDirection.Outbound, direction);
    }

    [Fact]
    public void ClassifyIpv4DirectionRejectsNonIpv4AndTruncatedFrames()
    {
        var ipv6 = CreateIpv4Frame(Source, Remote);
        ipv6[12] = 0x86;
        ipv6[13] = 0xdd;

        Assert.Equal(CapturedPacketDirection.None,
            NpcapCaptureSession.ClassifyIpv4Direction(ipv6, Source));
        Assert.Equal(CapturedPacketDirection.None,
            NpcapCaptureSession.ClassifyIpv4Direction(ipv6.AsSpan(0, 20), Source));
    }

    [Theory]
    [InlineData(6, 52000, 443, true)]
    [InlineData(17, 52000, 53, true)]
    [InlineData(17, 52000, 50000, false)]
    [InlineData(6, 52000, 27015, false)]
    public void IsProbeTransportSeparatesProbeAndApplicationTraffic(
        byte protocol,
        int sourcePort,
        int destinationPort,
        bool expected)
    {
        var frame = CreateIpv4Frame(Source, Remote, protocol: protocol,
            sourcePort: sourcePort, destinationPort: destinationPort);

        Assert.Equal(expected, NpcapCaptureSession.IsProbeTransport(frame));
    }

    private static byte[] CreateIpv4Frame(
        byte[] source,
        byte[] destination,
        int ipHeaderLength = 20,
        bool vlanTagged = false,
        byte protocol = 6,
        int sourcePort = 52000,
        int destinationPort = 443)
    {
        var ipOffset = vlanTagged ? 18 : 14;
        var frame = new byte[ipOffset + ipHeaderLength + 20];
        if (vlanTagged)
        {
            frame[12] = 0x81;
            frame[13] = 0x00;
            frame[16] = 0x08;
            frame[17] = 0x00;
        }
        else
        {
            frame[12] = 0x08;
            frame[13] = 0x00;
        }

        frame[ipOffset] = (byte)(0x40 | ipHeaderLength / 4);
        frame[ipOffset + 9] = protocol;
        source.CopyTo(frame, ipOffset + 12);
        destination.CopyTo(frame, ipOffset + 16);
        var transportOffset = ipOffset + ipHeaderLength;
        frame[transportOffset] = (byte)(sourcePort >> 8);
        frame[transportOffset + 1] = (byte)sourcePort;
        frame[transportOffset + 2] = (byte)(destinationPort >> 8);
        frame[transportOffset + 3] = (byte)destinationPort;
        return frame;
    }
}
