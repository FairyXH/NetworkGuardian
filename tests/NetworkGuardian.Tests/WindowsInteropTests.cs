using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Connectivity;
using NetworkGuardian.Windows.Devices;
using NetworkGuardian.Windows.Helper;
using NetworkGuardian.Windows.Location;
using NetworkGuardian.Windows.Native;
using NetworkGuardian.Windows.Network;
using NetworkGuardian.Windows.Wlan;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class HelperEntrySecurityTests
{
    [Fact]
    public void ExchangePaths_MustStayInHelperDirectoryAndUseGeneratedNames()
    {
        var validRequest = GuardianPaths.CreateHelperRequestPath();
        var validResponse = GuardianPaths.CreateHelperResponsePath();

        Assert.True(HelperEntry.IsExchangePath(validRequest, "request-"));
        Assert.True(HelperEntry.IsExchangePath(validResponse, "response-"));
        Assert.False(HelperEntry.IsExchangePath(Path.Combine(GuardianPaths.Root, "request-" + Guid.NewGuid().ToString("N") + ".json"), "request-"));
        Assert.False(HelperEntry.IsExchangePath(Path.Combine(GuardianPaths.HelperDirectory, "request-fixed.json"), "request-"));
        Assert.False(HelperEntry.IsExchangePath(validResponse, "request-"));
    }
}

public sealed class NetworkHotPlugInteropTests
{
    [Fact]
    public void NetworkInterfaceNotificationGuidMatchesWindowsSdk()
    {
        Assert.Equal(
            new Guid("cac88484-7515-4c03-82e6-71a87abac361"),
            SetupApiNative.GuidDevInterfaceNet);
        Assert.NotEqual(SetupApiNative.GuidDevClassNet, SetupApiNative.GuidDevInterfaceNet);
    }
}

/// <summary>Pure mapping helpers that back the Wi-Fi tables and the band preference policy.</summary>
public sealed class WifiMappingTests
{
    [Theory]
    [InlineData(2_412_000, 1)]
    [InlineData(2_437_000, 6)]
    [InlineData(2_472_000, 13)]
    [InlineData(2_484_000, 14)]
    [InlineData(5_180_000, 36)]
    [InlineData(5_500_000, 100)]
    [InlineData(5_955_000, 1)]
    public void FrequencyToChannel_MatchesTheStandardPlan(int frequencyKhz, int expectedChannel) =>
        Assert.Equal(expectedChannel, WifiMapping.FrequencyToChannel(frequencyKhz));

    [Theory]
    [InlineData(2_437_000, NetworkBand.Band2_4GHz)]
    [InlineData(5_180_000, NetworkBand.Band5GHz)]
    [InlineData(6_115_000, NetworkBand.Band6GHz)]
    [InlineData(0, NetworkBand.Unknown)]
    public void FrequencyToBand_IsCorrect(int frequencyKhz, NetworkBand expected) =>
        Assert.Equal(expected, WifiMapping.FrequencyToBand(frequencyKhz));

    [Theory]
    [InlineData(false, 1u, 1u, WifiSecurity.Open)]
    [InlineData(true, 2u, 2u, WifiSecurity.Wep)]
    [InlineData(true, 4u, 3u, WifiSecurity.WpaPersonal)]
    [InlineData(true, 3u, 4u, WifiSecurity.WpaEnterprise)]
    [InlineData(true, 7u, 4u, WifiSecurity.Wpa2Personal)]
    [InlineData(true, 6u, 4u, WifiSecurity.Wpa2Enterprise)]
    [InlineData(true, 9u, 8u, WifiSecurity.Wpa3Personal)]
    [InlineData(true, 11u, 4u, WifiSecurity.EnhancedOpen)]
    public void SecurityMapping_CoversTheCommonCiphers(bool enabled, uint auth, uint cipher, WifiSecurity expected) =>
        Assert.Equal(expected, WifiMapping.MapSecurity(enabled, auth, cipher));

    [Fact]
    public void EstimatedRssi_IsPlausible()
    {
        Assert.Equal(-50, WifiMapping.EstimateRssiFromQuality(100));
        Assert.Equal(-100, WifiMapping.EstimateRssiFromQuality(0));
    }
}

