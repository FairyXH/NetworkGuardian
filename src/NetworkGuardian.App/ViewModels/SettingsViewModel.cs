using System.Collections.ObjectModel;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Startup;

namespace NetworkGuardian.App.ViewModels;

/// <summary>
/// Settings page view model. It edits a working copy of <see cref="GuardianConfig"/> and only
/// commits it when the user presses Save, so a half-typed value can never break the running host.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly StartupRegistration _startup = new();
    private GuardianConfig _working = GuardianConfig.CreateDefault();

    public SettingsViewModel()
    {
        Commands = new ObservableCollection<CommandDefinitionViewModel>();
        Endpoints = new ObservableCollection<ProbeEndpointViewModel>();
        WifiSecurityOptions = new ObservableCollection<string>(
            new[] { "自动（跟随配置文件）" });
    }

    public ObservableCollection<CommandDefinitionViewModel> Commands { get; }

    public ObservableCollection<ProbeEndpointViewModel> Endpoints { get; }

    public ObservableCollection<string> WifiSecurityOptions { get; }

    public ObservableCollection<string> LogLevels { get; } = new(
        new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical" });

    public string ConfigPath { get; private set; } = GuardianPaths.ConfigFile;

    private string _status = "已加载当前配置";

    public string Status { get => _status; private set => Set(ref _status, value); }

    public void Load(GuardianConfig config, string configPath)
    {
        _working = DeepClone(config);
        ConfigPath = configPath;
        Populate();
        Status = $"已加载 {configPath}";
    }

    private static GuardianConfig DeepClone(GuardianConfig config)
    {
        var json = ConfigJson.Serialize(config);
        return ConfigJson.Deserialize(json) ?? GuardianConfig.CreateDefault();
    }

    private void Populate()
    {
        Commands.Clear();
        foreach (var command in _working.OfflineCommands)
        {
            Commands.Add(new CommandDefinitionViewModel(command));
        }

        Endpoints.Clear();
        foreach (var endpoint in _working.ProbeEndpoints)
        {
            Endpoints.Add(new ProbeEndpointViewModel(endpoint));
        }

        // General
        AutomaticRecovery = _working.General.AutomaticRecovery;
        PreferEthernet = _working.General.PreferEthernet;
        AutoEnableWifiRadio = _working.General.AutoEnableWifiRadio;
        AutoEnableWifiDevices = _working.General.AutoEnableWifiDevices;
        EnsureRadioOnAtStartup = _working.General.EnsureRadioOnAtStartup;
        HealthSweepSeconds = _working.General.HealthSweepSeconds;
        EnumerationRefreshSeconds = _working.General.EnumerationRefreshSeconds;
        ResumeSettleSeconds = _working.General.ResumeSettleSeconds;
        MinimumScanIntervalSeconds = _working.General.MinimumScanIntervalSeconds;
        ScanTimeoutSeconds = _working.General.ScanTimeoutSeconds;
        DhcpWaitSeconds = _working.General.DhcpWaitSeconds;
        ManageInterfaceMetrics = _working.General.ManageInterfaceMetrics;
        PreferredEthernetMetric = _working.General.PreferredEthernetMetric;
        PreferredWifiMetric = _working.General.PreferredWifiMetric;

        // Probe
        ProbeEnabled = _working.Probe.Enabled;
        ProbeIntervalSeconds = _working.Probe.IntervalSeconds;
        ProbeTimeoutMs = _working.Probe.TimeoutMs;
        ProbeRoundTimeoutMs = _working.Probe.RoundTimeoutMs;
        ProbeRequiredSuccessCount = _working.Probe.RequiredSuccessCount;
        ProbePerInterface = _working.Probe.PerInterfaceProbing;
        ProbeAllowIcmp = _working.Probe.AllowIcmp;
        ProbeTreatCaptivePortalAsOffline = _working.Probe.TreatCaptivePortalAsOffline;

        // Wi-Fi
        StickyConnection = _working.Wifi.StickyConnection;
        RecoverStaleConnections = _working.Wifi.RecoverStaleConnections;
        PreferHighBand = _working.Wifi.PreferHighBand;
        SignalHysteresis = _working.Wifi.SignalHysteresis;
        AllowSameSsidOnMultipleAdapters = _working.Wifi.AllowSameSsidOnMultipleAdapters;
        OnlySavedProfiles = _working.Wifi.OnlySavedProfiles;
        MinimumSignalQuality = _working.Wifi.MinimumSignalQuality;
        DisconnectGraceSeconds = _working.Wifi.DisconnectGraceSeconds;
        SsidDenyList = string.Join(Environment.NewLine, _working.Wifi.SsidDenyList);
        SsidAllowList = string.Join(Environment.NewLine, _working.Wifi.SsidAllowList);

        // Recovery
        InternetFailureThreshold = _working.Recovery.InternetFailureThreshold;
        WifiFailureThreshold = _working.Recovery.WifiFailureThreshold;
        InternetRecoveryThreshold = _working.Recovery.InternetRecoveryThreshold;
        CooldownSeconds = _working.Recovery.CooldownSeconds;
        BaseBackoffSeconds = _working.Recovery.BaseBackoffSeconds;
        MaxBackoffSeconds = _working.Recovery.MaxBackoffSeconds;
        MaxConnectAttemptsPerRound = _working.Recovery.MaxConnectAttemptsPerRound;
        ConnectFailureBlacklistSeconds = _working.Recovery.ConnectFailureBlacklistSeconds;
        DeviceEnableSettleSeconds = _working.Recovery.DeviceEnableSettleSeconds;
        MaxDeviceEnablePerHour = _working.Recovery.MaxDeviceEnablePerHour;

        // Ethernet
        EthernetEnabled = _working.Ethernet.Enabled;
        EthernetFailureThreshold = _working.Ethernet.FailureThreshold;
        EthernetAuthenticateWhenOffline = _working.Ethernet.AuthenticateWhenLinkUpButOffline;
        EthernetLinkUpGraceSeconds = _working.Ethernet.LinkUpGraceSeconds;

        // Campus auth
        CampusAuthEnabled = _working.CampusAuth.Enabled;
        CampusAuthName = _working.CampusAuth.Name;
        CampusAuthKindIndex = (int)_working.CampusAuth.Kind;
        CampusAuthPath = _working.CampusAuth.ExecutablePath;
        CampusAuthArguments = _working.CampusAuth.Arguments;
        CampusAuthWorkingDirectory = _working.CampusAuth.WorkingDirectory ?? string.Empty;
        CampusAuthRunAsAdmin = _working.CampusAuth.RunAsAdministrator;
        CampusAuthTriggerFailures = _working.CampusAuth.TriggerAfterConsecutiveFailures;
        CampusAuthWaitAfterRun = _working.CampusAuth.WaitAfterRunSeconds;
        CampusAuthMinInterval = _working.CampusAuth.MinIntervalSeconds;
        CampusAuthMaxPerHour = _working.CampusAuth.MaxRunsPerHour;
        CampusAuthMaxConsecutive = _working.CampusAuth.MaxConsecutiveRuns;
        CampusAuthTimeout = _working.CampusAuth.ExecutionTimeoutSeconds;
        CampusAuthKillOnTimeout = _working.CampusAuth.KillOnTimeout;
        CampusAuthSkipIfRunning = _working.CampusAuth.SkipIfAlreadyRunning;
        CampusAuthWaitForExit = _working.CampusAuth.WaitForExit;
        CampusAuthRunOnCaptivePortal = _working.CampusAuth.RunOnCaptivePortal;
        CampusAuthRequireEthernet = _working.CampusAuth.RequireEthernetLink;

        // Startup
        RunAtLogon = _startup.IsEnabled();
        StartMinimized = _working.Startup.StartMinimized;
        MinimizeToTray = _working.Startup.MinimizeToTray;
        CloseToTray = _working.Startup.CloseToTray;

        // Logging
        LogLevelIndex = LogLevels.IndexOf(_working.Logging.MinimumLevel.ToString()) is var index && index >= 0
            ? index
            : 2;
        LogWriteToFile = _working.Logging.WriteToFile;
        LogRetentionDays = _working.Logging.RetentionDays;
        LogMaxFileSizeKb = _working.Logging.MaxFileSizeKb;
        LogMaxFiles = _working.Logging.MaxFiles;
        LogUiBufferSize = _working.Logging.UiBufferSize;

        InterfaceDenyList = string.Join(Environment.NewLine, _working.InterfaceDenyList);
    }

    /// <summary>Writes the UI values back into the working copy.</summary>
    public GuardianConfig Build()
    {
        _working.General.AutomaticRecovery = AutomaticRecovery;
        _working.General.PreferEthernet = PreferEthernet;
        _working.General.AutoEnableWifiRadio = AutoEnableWifiRadio;
        _working.General.AutoEnableWifiDevices = AutoEnableWifiDevices;
        _working.General.EnsureRadioOnAtStartup = EnsureRadioOnAtStartup;
        _working.General.HealthSweepSeconds = (int)HealthSweepSeconds;
        _working.General.EnumerationRefreshSeconds = (int)EnumerationRefreshSeconds;
        _working.General.ResumeSettleSeconds = (int)ResumeSettleSeconds;
        _working.General.MinimumScanIntervalSeconds = (int)MinimumScanIntervalSeconds;
        _working.General.ScanTimeoutSeconds = (int)ScanTimeoutSeconds;
        _working.General.DhcpWaitSeconds = (int)DhcpWaitSeconds;
        _working.General.ManageInterfaceMetrics = ManageInterfaceMetrics;
        _working.General.PreferredEthernetMetric = (int)PreferredEthernetMetric;
        _working.General.PreferredWifiMetric = (int)PreferredWifiMetric;

        _working.Probe.Enabled = ProbeEnabled;
        _working.Probe.IntervalSeconds = (int)ProbeIntervalSeconds;
        _working.Probe.TimeoutMs = (int)ProbeTimeoutMs;
        _working.Probe.RoundTimeoutMs = (int)ProbeRoundTimeoutMs;
        _working.Probe.RequiredSuccessCount = (int)ProbeRequiredSuccessCount;
        _working.Probe.PerInterfaceProbing = ProbePerInterface;
        _working.Probe.AllowIcmp = ProbeAllowIcmp;
        _working.Probe.TreatCaptivePortalAsOffline = ProbeTreatCaptivePortalAsOffline;

        _working.Wifi.StickyConnection = StickyConnection;
        _working.Wifi.RecoverStaleConnections = RecoverStaleConnections;
        _working.Wifi.PreferHighBand = PreferHighBand;
        _working.Wifi.SignalHysteresis = (int)SignalHysteresis;
        _working.Wifi.AllowSameSsidOnMultipleAdapters = AllowSameSsidOnMultipleAdapters;
        _working.Wifi.OnlySavedProfiles = OnlySavedProfiles;
        _working.Wifi.MinimumSignalQuality = (int)MinimumSignalQuality;
        _working.Wifi.DisconnectGraceSeconds = (int)DisconnectGraceSeconds;
        _working.Wifi.SsidDenyList = SplitLines(SsidDenyList);
        _working.Wifi.SsidAllowList = SplitLines(SsidAllowList);

        _working.Recovery.InternetFailureThreshold = (int)InternetFailureThreshold;
        _working.Recovery.WifiFailureThreshold = (int)WifiFailureThreshold;
        _working.Recovery.InternetRecoveryThreshold = (int)InternetRecoveryThreshold;
        _working.Recovery.CooldownSeconds = (int)CooldownSeconds;
        _working.Recovery.BaseBackoffSeconds = (int)BaseBackoffSeconds;
        _working.Recovery.MaxBackoffSeconds = (int)MaxBackoffSeconds;
        _working.Recovery.MaxConnectAttemptsPerRound = (int)MaxConnectAttemptsPerRound;
        _working.Recovery.ConnectFailureBlacklistSeconds = (int)ConnectFailureBlacklistSeconds;
        _working.Recovery.DeviceEnableSettleSeconds = (int)DeviceEnableSettleSeconds;
        _working.Recovery.MaxDeviceEnablePerHour = (int)MaxDeviceEnablePerHour;

        _working.Ethernet.Enabled = EthernetEnabled;
        _working.Ethernet.FailureThreshold = (int)EthernetFailureThreshold;
        _working.Ethernet.AuthenticateWhenLinkUpButOffline = EthernetAuthenticateWhenOffline;
        _working.Ethernet.LinkUpGraceSeconds = (int)EthernetLinkUpGraceSeconds;

        _working.CampusAuth.Enabled = CampusAuthEnabled;
        _working.CampusAuth.Name = CampusAuthName;
        _working.CampusAuth.Kind = (CommandKind)CampusAuthKindIndex;
        _working.CampusAuth.ExecutablePath = CampusAuthPath;
        _working.CampusAuth.Arguments = CampusAuthArguments;
        _working.CampusAuth.WorkingDirectory = string.IsNullOrWhiteSpace(CampusAuthWorkingDirectory)
            ? null
            : CampusAuthWorkingDirectory;
        _working.CampusAuth.RunAsAdministrator = CampusAuthRunAsAdmin;
        _working.CampusAuth.TriggerAfterConsecutiveFailures = (int)CampusAuthTriggerFailures;
        _working.CampusAuth.WaitAfterRunSeconds = (int)CampusAuthWaitAfterRun;
        _working.CampusAuth.MinIntervalSeconds = (int)CampusAuthMinInterval;
        _working.CampusAuth.MaxRunsPerHour = (int)CampusAuthMaxPerHour;
        _working.CampusAuth.MaxConsecutiveRuns = (int)CampusAuthMaxConsecutive;
        _working.CampusAuth.ExecutionTimeoutSeconds = (int)CampusAuthTimeout;
        _working.CampusAuth.KillOnTimeout = CampusAuthKillOnTimeout;
        _working.CampusAuth.SkipIfAlreadyRunning = CampusAuthSkipIfRunning;
        _working.CampusAuth.WaitForExit = CampusAuthWaitForExit;
        _working.CampusAuth.RunOnCaptivePortal = CampusAuthRunOnCaptivePortal;
        _working.CampusAuth.RequireEthernetLink = CampusAuthRequireEthernet;

        _working.Startup.StartMinimized = StartMinimized;
        _working.Startup.MinimizeToTray = MinimizeToTray;
        _working.Startup.CloseToTray = CloseToTray;

        _working.Logging.MinimumLevel = Enum.TryParse<GuardianLogLevel>(
            LogLevels.ElementAtOrDefault(LogLevelIndex) ?? "Information", ignoreCase: true, out var level)
            ? level
            : GuardianLogLevel.Information;
        _working.Logging.WriteToFile = LogWriteToFile;
        _working.Logging.RetentionDays = (int)LogRetentionDays;
        _working.Logging.MaxFileSizeKb = (int)LogMaxFileSizeKb;
        _working.Logging.MaxFiles = (int)LogMaxFiles;
        _working.Logging.UiBufferSize = (int)LogUiBufferSize;

        _working.InterfaceDenyList = SplitLines(InterfaceDenyList);

        _working.OfflineCommands = Commands.Select(c => c.Model).ToList();
        _working.ProbeEndpoints = Endpoints.Select(e => new ProbeEndpointSettings
        {
            Name = e.Name,
            Enabled = e.Enabled,
            Kind = (ProbeKind)e.KindIndex,
            Target = e.Target,
            TimeoutMs = (int)e.TimeoutMs,
            BodyMarker = string.IsNullOrWhiteSpace(e.BodyMarker) ? null : e.BodyMarker,
        }).ToList();

        return _working;
    }

    public void ApplyStartupRegistration()
    {
        var ok = _startup.SetEnabled(RunAtLogon, "--minimized");
        Status = ok ? "开机启动设置已写入注册表" : "开机启动设置写入失败（可能被策略阻止）";
    }

    public void AddCommand()
    {
        var command = new CommandDefinition
        {
            Name = $"断网命令 {Commands.Count + 1}",
            Kind = CommandKind.Shell,
            ExecutablePath = string.Empty,
            Enabled = true,
        };

        Commands.Add(new CommandDefinitionViewModel(command));
        Status = "已添加一条断网命令，记得填写内容并保存";
    }

    public void RemoveCommand(CommandDefinitionViewModel command)
    {
        Commands.Remove(command);
        Status = "已移除命令，保存后生效";
    }

    private static List<string> SplitLines(string value) =>
        value.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- General ----
    private bool _automaticRecovery = true;
    private bool _preferEthernet = true;
    private bool _autoEnableWifiRadio = true;
    private bool _autoEnableWifiDevices = true;
    private bool _ensureRadioOnAtStartup = true;
    private double _healthSweepSeconds = 20;
    private double _enumerationRefreshSeconds = 90;
    private double _resumeSettleSeconds = 8;
    private double _minimumScanIntervalSeconds = 25;
    private double _scanTimeoutSeconds = 12;
    private double _dhcpWaitSeconds = 20;
    private bool _manageInterfaceMetrics;
    private double _preferredEthernetMetric = 10;
    private double _preferredWifiMetric = 35;

    public bool AutomaticRecovery { get => _automaticRecovery; set => Set(ref _automaticRecovery, value); }

    public bool PreferEthernet { get => _preferEthernet; set => Set(ref _preferEthernet, value); }

    public bool AutoEnableWifiRadio { get => _autoEnableWifiRadio; set => Set(ref _autoEnableWifiRadio, value); }

    public bool AutoEnableWifiDevices { get => _autoEnableWifiDevices; set => Set(ref _autoEnableWifiDevices, value); }

    public bool EnsureRadioOnAtStartup { get => _ensureRadioOnAtStartup; set => Set(ref _ensureRadioOnAtStartup, value); }

    public double HealthSweepSeconds { get => _healthSweepSeconds; set => Set(ref _healthSweepSeconds, value); }

    public double EnumerationRefreshSeconds { get => _enumerationRefreshSeconds; set => Set(ref _enumerationRefreshSeconds, value); }

    public double ResumeSettleSeconds { get => _resumeSettleSeconds; set => Set(ref _resumeSettleSeconds, value); }

    public double MinimumScanIntervalSeconds { get => _minimumScanIntervalSeconds; set => Set(ref _minimumScanIntervalSeconds, value); }

    public double ScanTimeoutSeconds { get => _scanTimeoutSeconds; set => Set(ref _scanTimeoutSeconds, value); }

    public double DhcpWaitSeconds { get => _dhcpWaitSeconds; set => Set(ref _dhcpWaitSeconds, value); }

    public bool ManageInterfaceMetrics { get => _manageInterfaceMetrics; set => Set(ref _manageInterfaceMetrics, value); }

    public double PreferredEthernetMetric { get => _preferredEthernetMetric; set => Set(ref _preferredEthernetMetric, value); }

    public double PreferredWifiMetric { get => _preferredWifiMetric; set => Set(ref _preferredWifiMetric, value); }

    // ---- Probe ----
    private bool _probeEnabled = true;
    private double _probeIntervalSeconds = 15;
    private double _probeTimeoutMs = 2500;
    private double _probeRoundTimeoutMs = 8000;
    private double _probeRequiredSuccessCount = 1;
    private bool _probePerInterface = true;
    private bool _probeAllowIcmp;
    private bool _probeTreatCaptivePortalAsOffline = true;

    public bool ProbeEnabled { get => _probeEnabled; set => Set(ref _probeEnabled, value); }

    public double ProbeIntervalSeconds { get => _probeIntervalSeconds; set => Set(ref _probeIntervalSeconds, value); }

    public double ProbeTimeoutMs { get => _probeTimeoutMs; set => Set(ref _probeTimeoutMs, value); }

    public double ProbeRoundTimeoutMs { get => _probeRoundTimeoutMs; set => Set(ref _probeRoundTimeoutMs, value); }

    public double ProbeRequiredSuccessCount { get => _probeRequiredSuccessCount; set => Set(ref _probeRequiredSuccessCount, value); }

    public bool ProbePerInterface { get => _probePerInterface; set => Set(ref _probePerInterface, value); }

    public bool ProbeAllowIcmp { get => _probeAllowIcmp; set => Set(ref _probeAllowIcmp, value); }

    public bool ProbeTreatCaptivePortalAsOffline
    {
        get => _probeTreatCaptivePortalAsOffline;
        set => Set(ref _probeTreatCaptivePortalAsOffline, value);
    }

    // ---- Wi-Fi ----
    private bool _stickyConnection = true;
    private bool _recoverStaleConnections = true;
    private bool _preferHighBand = true;
    private double _signalHysteresis = 5;
    private bool _allowSameSsid = true;
    private bool _onlySavedProfiles = true;
    private double _minimumSignalQuality = 10;
    private double _disconnectGraceSeconds = 6;
    private string _ssidDenyList = string.Empty;
    private string _ssidAllowList = string.Empty;

    public bool StickyConnection { get => _stickyConnection; set => Set(ref _stickyConnection, value); }

    public bool RecoverStaleConnections { get => _recoverStaleConnections; set => Set(ref _recoverStaleConnections, value); }

    public bool PreferHighBand { get => _preferHighBand; set => Set(ref _preferHighBand, value); }

    public double SignalHysteresis { get => _signalHysteresis; set => Set(ref _signalHysteresis, value); }

    public bool AllowSameSsidOnMultipleAdapters { get => _allowSameSsid; set => Set(ref _allowSameSsid, value); }

    public bool OnlySavedProfiles { get => _onlySavedProfiles; set => Set(ref _onlySavedProfiles, value); }

    public double MinimumSignalQuality { get => _minimumSignalQuality; set => Set(ref _minimumSignalQuality, value); }

    public double DisconnectGraceSeconds { get => _disconnectGraceSeconds; set => Set(ref _disconnectGraceSeconds, value); }

    public string SsidDenyList { get => _ssidDenyList; set => Set(ref _ssidDenyList, value); }

    public string SsidAllowList { get => _ssidAllowList; set => Set(ref _ssidAllowList, value); }

    // ---- Recovery ----
    private double _internetFailureThreshold = 3;
    private double _wifiFailureThreshold = 3;
    private double _internetRecoveryThreshold = 2;
    private double _cooldownSeconds = 45;
    private double _baseBackoffSeconds = 5;
    private double _maxBackoffSeconds = 600;
    private double _maxConnectAttemptsPerRound = 3;
    private double _connectFailureBlacklistSeconds = 180;
    private double _deviceEnableSettleSeconds = 6;
    private double _maxDeviceEnablePerHour = 6;

    public double InternetFailureThreshold { get => _internetFailureThreshold; set => Set(ref _internetFailureThreshold, value); }

    public double WifiFailureThreshold { get => _wifiFailureThreshold; set => Set(ref _wifiFailureThreshold, value); }

    public double InternetRecoveryThreshold { get => _internetRecoveryThreshold; set => Set(ref _internetRecoveryThreshold, value); }

    public double CooldownSeconds { get => _cooldownSeconds; set => Set(ref _cooldownSeconds, value); }

    public double BaseBackoffSeconds { get => _baseBackoffSeconds; set => Set(ref _baseBackoffSeconds, value); }

    public double MaxBackoffSeconds { get => _maxBackoffSeconds; set => Set(ref _maxBackoffSeconds, value); }

    public double MaxConnectAttemptsPerRound { get => _maxConnectAttemptsPerRound; set => Set(ref _maxConnectAttemptsPerRound, value); }

    public double ConnectFailureBlacklistSeconds { get => _connectFailureBlacklistSeconds; set => Set(ref _connectFailureBlacklistSeconds, value); }

    public double DeviceEnableSettleSeconds { get => _deviceEnableSettleSeconds; set => Set(ref _deviceEnableSettleSeconds, value); }

    public double MaxDeviceEnablePerHour { get => _maxDeviceEnablePerHour; set => Set(ref _maxDeviceEnablePerHour, value); }

    // ---- Ethernet ----
    private bool _ethernetEnabled = true;
    private double _ethernetFailureThreshold = 3;
    private bool _ethernetAuthenticateWhenOffline = true;
    private double _ethernetLinkUpGraceSeconds = 12;

    public bool EthernetEnabled { get => _ethernetEnabled; set => Set(ref _ethernetEnabled, value); }

    public double EthernetFailureThreshold { get => _ethernetFailureThreshold; set => Set(ref _ethernetFailureThreshold, value); }

    public bool EthernetAuthenticateWhenOffline { get => _ethernetAuthenticateWhenOffline; set => Set(ref _ethernetAuthenticateWhenOffline, value); }

    public double EthernetLinkUpGraceSeconds { get => _ethernetLinkUpGraceSeconds; set => Set(ref _ethernetLinkUpGraceSeconds, value); }

    // ---- Campus auth ----
    private bool _campusAuthEnabled;
    private string _campusAuthName = "校园网认证";
    private int _campusAuthKindIndex;
    private string _campusAuthPath = string.Empty;
    private string _campusAuthArguments = string.Empty;
    private string _campusAuthWorkingDirectory = string.Empty;
    private bool _campusAuthRunAsAdmin;
    private double _campusAuthTriggerFailures = 2;
    private double _campusAuthWaitAfterRun = 12;
    private double _campusAuthMinInterval = 90;
    private double _campusAuthMaxPerHour = 8;
    private double _campusAuthMaxConsecutive = 5;
    private double _campusAuthTimeout = 30;
    private bool _campusAuthKillOnTimeout = true;
    private bool _campusAuthSkipIfRunning = true;
    private bool _campusAuthWaitForExit;
    private bool _campusAuthRunOnCaptivePortal = true;
    private bool _campusAuthRequireEthernet = true;

    public bool CampusAuthEnabled { get => _campusAuthEnabled; set => Set(ref _campusAuthEnabled, value); }

    public string CampusAuthName { get => _campusAuthName; set => Set(ref _campusAuthName, value); }

    public int CampusAuthKindIndex { get => _campusAuthKindIndex; set => Set(ref _campusAuthKindIndex, value); }

    public string CampusAuthPath { get => _campusAuthPath; set => Set(ref _campusAuthPath, value); }

    public string CampusAuthArguments { get => _campusAuthArguments; set => Set(ref _campusAuthArguments, value); }

    public string CampusAuthWorkingDirectory { get => _campusAuthWorkingDirectory; set => Set(ref _campusAuthWorkingDirectory, value); }

    public bool CampusAuthRunAsAdmin { get => _campusAuthRunAsAdmin; set => Set(ref _campusAuthRunAsAdmin, value); }

    public double CampusAuthTriggerFailures { get => _campusAuthTriggerFailures; set => Set(ref _campusAuthTriggerFailures, value); }

    public double CampusAuthWaitAfterRun { get => _campusAuthWaitAfterRun; set => Set(ref _campusAuthWaitAfterRun, value); }

    public double CampusAuthMinInterval { get => _campusAuthMinInterval; set => Set(ref _campusAuthMinInterval, value); }

    public double CampusAuthMaxPerHour { get => _campusAuthMaxPerHour; set => Set(ref _campusAuthMaxPerHour, value); }

    public double CampusAuthMaxConsecutive { get => _campusAuthMaxConsecutive; set => Set(ref _campusAuthMaxConsecutive, value); }

    public double CampusAuthTimeout { get => _campusAuthTimeout; set => Set(ref _campusAuthTimeout, value); }

    public bool CampusAuthKillOnTimeout { get => _campusAuthKillOnTimeout; set => Set(ref _campusAuthKillOnTimeout, value); }

    public bool CampusAuthSkipIfRunning { get => _campusAuthSkipIfRunning; set => Set(ref _campusAuthSkipIfRunning, value); }

    public bool CampusAuthWaitForExit { get => _campusAuthWaitForExit; set => Set(ref _campusAuthWaitForExit, value); }

    public bool CampusAuthRunOnCaptivePortal { get => _campusAuthRunOnCaptivePortal; set => Set(ref _campusAuthRunOnCaptivePortal, value); }

    public bool CampusAuthRequireEthernet { get => _campusAuthRequireEthernet; set => Set(ref _campusAuthRequireEthernet, value); }

    // ---- Startup ----
    private bool _runAtLogon;
    private bool _startMinimized = true;
    private bool _minimizeToTray = true;
    private bool _closeToTray = true;

    public bool RunAtLogon { get => _runAtLogon; set => Set(ref _runAtLogon, value); }

    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

    public bool MinimizeToTray { get => _minimizeToTray; set => Set(ref _minimizeToTray, value); }

    public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }

    // ---- Logging ----
    private int _logLevelIndex = 2;
    private bool _logWriteToFile = true;
    private double _logRetentionDays = 7;
    private double _logMaxFileSizeKb = 4096;
    private double _logMaxFiles = 20;
    private double _logUiBufferSize = 2000;

    public int LogLevelIndex { get => _logLevelIndex; set => Set(ref _logLevelIndex, value); }

    public bool LogWriteToFile { get => _logWriteToFile; set => Set(ref _logWriteToFile, value); }

    public double LogRetentionDays { get => _logRetentionDays; set => Set(ref _logRetentionDays, value); }

    public double LogMaxFileSizeKb { get => _logMaxFileSizeKb; set => Set(ref _logMaxFileSizeKb, value); }

    public double LogMaxFiles { get => _logMaxFiles; set => Set(ref _logMaxFiles, value); }

    public double LogUiBufferSize { get => _logUiBufferSize; set => Set(ref _logUiBufferSize, value); }

    // ---- Device filtering ----
    private string _interfaceDenyList = string.Empty;

    public string InterfaceDenyList { get => _interfaceDenyList; set => Set(ref _interfaceDenyList, value); }

    public void ReportStatus(string status) => Status = status;
}
