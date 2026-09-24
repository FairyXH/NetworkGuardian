namespace NetworkGuardian.Core.Models;

/// <summary>Broad category of a network interface as seen by the OS routing table.</summary>
public enum InterfaceKind
{
    Unknown = 0,
    Ethernet,
    Wifi,
    Loopback,
    Tunnel,
    Virtual,
    Other,
}

/// <summary>Classification result for a PnP network device.</summary>
public enum DeviceCategory
{
    Unknown = 0,

    /// <summary>Real, wired Ethernet NIC on a physical bus.</summary>
    PhysicalEthernet,

    /// <summary>Real 802.11 radio on a physical bus.</summary>
    PhysicalWifi,

    /// <summary>Software / virtual adapter (Wi-Fi Direct, Hyper-V, VPN, TAP, Wintun, ...).</summary>
    Virtual,

    /// <summary>Bluetooth PAN, WWAN, WAN miniport and other non-LAN networking devices.</summary>
    Other,
}

public enum NetworkBand
{
    Unknown = 0,
    Band2_4GHz,
    Band5GHz,
    Band6GHz,
    Band60GHz,
}

public enum WifiSecurity
{
    Unknown = 0,
    Open,
    Wep,
    WpaPersonal,
    Wpa2Personal,
    Wpa3Personal,
    WpaEnterprise,
    Wpa2Enterprise,
    Wpa3Enterprise,
    EnhancedOpen,
}

public enum WifiBssType
{
    Unknown = 0,
    Infrastructure,
    Independent,
    Any,
}

public enum WifiConnectionState
{
    Unknown = 0,
    NotReady,
    Disconnected,
    Associating,
    Authenticating,
    Connecting,
    Connected,
    Disconnecting,
    AdHocFormed,
}

public enum RadioState
{
    Unknown = 0,
    On,
    Off,
    Disabled,
}

/// <summary>Why a probe attempt did or did not succeed.</summary>
public enum ProbeOutcome
{
    Success = 0,
    Timeout,
    ConnectionRefused,
    HostUnreachable,
    DnsFailure,
    UnexpectedHttpStatus,
    CaptivePortalRedirect,
    AccessDenied,
    NotAttempted,
    UnknownFailure,
}

/// <summary>Strength and meaning of a single connectivity observation.</summary>
public enum ProbeEvidence
{
    None = 0,
    LocalNetwork,
    InternetTransport,
    InternetVerified,
    CaptivePortal,
}

/// <summary>Layered Internet reachability verdict used for scheduling and UI.</summary>
public enum InternetReachability
{
    Unknown = 0,
    LocalOnly,
    CaptivePortal,
    InternetLikely,
    InternetVerified,
}

public enum ProbeKind
{
    Tcp = 0,
    Http,
    Https,
    Dns,
    Icmp,
}

/// <summary>High level connectivity classification used by the UI and the state machine.</summary>
public enum ConnectivityLevel
{
    Unknown = 0,

    /// <summary>No physical link on any managed interface.</summary>
    NoLink,

    /// <summary>Link is up but no usable IPv4 address is configured.</summary>
    NoIpConfiguration,

    /// <summary>An address exists but there is no default gateway / route.</summary>
    NoDefaultRoute,

    /// <summary>Routing works but probes suggest a captive portal / campus walled garden.</summary>
    CaptivePortal,

    /// <summary>Queries reach the local router/host but the Internet is unreachable.</summary>
    NoInternet,

    /// <summary>The adapter reports a healthy link but every probe failed.</summary>
    ProbeFailed,

    Online,
}

/// <summary>Overall recovery state machine states.</summary>
public enum RecoveryState
{
    Initializing = 0,
    Healthy,
    Degraded,
    EthernetNoInternet,
    Authenticating,
    WaitingForAuthentication,
    WifiRadioOff,
    EnablingWifiRadio,
    EnablingWifiDevices,
    WifiScanning,
    WifiConnecting,
    WaitingForDhcp,
    VerifyingInternet,
    Recovering,
    Cooldown,
    Paused,
    Error,
}

/// <summary>Result of a privileged device (PnP) operation.</summary>
public enum DeviceOperationOutcome
{
    Succeeded = 0,
    AlreadyInDesiredState,
    DeviceNotFound,
    NotPhysicalDevice,
    AccessDenied,
    NotSupported,
    RebootRequired,
    Failed,
}

public enum GuardianLogLevel
{
    Trace = 0,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}