public sealed class NativeWifiManagerLayoutTests
{
    [Fact]
    public void AllWlanStructs_MatchTheNativeSizes()
    {
        using var manager = new NetworkGuardian.Windows.Wlan.NativeWifiManager();

        var problems = manager.ValidateStructLayouts();

        Assert.Empty(problems);
    }
}

public sealed class PhysicalDeviceManagerTests
{
    [Fact]
    public async Task UnknownDevice_IsRefusedWithoutTouchingHardware()
    {
        var manager = new PhysicalDeviceManager(new Core.Policies.NetworkDeviceClassifier());

        var result = await manager.EnableAsync(@"BOGUS\DEVICE\0000", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DeviceOperationOutcome.DeviceNotFound, result.Outcome);
        Assert.Null(result.NativeErrorCode);
    }

    [Fact]
    public async Task Enumeration_ReportsEveryNetworkClassNodeWithAVerdict()
    {
        var manager = new PhysicalDeviceManager(new Core.Policies.NetworkDeviceClassifier());

        var devices = await manager.EnumerateAsync(CancellationToken.None);

        // A machine always has at least one network class device (loopback/miniport included).
        Assert.NotEmpty(devices);
        Assert.All(devices, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Record.DeviceInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(d.Classification.Rule));
            Assert.False(string.IsNullOrWhiteSpace(d.Classification.Reason));
        });

        // Virtual adapters must be present in the list but flagged as not physical.
        var virtualOnes = devices.Where(d => d.Classification.Category == DeviceCategory.Virtual).ToList();
        Assert.All(virtualOnes, d => Assert.False(d.Classification.IsPhysical));
    }
}

