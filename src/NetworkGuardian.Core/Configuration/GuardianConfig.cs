using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Configuration;

/// <summary>
/// Root configuration document. Persisted as JSON under
/// <c>%LOCALAPPDATA%\NetworkGuardian\config.json</c>.
/// </summary>
/// <remarks>
/// Every knob is optional: missing members keep the CLR default from <see cref="CreateDefault"/>,
/// so a partially corrupted or hand-edited file still produces a usable configuration.
/// </remarks>
public sealed class GuardianConfig
{
    /// <summary>Schema version. Bumped whenever a migration step is required.</summary>
    public int Version { get; set; } = CurrentVersion;

    public const int CurrentVersion = 12;

    public GeneralSettings General { get; set; } = new();

    public RecoverySettings Recovery { get; set; } = new();

    public ProbeSettings Probe { get; set; } = new();

    public WifiSettings Wifi { get; set; } = new();

    public EthernetSettings Ethernet { get; set; } = new();

    public CampusAuthSettings CampusAuth { get; set; } = new();

    public List<CommandDefinition> OfflineCommands { get; set; } = new();

    public StartupSettings Startup { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();

    public List<ProbeEndpointSettings> ProbeEndpoints { get; set; } = new();

    public List<string> InterfaceDenyList { get; set; } = new();

    public static GuardianConfig CreateDefault() => new()
    {
        Version = CurrentVersion,
        ProbeEndpoints = ProbeEndpointSettings.CreateDefaults(),
        OfflineCommands = new List<CommandDefinition>(),
    };
}

public sealed class GeneralSettings
{
    /// <summary>Master switch for background automatic recovery.</summary>
    public bool AutomaticRecovery { get; set; } = true;

    /// <summary>Prefer keeping Ethernet as the primary path; Wi-Fi stays connected but is not optimised.</summary>
    public bool PreferEthernet { get; set; } = true;

    /// <summary>Allow automatically turning the Windows Wi-Fi radio back on.</summary>
    public bool AutoEnableWifiRadio { get; set; } = true;

    /// <summary>Allow automatically enabling physically present but disabled Wi-Fi adapters.</summary>
    public bool AutoEnableWifiDevices { get; set; } = true;

    /// <summary>
    /// Allow one bounded repair attempt (disable + enable, the same thing Device Manager's
    /// "Disable device / Enable device" does) for a physical Wi-Fi adapter that is present but not
    /// running because its driver failed to start. Windows reports those as a problem code other
    /// than "disabled" (10 = CM_PROB_FAILED_START, 43 = CM_PROB_FAILED_POST_START, ...).
    /// </summary>
    /// <remarks>
    /// This is a real state change on the machine, so it is rate limited per device and stops after a
    /// few consecutive failures; the log states plainly when the restart did not help, because at
    /// that point the fix is a driver reinstall, not another software switch.
    /// </remarks>
    public bool AutoRestartFaultedWifiDevices { get; set; } = true;

    /// <summary>Keep the Wi-Fi radio asserted On during startup and recovery.</summary>
    public bool EnsureRadioOnAtStartup { get; set; } = true;

    /// <summary>
    /// Keep every wireless adapter's software radio switch on. A dedicated watchdog re-reads the
    /// per-adapter switch (Windows Radio Manager) and turns a switched-off radio back on immediately;
    /// one adapter switched off in Windows Settings used to stay dark for the whole run.
    /// </summary>
    public bool RadioWatchdogEnabled { get; set; } = true;

    /// <summary>Poll cadence of the radio watchdog in seconds.</summary>
    public int RadioWatchdogSeconds { get; set; } = 3;

    /// <summary>Poll cadence of the low frequency health sweep.</summary>
    public int HealthSweepSeconds { get; set; } = 20;

    /// <summary>How often the full adapter/device enumeration is refreshed (devices + WLAN interfaces).</summary>
    public int EnumerationRefreshSeconds { get; set; } = 90;

