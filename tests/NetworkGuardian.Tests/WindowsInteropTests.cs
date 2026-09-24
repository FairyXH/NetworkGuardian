using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Connectivity;
using NetworkGuardian.Windows.Devices;
using NetworkGuardian.Windows.Location;
using NetworkGuardian.Windows.Network;
using NetworkGuardian.Windows.Wlan;
using Xunit;

namespace NetworkGuardian.Tests;

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