public sealed class ConnectivityProbeTests
{
    [Fact]
    public async Task OneSuccessfulHttpEndpoint_DeclaresInternetOnline()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[2048];
            _ = await stream.ReadAsync(buffer);
            var response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response);
        });

        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = new[]
            {
                new ProbeEndpointSettings
                {
                    Name = "web",
                    Kind = ProbeKind.Http,
                    Target = $"http://127.0.0.1:{port}/",
                    TimeoutMs = 1500,
                    BodyMarker = "OK",
                },
                new ProbeEndpointSettings
                {
                    Name = "dead",
                    Kind = ProbeKind.Http,
                    Target = "http://127.0.0.1:1/",
                    TimeoutMs = 500,
                },
            },
            Settings = new ProbeSettings
            {
                RequiredSuccessCount = 1,
                RoundTimeoutMs = 4000,
                MaxConcurrency = 2,
                PingTargets = new List<string>(),
            },
        }, CancellationToken.None);
        await server;

        Assert.True(report.IsOnline);
        Assert.Equal(InternetReachability.InternetVerified, report.Reachability);
        Assert.Equal(1, report.SuccessCount);
    }

    [Fact]
    public async Task RelativeRedirectOnSameHost_IsAcceptedAsNormalServiceBehavior()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[2048]);
            var response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: /regional\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
        });

        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = new[]
            {
                new ProbeEndpointSettings
                {
                    Name = "same-host-redirect",
                    Kind = ProbeKind.Http,
                    Target = $"http://127.0.0.1:{port}/",
                    TimeoutMs = 1500,
                },
            },
            Settings = new ProbeSettings
            {
                RequiredSuccessCount = 1,
                RoundTimeoutMs = 3000,
                MaxConcurrency = 1,
                PingTargets = new List<string>(),
            },
        }, CancellationToken.None);
        await server;

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal(ProbeOutcome.Success, attempt.Outcome);
        Assert.False(report.CaptivePortalSuspected);
    }

    [Fact]
    public async Task ConsecutiveBoundProbes_OpenFreshConnections()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var first = await listener.AcceptTcpClientAsync();
            await using var firstStream = first.GetStream();
            _ = await firstStream.ReadAsync(new byte[2048]);
            var response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nOK");
            await firstStream.WriteAsync(response);

            using var second = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));
            await using var secondStream = second.GetStream();
            _ = await secondStream.ReadAsync(new byte[2048]);
            await secondStream.WriteAsync(response);
        });

        using var probe = new ConnectivityProbe();
        var request = new ProbeRequest
        {
            Endpoints = new[]
            {
                new ProbeEndpointSettings
                {
                    Name = "fresh-bound-http",
                    Kind = ProbeKind.Http,
                    Target = $"http://127.0.0.1:{port}/",
                    TimeoutMs = 1500,
                    BodyMarker = "OK",
                },
            },
            Settings = new ProbeSettings
            {
                RequiredSuccessCount = 1,
                RoundTimeoutMs = 3000,
                MaxConcurrency = 1,
                PingTargets = new List<string>(),
            },
            SourceAddress = "127.0.0.1",
            InterfaceIndex = 1,
        };

        var firstReport = await probe.ProbeAsync(request, CancellationToken.None);
        var secondReport = await probe.ProbeAsync(request, CancellationToken.None);
        await server;

        Assert.True(firstReport.IsOnline);
        Assert.True(secondReport.IsOnline);
    }

    [Fact]
    public async Task BoundHttpProbe_WithoutAdapterDnsUsesSystemDnsForAddressDiscovery()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await using var stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[2048]);
            var response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response);
        });

        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = new[]
            {
                new ProbeEndpointSettings
                {
                    Name = "hostname-over-bound-interface",
                    Kind = ProbeKind.Http,
                    Target = $"http://localhost:{port}/",
                    TimeoutMs = 1500,
                    BodyMarker = "OK",
                },
            },
            Settings = new ProbeSettings
            {
                RequiredSuccessCount = 1,
                RoundTimeoutMs = 3000,
                MaxConcurrency = 1,
                PingTargets = new List<string>(),
            },
            SourceAddress = "127.0.0.1",
            InterfaceIndex = 1,
            DnsServerAddresses = Array.Empty<string>(),
        }, CancellationToken.None);
        await server;

        Assert.True(report.IsOnline);
    }

    [Theory]
    [InlineData("https://www.bing.com/", "https://cn.bing.com/")]
    [InlineData("https://www.bing.com/search?q=test", "https://www.bing.com/?cc=us")]
    [InlineData("https://www.bing.com/", "https://de.bing.com/")]
    [InlineData("https://www.bing.com/", "https://bing.com/")]
    public void BingRegionalLocalization_IsAnExpectedInternetRedirect(string source, string target)
    {
        Assert.True(ConnectivityProbe.IsExpectedInternetRedirect(new Uri(source), new Uri(target)));
    }

    [Theory]
    [InlineData("http://www.bing.com/", "http://cn.bing.com/")]
    [InlineData("https://www.bing.com/", "https://login.example.com/")]
    [InlineData("https://www.bing.com/", "https://notbing.com/")]
    [InlineData("https://www.bing.com/", "https://bing.com.example.com/")]
    [InlineData("https://example.com/", "https://cn.bing.com/")]
    public void UnrelatedRedirect_RemainsCaptivePortalEvidence(string source, string target)
    {
        Assert.False(ConnectivityProbe.IsExpectedInternetRedirect(new Uri(source), new Uri(target)));
    }

    [Fact]
    public async Task VerifiedEndpoint_CancelsSlowerAttemptsImmediately()
    {
        using var fastListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        using var slowListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        fastListener.Start();
        slowListener.Start();
        var fastPort = ((System.Net.IPEndPoint)fastListener.LocalEndpoint).Port;
        var slowPort = ((System.Net.IPEndPoint)slowListener.LocalEndpoint).Port;

        var fastServer = Task.Run(async () =>
        {
            using var client = await fastListener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[2048];
            _ = await stream.ReadAsync(buffer);
            var response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response);
        });
        var slowServer = Task.Run(async () =>
        {
            using var client = await slowListener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[2048];
            _ = await stream.ReadAsync(buffer);
            _ = await stream.ReadAsync(buffer);
        });

        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = new[]
            {
                new ProbeEndpointSettings
                {
                    Name = "fast-marker",
                    Kind = ProbeKind.Http,
                    Target = $"http://127.0.0.1:{fastPort}/",
                    TimeoutMs = 3000,
                    BodyMarker = "OK",
                },
                new ProbeEndpointSettings
                {
                    Name = "slow",
                    Kind = ProbeKind.Http,
                    Target = $"http://127.0.0.1:{slowPort}/",
                    TimeoutMs = 3000,
                },
            },
            Settings = new ProbeSettings
            {
                RequiredSuccessCount = 1,
                RoundTimeoutMs = 5000,
                MaxConcurrency = 2,
                PingTargets = new List<string>(),
            },
        }, CancellationToken.None);

        await Task.WhenAll(fastServer, slowServer).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(report.IsOnline);
        Assert.True(report.Duration < TimeSpan.FromSeconds(1.5), $"probe took {report.Duration}");
    }

    [Fact]
    public async Task PortalLikeResponse_DoesNotCancelPendingVerifiedEndpoint()
    {
        using var portalListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        using var verifiedListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        portalListener.Start();
        verifiedListener.Start();
        var portalPort = ((System.Net.IPEndPoint)portalListener.LocalEndpoint).Port;
        var verifiedPort = ((System.Net.IPEndPoint)verifiedListener.LocalEndpoint).Port;

        var portalServer = Task.Run(async () =>
        {
            using var client = await portalListener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[2048]);
            var response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nLOGIN");
            await stream.WriteAsync(response);
        });
        var verifiedServer = Task.Run(async () =>
        {
            using var client = await verifiedListener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[2048]);
            await Task.Delay(150);
            var response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response);
        });

        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = new[]
            {
                new ProbeEndpointSettings
                {
                    Name = "portal-like",
                    Kind = ProbeKind.Http,
                    Target = $"http://127.0.0.1:{portalPort}/",
                    TimeoutMs = 1500,
                    BodyMarker = "OK",
                },
                new ProbeEndpointSettings
                {
                    Name = "verified",
                    Kind = ProbeKind.Http,
                    Target = $"http://127.0.0.1:{verifiedPort}/",
                    TimeoutMs = 1500,
                    BodyMarker = "OK",
                },
            },
            Settings = new ProbeSettings
            {
                RequiredSuccessCount = 1,
                RoundTimeoutMs = 3000,
                MaxConcurrency = 2,
                PingTargets = new List<string>(),
            },
        }, CancellationToken.None);

        await Task.WhenAll(portalServer, verifiedServer);
        Assert.True(report.IsOnline);
        Assert.True(report.CaptivePortalSuspected);
        Assert.Equal(1, report.SuccessCount);
    }

    [Fact]
    public async Task PingSuccessAlone_DoesNotDeclareUsableInternet()
    {
        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = new[]
            {
                new ProbeEndpointSettings
                {
                    Name = "loopback-ping",
                    Kind = ProbeKind.Icmp,
                    Target = "127.0.0.1",
                    TimeoutMs = 1200,
                },
            },
            Settings = new ProbeSettings
            {
                AllowIcmp = true,
                PingTargets = new List<string> { "127.0.0.1" },
                RequiredSuccessCount = 1,
                RoundTimeoutMs = 2500,
                MaxConcurrency = 1,
            },
        }, CancellationToken.None);

        Assert.False(report.IsOnline);
        Assert.Equal(0, report.SuccessCount);
        Assert.Single(report.Attempts);
    }

    [Fact]
    public async Task InterfaceBoundProbe_NeverUsesUnboundManagedPing()
    {
        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = Array.Empty<ProbeEndpointSettings>(),
            Settings = new ProbeSettings
            {
                AllowIcmp = true,
                PingTargets = new List<string> { "127.0.0.1" },
                RoundTimeoutMs = 1500,
            },
            SourceAddress = "127.0.0.1",
            InterfaceIndex = 1,
            InterfaceId = "luid:1",
        }, CancellationToken.None);

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal(ProbeOutcome.NotAttempted, attempt.Outcome);
        Assert.Equal(ProbeEvidence.None, attempt.Evidence);
        Assert.False(report.IsOnline);
    }

    [Fact]
    public async Task TcpSuccessAlone_DoesNotDeclareUsableInternet()
    {
        // A deliberately unreachable endpoint plus a working loopback TCP listener: the verdict must
        // come from the successful probe only.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = new[]
            {
                new ProbeEndpointSettings { Name = "dead", Kind = ProbeKind.Tcp, Target = "127.0.0.1:1", TimeoutMs = 700 },
                new ProbeEndpointSettings { Name = "alive", Kind = ProbeKind.Tcp, Target = $"127.0.0.1:{port}", TimeoutMs = 1500 },
            },
            Settings = new ProbeSettings
            {
                RequiredSuccessCount = 1,
                RoundTimeoutMs = 6000,
                MaxConcurrency = 2,
                PingTargets = new List<string>(),
            },
        }, CancellationToken.None);

        Assert.False(report.IsOnline);
        Assert.Equal(0, report.SuccessCount);
        Assert.Equal(2, report.AttemptCount);
    }

    [Fact]
    public async Task AllEndpointsFailing_ReportsOffline()
    {
        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = new[]
            {
                new ProbeEndpointSettings { Name = "dead", Kind = ProbeKind.Tcp, Target = "127.0.0.1:1", TimeoutMs = 500 },
            },
            Settings = new ProbeSettings
            {
                RequiredSuccessCount = 1,
                RoundTimeoutMs = 3000,
                MaxConcurrency = 1,
                PingTargets = new List<string>(),
            },
        }, CancellationToken.None);

        Assert.False(report.IsOnline);
        Assert.Equal(0, report.SuccessCount);
    }

    [Fact]
    public async Task DisabledProbing_ReturnsAnExplicitNotAttemptedReport()
    {
        using var probe = new ConnectivityProbe();
        var report = await probe.ProbeAsync(new ProbeRequest
        {
            Endpoints = ProbeEndpointSettings.CreateDefaults(),
            Settings = new ProbeSettings { Enabled = false },
        }, CancellationToken.None);

        Assert.False(report.IsOnline);
        Assert.Equal(0, report.AttemptCount);
    }
}