    /// <summary>Delay applied after resume from sleep / fast startup before trusting the network stack.</summary>
    public int ResumeSettleSeconds { get; set; } = 8;

    /// <summary>Minimum seconds between two automatic Wi-Fi scans on the same adapter.</summary>
    public int MinimumScanIntervalSeconds { get; set; } = 25;

    /// <summary>Hard cap on how long a scan may take before it is declared failed.</summary>
    public int ScanTimeoutSeconds { get; set; } = 12;

    /// <summary>Time allowed for a manual scan triggered from the UI.</summary>
    public int ManualScanTimeoutSeconds { get; set; } = 15;

    /// <summary>How long to wait for the OS to complete DHCP after a successful association.</summary>
    public int DhcpWaitSeconds { get; set; } = 20;

    /// <summary>Set <c>InterfaceMetric</c> automatically for wired-first failover.</summary>
    public bool ManageInterfaceMetrics { get; set; } = true;

    /// <summary>Preferred metric given to Ethernet when <see cref="ManageInterfaceMetrics"/> is on.</summary>
    public int PreferredEthernetMetric { get; set; } = 10;

    /// <summary>Preferred metric given to Wi-Fi when <see cref="ManageInterfaceMetrics"/> is on.</summary>
    public int PreferredWifiMetric { get; set; } = 35;
}

public sealed class RecoverySettings
{
    /// <summary>Consecutive failed probes before the Internet is considered down.</summary>
    public int InternetFailureThreshold { get; set; } = 3;

    /// <summary>Consecutive successful probes required to leave recovery mode (hysteresis).</summary>
    public int InternetRecoveryThreshold { get; set; } = 2;

    /// <summary>Minimum stable time before a recovered preferred interface may lead again.</summary>
    public int InterfaceRecoveryHoldSeconds { get; set; } = 6;

    /// <summary>Consecutive failed probes before an adapter's connection is considered dead.</summary>
    public int WifiFailureThreshold { get; set; } = 3;

    /// <summary>Global cooldown between two automatic recovery rounds.</summary>
    public int CooldownSeconds { get; set; } = 45;

    /// <summary>Exponential backoff cap for repeated failures.</summary>
    public int MaxBackoffSeconds { get; set; } = 600;

    /// <summary>Base backoff used for the first retry of a failing operation.</summary>
    public int BaseBackoffSeconds { get; set; } = 5;

    /// <summary>How many times the same adapter may attempt a reconnect before entering a longer cooldown.</summary>
    public int MaxConnectAttemptsPerRound { get; set; } = 3;

    /// <summary>Short-term ban applied to a profile after a connect failure.</summary>
    public int ConnectFailureBlacklistSeconds { get; set; } = 180;

    /// <summary>Seconds to wait after the Wi-Fi device state changes before expecting an interface.</summary>
    public int DeviceEnableSettleSeconds { get; set; } = 6;

    /// <summary>Maximum number of PnP enable operations performed per hour.</summary>
    public int MaxDeviceEnablePerHour { get; set; } = 6;

    /// <summary>Failures of the same operation that trigger a circuit breaker.</summary>
    public int OperationCircuitBreakerThreshold { get; set; } = 5;

    /// <summary>How long a tripped circuit breaker stays open.</summary>
    public int OperationCircuitBreakerSeconds { get; set; } = 900;
}

public sealed class ProbeSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Interval of the periodic Internet check.</summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>Fast cadence used while automatic route failover is active.</summary>
    public int FastRouteIntervalSeconds { get; set; } = 2;

    /// <summary>Retry interval while an interface is offline or behind a captive portal.</summary>
    public int FailureIntervalSeconds { get; set; } = 2;

    /// <summary>Retry interval when only weak/local evidence is available.</summary>
    public int IndeterminateIntervalSeconds { get; set; } = 5;

    /// <summary>Per-attempt timeout. Keep short so recovery stays responsive.</summary>
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>Total budget for one full probe round.</summary>
    public int RoundTimeoutMs { get; set; } = 3500;

