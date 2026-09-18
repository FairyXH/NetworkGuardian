using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Tests;

/// <summary>Shared builders so every test states only what it cares about.</summary>
internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static readonly Guid AdapterA = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    public static readonly Guid AdapterB = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    public static GuardianConfig Config(Action<GuardianConfig>? configure = null)
    {
        var config = GuardianConfig.CreateDefault();
        config.CampusAuth.Enabled = false;
        configure?.Invoke(config);
        return config;
    }

    public static ConnectivityProbeReport OnlineProbe(string? source = null) => new()
    {
        TimestampUtc = Now,
        IsOnline = true,
        SuccessCount = 2,
        AttemptCount = 2,
        RequiredSuccessCount = 1,
        SourceAddress = source,
        Attempts = new[]
        {
            new ProbeAttemptResult
            {
                EndpointName = "tcp",
                Kind = ProbeKind.Tcp,
                Target = "223.5.5.5:53",
                Outcome = ProbeOutcome.Success,
            },
        },
    };

    public static ConnectivityProbeReport OfflineProbe(string? source = null, bool captive = false) => new()
    {
        TimestampUtc = Now,
        IsOnline = false,
        CaptivePortalSuspected = captive,
        CaptivePortalInterceptedBy = captive ? "http -> http://portal.example/login" : null,
        SuccessCount = 0,
        AttemptCount = 2,
        RequiredSuccessCount = 1,
        SourceAddress = source,
        Attempts = new[]
        {
            new ProbeAttemptResult
            {
                EndpointName = "http",
                Kind = ProbeKind.Http,
                Target = "http://www.msftconnecttest.com/connecttest.txt",
                Outcome = captive ? ProbeOutcome.CaptivePortalRedirect : ProbeOutcome.Timeout,
                RedirectLocation = captive ? "http://portal.example/login" : null,
                Detail = captive ? "HTTP 302 redirected" : "timed out",
            },
        },
    };

    public static InterfaceRuntimeState EthernetInterface(
        bool up = true,
        bool hasAddress = true,
        bool hasGateway = true,
        ConnectivityProbeReport? probe = null,
        bool? isPhysical = true,
        string id = "luid:1001") => new()
    {
        Id = id,
        Name = "Ethernet",
        Description = "Intel(R) Ethernet Connection",
        Kind = InterfaceKind.Ethernet,
        IsPhysicalDevice = isPhysical,
        IsUp = up,
        IsPresent = true,
        HasUsableIpv4 = hasAddress,
        HasDefaultGateway = hasGateway,
        Ipv4Addresses = hasAddress ? new[] { "10.10.10.20" } : Array.Empty<string>(),
        Ipv4Gateways = hasGateway ? new[] { "10.10.10.1" } : Array.Empty<string>(),
        InterfaceMetric = 10,
        Probe = probe,
        ObservedAtUtc = Now,
    };

    public static InterfaceRuntimeState WifiInterface(
        Guid guid,
        bool up = true,
        bool hasAddress = true,
        ConnectivityProbeReport? probe = null,
        string id = "luid:2001") => new()
    {
        Id = id,
        Name = "Wi-Fi",
        Description = "Intel(R) Wi-Fi 6E AX211",
        Kind = InterfaceKind.Wifi,
        WlanInterfaceGuid = guid,
        IsUp = up,
        IsPresent = true,
        HasUsableIpv4 = hasAddress,
        HasDefaultGateway = hasAddress,
        Ipv4Addresses = hasAddress ? new[] { "10.20.30.40" } : Array.Empty<string>(),
        Ipv4Gateways = hasAddress ? new[] { "10.20.30.1" } : Array.Empty<string>(),
        InterfaceMetric = 35,
        Probe = probe,
        ObservedAtUtc = Now,
    };

    public static WifiAdapterRuntimeState ConnectedAdapter(
        Guid guid,
        string ssid,
        string profile,
        int quality = 50,
        AdapterScanSnapshot? scan = null,
        string? description = "Intel(R) Wi-Fi 6E AX211") => new()
    {
        InterfaceGuid = guid,
        Description = description!,
        IsConnected = true,
        Connection = new WifiConnectionInfo
        {
            InterfaceGuid = guid,
            State = WifiConnectionState.Connected,
            Ssid = ssid,
            ProfileName = profile,
            Bssid = "AA:BB:CC:DD:EE:01",
            SignalQuality = quality,
            Rssi = -60,
            ObservedAtUtc = Now,
        },
        LastScan = scan,
        ProfileListKnown = true,
        SavedProfiles = new[] { profile },
    };

    public static WifiAdapterRuntimeState DisconnectedAdapter(
        Guid guid,
        IReadOnlyList<string>? profiles = null,
        AdapterScanSnapshot? scan = null) => new()
    {
        InterfaceGuid = guid,
        Description = "USB Wi-Fi Adapter",
        IsConnected = false,
        Connection = new WifiConnectionInfo
        {
            InterfaceGuid = guid,
            State = WifiConnectionState.Disconnected,
            ObservedAtUtc = Now,
        },
        LastScan = scan,
        ProfileListKnown = true,
        SavedProfiles = profiles ?? new[] { "CampusWiFi" },
    };

    public static AdapterScanSnapshot Scan(
        Guid guid,
        params ScannedNetwork[] networks) => new()
    {
        InterfaceGuid = guid,
        StartedAtUtc = Now.AddSeconds(-5),
        CompletedAtUtc = Now,
        Completed = true,
        Networks = networks,
    };

    public static ScannedNetwork Network(
        Guid guid,
        string ssid,
        int quality,
        bool hasProfile = true,
        string? profileName = null,
        NetworkBand band = NetworkBand.Band5GHz,
        bool connectable = true,
        WifiBssType bssType = WifiBssType.Infrastructure,
        WifiSecurity security = WifiSecurity.Wpa2Personal) => new()
    {
        InterfaceGuid = guid,
        Ssid = ssid,
        SignalQuality = quality,
        Rssi = (quality / 2) - 100,
        FrequencyKhz = band == NetworkBand.Band5GHz ? 5_180_000 : 2_437_000,
        Channel = band == NetworkBand.Band5GHz ? 36 : 6,
        Band = band,
        Security = security,
        BssType = bssType,
        ProfileName = hasProfile ? profileName ?? ssid : null,
        HasProfile = hasProfile,
        Connectable = connectable,
        ObservedAtUtc = Now,
        BssEntries = Array.Empty<WifiBssEntry>(),
    };

    public static PnpDeviceRecord Pnp(
        string instanceId,
        string? enumerator = "PCI",
        string? service = "netwtw10",
        int? physicalMediaType = 9,
        IReadOnlyList<string>? hardwareIds = null,
        string? friendlyName = "Intel(R) Wi-Fi 6E AX211 160MHz",
        string? netCfgInstanceId = null,
        uint problemCode = 0,
        bool started = true) => new()
    {
        DeviceInstanceId = instanceId,
        EnumeratorName = enumerator,
        Service = service,
        PhysicalMediaType = physicalMediaType,
        HardwareIds = hardwareIds ?? new[] { @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086" },
        CompatibleIds = Array.Empty<string>(),
        FriendlyName = friendlyName,
        DeviceDescription = friendlyName,
        ClassGuid = new Guid("4d36e972-e325-11ce-bfc1-08002be10318"),
        ClassName = "Net",
        NetCfgInstanceId = netCfgInstanceId,
        ProblemCode = problemCode,
        IsStarted = started,
        IsPresent = true,
    };

    public static ManagedDevice Managed(PnpDeviceRecord record, bool physical, Guid? wlanGuid = null)
    {
        var classifier = new Core.Policies.NetworkDeviceClassifier();
        var classification = classifier.Classify(
            record,
            wlanGuid is null ? Array.Empty<Guid>() : new[] { wlanGuid.Value });

        return new ManagedDevice { Record = record, Classification = classification };
    }

    public static WifiRadioSnapshot RadioOn => new()
    {
        State = RadioState.On,
        IsAccessAllowed = true,
        Name = "Wi-Fi",
        ObservedAtUtc = Now,
    };
}