public sealed class NpcapRawPacketTests
{
    [Fact]
    public void RawIcmpFrame_HasValidEthernetIpv4AndIcmpHeaders()
    {
        var localMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var gatewayMac = new byte[] { 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff };
        var frame = NpcapProbeVerifier.NpcapApi.BuildIcmpEcho(
            localMac, gatewayMac,
            System.Net.IPAddress.Parse("192.0.2.10").GetAddressBytes(),
            System.Net.IPAddress.Parse("223.5.5.5").GetAddressBytes(),
            0x1234, 1);

        Assert.Equal(gatewayMac, frame[..6]);
        Assert.Equal(localMac, frame[6..12]);
        Assert.Equal(0x0800, frame[12] << 8 | frame[13]);
        Assert.Equal(0, NpcapProbeVerifier.NpcapApi.Checksum(frame.AsSpan(14, 20)));
        Assert.Equal(0, NpcapProbeVerifier.NpcapApi.Checksum(frame.AsSpan(34, 8)));
    }

    [Fact]
    public void RawTcpSynFrame_HasValidIpv4AndTcpChecksums()
    {
        var localMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var gatewayMac = new byte[] { 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff };
        var localIp = System.Net.IPAddress.Parse("192.0.2.10").GetAddressBytes();
        var targetIp = System.Net.IPAddress.Parse("1.1.1.1").GetAddressBytes();
        var frame = NpcapProbeVerifier.NpcapApi.BuildTcpSyn(
            localMac, gatewayMac, localIp, targetIp, 55000, 443, 0x12345678);

        Assert.Equal(0x0800, frame[12] << 8 | frame[13]);
        Assert.Equal(6, frame[23]);
        Assert.Equal(0x02, frame[47]);
        Assert.Equal(0, NpcapProbeVerifier.NpcapApi.Checksum(frame.AsSpan(14, 20)));
        Assert.Equal(0, NpcapProbeVerifier.NpcapApi.TcpChecksum(localIp, targetIp, frame.AsSpan(34, 20)));
    }