    /// <summary>Concurrent attempt limit.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Attempts that must succeed before the Internet is considered reachable.</summary>
    public int RequiredSuccessCount { get; set; } = 1;

    /// <summary>Treat a captive portal response as "no usable Internet".</summary>
    public bool TreatCaptivePortalAsOffline { get; set; } = true;

    /// <summary>Also run an interface-bound probe per up interface (needed for sticky decisions).</summary>
    public bool PerInterfaceProbing { get; set; } = true;

    /// <summary>Enable the ICMP fallback probes in the endpoint list.</summary>
    public bool AllowIcmp { get; set; }

    /// <summary>Timeout for each single ICMP echo request.</summary>
    public int PingTimeoutMs { get; set; } = 1200;

    /// <summary>Domains or IP addresses probed concurrently, one ICMP packet per target.</summary>
    public List<string> PingTargets { get; set; } = new()
    {
        "www.baidu.com",
        "www.bing.com",
        "www.sogou.com",
        "223.5.5.5",
        "119.29.29.29",
    };

    /// <summary>Do not follow HTTP redirects; a 3xx means a portal is intercepting.</summary>
    public bool DetectCaptivePortalRedirects { get; set; } = true;

    /// <summary>Optional explicit User-Agent; some portals answer differently for browsers.</summary>
    public string? UserAgent { get; set; }
}

public sealed class ProbeEndpointSettings
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public ProbeKind Kind { get; set; } = ProbeKind.Tcp;

    /// <summary>
    /// Tcp/Http/Https: <c>host:port</c> or a URI. Dns: a host name to resolve. Icmp: an address.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    public int? TimeoutMs { get; set; }

    public int ExpectedStatusMin { get; set; } = 200;

    public int ExpectedStatusMax { get; set; } = 299;

    /// <summary>When set, a 200 response must contain this marker to count as a real success.</summary>
    public string? BodyMarker { get; set; }

    public static List<ProbeEndpointSettings> CreateDefaults() => new()
    {
        new ProbeEndpointSettings
        {
            Name = "MicrosoftNcsiHttp",
            Kind = ProbeKind.Http,
            Target = "http://www.msftconnecttest.com/connecttest.txt",
            BodyMarker = "Microsoft Connect Test",
        },
        new ProbeEndpointSettings
        {
            Name = "MicrosoftNcsiHttps",
            Kind = ProbeKind.Https,
            Target = "https://www.msftconnecttest.com/connecttest.txt",
            BodyMarker = "Microsoft Connect Test",
        },
        new ProbeEndpointSettings
        {
            Name = "BaiduHttps",
            Kind = ProbeKind.Https,
            Target = "https://www.baidu.com/",
        },
        new ProbeEndpointSettings
        {
            Name = "BingHttps",
            Kind = ProbeKind.Https,
            Target = "https://www.bing.com/",
        },
        new ProbeEndpointSettings
        {
            Name = "QQHttps",
            Kind = ProbeKind.Https,
            Target = "https://www.qq.com/",
        },
        new ProbeEndpointSettings
        {
            Name = "CloudflareHttps",
            Kind = ProbeKind.Https,
            Target = "https://www.cloudflare.com/cdn-cgi/trace",
            Enabled = false,
        },
        new ProbeEndpointSettings
        {
            Name = "GoogleGenerate204",
            Kind = ProbeKind.Http,
            Target = "http://connectivitycheck.gstatic.com/generate_204",
            ExpectedStatusMin = 204,
            ExpectedStatusMax = 204,
        },
        new ProbeEndpointSettings
        {
            Name = "MiuiGenerate204",
            Kind = ProbeKind.Http,
            Target = "http://connect.rom.miui.com/generate_204",
            ExpectedStatusMin = 204,
            ExpectedStatusMax = 204,
        },
        new ProbeEndpointSettings
        {
            Name = "AliDnsTcp",
            Kind = ProbeKind.Tcp,
            Target = "223.5.5.5:53",
            TimeoutMs = 2000,
            Enabled = false,
        },
        new ProbeEndpointSettings
        {
            Name = "DnspodTcp",
            Kind = ProbeKind.Tcp,
            Target = "119.29.29.29:53",
            TimeoutMs = 2000,
            Enabled = false,
        },
        new ProbeEndpointSettings
        {
            Name = "PublicDnsResolve",
            Kind = ProbeKind.Dns,
            Target = "www.baidu.com",
            TimeoutMs = 2000,
            Enabled = false,
        },
        new ProbeEndpointSettings
        {
            Name = "PingAliDns",
            Kind = ProbeKind.Icmp,
            Target = "223.5.5.5",
            TimeoutMs = 1200,
        },
        new ProbeEndpointSettings
        {
            Name = "PingDnspod",
            Kind = ProbeKind.Icmp,
            Target = "119.29.29.29",
            TimeoutMs = 1200,
        },
        new ProbeEndpointSettings
        {
            Name = "PingCloudflare",
            Kind = ProbeKind.Icmp,
            Target = "1.1.1.1",
            TimeoutMs = 1200,
        },
        new ProbeEndpointSettings
        {
            Name = "PingGoogleDns",
            Kind = ProbeKind.Icmp,
            Target = "8.8.8.8",
            TimeoutMs = 1200,
        },
        new ProbeEndpointSettings
        {
            Name = "PingGateway",
            Kind = ProbeKind.Icmp,
            Target = string.Empty,
            Enabled = false,
            TimeoutMs = 1500,
        },
    };
}

