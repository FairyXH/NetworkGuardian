using NetworkGuardian.Windows.Connectivity;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class NpcapProbeVerifierTests
{
    private static readonly byte[] Source = [192, 0, 2, 10];
    private static readonly byte[] Remote = [198, 51, 100, 20];

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

    private static byte[] CreateIpv4Frame(
        byte[] source,
        byte[] destination,
        int ipHeaderLength = 20,
        bool vlanTagged = false)
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
        source.CopyTo(frame, ipOffset + 12);
        destination.CopyTo(frame, ipOffset + 16);
        return frame;
    }
}