    [Fact]
    public void RawArpFrame_TargetsGatewayWithoutUsingTheIpStack()
    {
        var localMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var localIp = System.Net.IPAddress.Parse("192.0.2.10").GetAddressBytes();
        var gatewayIp = System.Net.IPAddress.Parse("192.0.2.1").GetAddressBytes();
        var frame = NpcapProbeVerifier.NpcapApi.BuildArpRequest(localMac, localIp, gatewayIp);

        Assert.All(frame[..6], value => Assert.Equal(0xff, value));
        Assert.Equal(localMac, frame[6..12]);
        Assert.Equal(0x0806, frame[12] << 8 | frame[13]);
        Assert.Equal(localIp, frame[28..32]);
        Assert.Equal(gatewayIp, frame[38..42]);
    }
}

public sealed class LocationPermissionServiceTests
{
    [Fact]
    public void NativeObservation_IsSurfacedAsABlockingProblem()
    {
        var service = new LocationPermissionService(() => new LocationPermissionSnapshot
        {
            ScanBlockedByPolicy = true,
            BlockedOperation = "WlanGetNetworkBssList",
            Detail = "ERROR_ACCESS_DENIED",
            ObservedAtUtc = DateTimeOffset.UtcNow,
        });

        var snapshot = service.Read();

        Assert.True(snapshot.HasProblem);
        var guidance = LocationPermissionService.BuildGuidance(snapshot);
        Assert.Contains("位置", guidance);
        Assert.Contains("WlanGetNetworkBssList", guidance);

        // Only the statistics APIs were denied, so the guidance must not claim that scanning is dead.
        Assert.Contains("扫描与已保存网络的连接仍然可用", guidance);
    }