public sealed class WifiSettings
{
    /// <summary>Prefer a Wi-Fi path on a different upstream while healthy Ethernet is the outlet.</summary>
    public bool DiversifyFromHealthyEthernet { get; set; } = true;
    /// <summary>Never switch an adapter that currently has a working connection (sticky connection).</summary>
    public bool StickyConnection { get; set; } = true;

    /// <summary>Allow an adapter that is connected but has no usable Internet to be re-evaluated.</summary>
    public bool RecoverStaleConnections { get; set; } = true;

    /// <summary>Minimum continuous offline time before a connected adapter may switch networks.</summary>
    public int StaleConnectionSeconds { get; set; } = 120;

    /// <summary>Prefer 5 GHz / 6 GHz candidates when scores are close.</summary>
    public bool PreferHighBand { get; set; } = true;

    /// <summary>Score bonus applied to 5/6 GHz candidates when <see cref="PreferHighBand"/> is on.</summary>
    public double HighBandBonus { get; set; } = 8.0;

    /// <summary>Signal quality difference (percentage points) that must be exceeded to prefer another network.</summary>
    public int SignalHysteresis { get; set; } = 5;

    /// <summary>Allow more than one adapter to connect to the same SSID.</summary>
    public bool AllowSameSsidOnMultipleAdapters { get; set; }

    /// <summary>Prefer a profile that connected successfully recently when scores are close.</summary>
    public bool PreferRecentProfiles { get; set; } = true;

    /// <summary>Score bonus for the profile used most recently on an adapter.</summary>
    public double RecentProfileBonus { get; set; } = 6.0;

    /// <summary>Ignore networks whose signal quality is below this value unless nothing better exists.</summary>
    public int MinimumSignalQuality { get; set; } = 10;

    /// <summary>Only auto-connect networks that have a saved profile (never guess credentials).</summary>
    public bool OnlySavedProfiles { get; set; } = true;

    /// <summary>Also auto-connect hidden saved profiles when they are detected.</summary>
    public bool AllowHiddenProfiles { get; set; } = true;

    /// <summary>Seconds an adapter must remain disconnected before recovery scanning starts.</summary>
    public int DisconnectGraceSeconds { get; set; } = 6;

    /// <summary>Explicit SSID deny list; matching profiles are never auto-connected.</summary>
    public List<string> SsidDenyList { get; set; } = new();

    /// <summary>Explicit SSID allow list. When non-empty, only these SSIDs are auto-connected.</summary>
    public List<string> SsidAllowList { get; set; } = new();

    /// <summary>
    /// Use the built-in wireless network library for 802.1X/EAP networks: when a candidate requires
    /// EAP authentication the profile is generated from the stored account and pushed to Windows
    /// before connecting. When false such networks are left untouched (Windows prompts as usual).
    /// </summary>
    public bool UseCredentialLibraryForEap { get; set; } = true;

    /// <summary>Rewrites the Windows profile whenever the library entry changed since it was applied.</summary>
    public bool ApplyEapProfileOnConnect { get; set; } = true;

    /// <summary>
    /// Failed EAP authentication attempts allowed per adapter and SSID before that network is given up
    /// for the rest of the run. The counter is not persisted: the next start tries again.
    /// </summary>
    public int EapConnectMaxAttempts { get; set; } = 5;

    /// <summary>Networks that must not be joined during the configured campus authentication outage.</summary>
    public List<string> CampusNetworkSsids { get; set; } = new();

    /// <summary>Optional SSID to WLAN interface binding for campus networks.</summary>
    public Dictionary<string, string> CampusWifiAdapterAssignments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool CampusQuietPeriodEnabled { get; set; }

    /// <summary>Local minutes after midnight. A period whose end is before its start crosses midnight.</summary>
    public int CampusQuietStartMinutes { get; set; }

    public int CampusQuietEndMinutes { get; set; } = 360;
}

public sealed class EthernetSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Consecutive probe failures on a link-up Ethernet adapter before acting.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Run the campus auth program when an Ethernet link is up but the Internet is not.</summary>
    public bool AuthenticateWhenLinkUpButOffline { get; set; } = true;

    /// <summary>Seconds to wait after link-up before expecting usable connectivity.</summary>
    public int LinkUpGraceSeconds { get; set; } = 12;

    /// <summary>Also trigger when the link is up but no IPv4 address was obtained.</summary>
    public bool AuthenticateOnNoIpConfiguration { get; set; } = true;
}

public enum CommandKind
{
    /// <summary>Start the file directly.</summary>
    Executable = 0,

    /// <summary>Run the command line through the Windows shell (<c>cmd.exe /c</c>).</summary>
    Shell = 1,
}

public enum CommandWindowStyle
{
    Normal = 0,
    Hidden,
    Minimized,
}

/// <summary>Shared description of an external program triggered by NetworkGuardian.</summary>
public class CommandDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Command";

    public bool Enabled { get; set; } = true;

    public CommandKind Kind { get; set; } = CommandKind.Executable;

    public string ExecutablePath { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string? WorkingDirectory { get; set; }

    public bool RunAsAdministrator { get; set; }

    public CommandWindowStyle WindowStyle { get; set; } = CommandWindowStyle.Hidden;

    /// <summary>Seconds before the process is considered hung (0 disables the timeout).</summary>
    public int ExecutionTimeoutSeconds { get; set; } = 30;

    /// <summary>Minimum seconds between two runs of this command.</summary>
    public int MinIntervalSeconds { get; set; } = 120;

    public int MaxRunsPerHour { get; set; } = 6;

    /// <summary>Maximum consecutive runs without an intervening Internet success.</summary>
    public int MaxConsecutiveRuns { get; set; } = 4;

    /// <summary>Do not start a new instance while a previous one is still running.</summary>
    public bool SkipIfAlreadyRunning { get; set; } = true;

    /// <summary>Kill the process when <see cref="ExecutionTimeoutSeconds"/> elapses.</summary>
    public bool KillOnTimeout { get; set; } = true;

    /// <summary>Wait for the process to exit before continuing recovery.</summary>
    public bool WaitForExit { get; set; }

    /// <summary>Seconds to wait after a successful run before re-probing connectivity.</summary>
    public int WaitAfterRunSeconds { get; set; } = 8;

    public CommandDefinition Clone() => (CommandDefinition)MemberwiseClone();
}