    [Fact]
    public void ScanDenied_IsReportedAsABlockingProblem()
    {
        var service = new LocationPermissionService(() => new LocationPermissionSnapshot
        {
            ScanBlockedByPolicy = true,
            BlockedOperation = "WlanScan",
        });

        var guidance = LocationPermissionService.BuildGuidance(service.Read());

        Assert.Contains("WlanScan", guidance);
        Assert.Contains("扫描", guidance);
    }

    [Fact]
    public void ConsentDeniedWithoutADeniedApi_IsExplainedWithoutInventingAnOperation()
    {
        // BuildGuidance is a pure function of the snapshot, so it is called directly: going through
        // Read() would merge this machine's real location consent (HKCU ConsentStore\location) into
        // the assertion and make the test depend on how the developer configured Windows privacy.
        var snapshot = new LocationPermissionSnapshot
        {
            AppLocationAllowed = false,
            ScanBlockedByPolicy = false,
        };

        var guidance = LocationPermissionService.BuildGuidance(snapshot);

        Assert.Contains("consent", guidance);
        Assert.DoesNotContain("未知调用", guidance);
    }

    [Fact]
    public void ReadAlwaysReturnsASnapshot_EvenWithoutRegistryAccess()
    {
        var service = new LocationPermissionService();
        var snapshot = service.Read();

        Assert.NotNull(snapshot);
    }
}

public sealed class NetworkInterfaceProviderTests
{
    [Fact]
    public void InterfacesAndRoutes_AreReadable()
    {
        var provider = new NetworkInterfaceProvider();

        var interfaces = provider.GetInterfaces();
        var routes = provider.GetDefaultRoutes();

        Assert.NotNull(interfaces);
        Assert.NotNull(routes);
        Assert.All(interfaces, i => Assert.False(string.IsNullOrWhiteSpace(i.Id)));
    }

    [Fact]
    public async Task InterfaceMetrics_AreIgnoredWhenDisabled()
    {
        var provider = new NetworkInterfaceProvider();
        var config = GuardianConfig.CreateDefault();
        config.General.ManageInterfaceMetrics = false;

        var notes = await provider.ApplyInterfaceMetricsAsync(config, CancellationToken.None);

        Assert.Contains(notes, n => n.Contains("disabled"));
    }
}