/// <summary>Campus portal / authenticator client configuration.</summary>
public sealed class CampusAuthSettings
{
    public bool Enabled { get; set; }

    public string Name { get; set; } = "Campus Auth";

    public CommandKind Kind { get; set; } = CommandKind.Executable;

    public string ExecutablePath { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string? WorkingDirectory { get; set; }

    public bool RunAsAdministrator { get; set; }

    public CommandWindowStyle WindowStyle { get; set; } = CommandWindowStyle.Minimized;

    /// <summary>Consecutive Internet failures before the authenticator is started.</summary>
    public int TriggerAfterConsecutiveFailures { get; set; } = 2;

    /// <summary>Seconds to wait after starting the authenticator before probing again.</summary>
    public int WaitAfterRunSeconds { get; set; } = 12;

    /// <summary>Shortest interval allowed between two launches.</summary>
    public int MinIntervalSeconds { get; set; } = 90;

    /// <summary>Hard cap on launches inside one hour.</summary>
    public int MaxRunsPerHour { get; set; } = 8;

    /// <summary>Maximum consecutive launches without a proven Internet success.</summary>
    public int MaxConsecutiveRuns { get; set; } = 5;

    public int ExecutionTimeoutSeconds { get; set; } = 30;

    public bool KillOnTimeout { get; set; } = true;

    /// <summary>Reuse an already running authenticator instead of starting another copy.</summary>
    public bool SkipIfAlreadyRunning { get; set; } = true;

    public bool WaitForExit { get; set; }

    /// <summary>Also run when a captive portal was detected.</summary>
    public bool RunOnCaptivePortal { get; set; } = true;

    /// <summary>Only run when at least one physical Ethernet link is up.</summary>
    public bool RequireEthernetLink { get; set; } = true;

    /// <summary>Extra probe attempts performed right after launching the authenticator.</summary>
    public int VerificationProbes { get; set; } = 2;

    public CommandDefinition ToCommandDefinition() => new()
    {
        Id = "campus-auth",
        Name = Name,
        Enabled = Enabled,
        Kind = Kind,
        ExecutablePath = ExecutablePath,
        Arguments = Arguments,
        WorkingDirectory = WorkingDirectory,
        RunAsAdministrator = RunAsAdministrator,
        WindowStyle = WindowStyle,
        ExecutionTimeoutSeconds = ExecutionTimeoutSeconds,
        MinIntervalSeconds = MinIntervalSeconds,
        MaxRunsPerHour = MaxRunsPerHour,
        MaxConsecutiveRuns = MaxConsecutiveRuns,
        SkipIfAlreadyRunning = SkipIfAlreadyRunning,
        KillOnTimeout = KillOnTimeout,
        WaitForExit = WaitForExit,
        WaitAfterRunSeconds = WaitAfterRunSeconds,
    };
}

public sealed class StartupSettings
{
    public bool RunAtLogon { get; set; }

    public bool StartMinimized { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Close button behaviour: true = hide to tray, false = exit the process.</summary>
    public bool CloseToTray { get; set; } = true;

    public bool StartMinimizedOnResume { get; set; }
}

public sealed class LoggingSettings
{
    public GuardianLogLevel MinimumLevel { get; set; } = GuardianLogLevel.Information;

    public bool WriteToFile { get; set; } = true;

    public int RetentionDays { get; set; } = 7;

    public int MaxFileSizeKb { get; set; } = 4096;

    public int MaxFiles { get; set; } = 20;

    /// <summary>Log every scan result detail, not only the summary line.</summary>
    public bool VerboseNetwork { get; set; }

    /// <summary>Retain the in-memory ring buffer shown by the Logs page.</summary>
    public int UiBufferSize { get; set; } = 2000;
}
