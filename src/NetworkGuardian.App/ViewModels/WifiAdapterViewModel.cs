using System.Collections.ObjectModel;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.App.ViewModels;

/// <summary>One physical Wi-Fi adapter row, including its own scan results.</summary>
public sealed class WifiAdapterViewModel : ObservableObject
{
    private WifiAdapterRuntimeState _state;

    public WifiAdapterViewModel(WifiAdapterRuntimeState state)
    {
        _state = state;
        Networks = new ObservableCollection<ScannedNetworkViewModel>();
        Profiles = new ObservableCollection<string>();
        Update(state);
    }

    public Guid InterfaceGuid => _state.InterfaceGuid;

    public ObservableCollection<ScannedNetworkViewModel> Networks { get; }

    public ObservableCollection<string> Profiles { get; }

    private string _name = string.Empty;
    private string _statusText = string.Empty;
    private string _ssid = string.Empty;
    private string _bssid = string.Empty;
    private string _signalText = string.Empty;
    private string _bandText = string.Empty;
    private string _ipText = string.Empty;
    private string _guidText = string.Empty;
    private string _deviceInstanceId = string.Empty;
    private string _busType = string.Empty;
    private string _mac = string.Empty;
    private string _lastScanText = string.Empty;
    private string _lastConnectText = string.Empty;
    private string _lastFailure = string.Empty;
    private string _savedProfileText = string.Empty;
    private bool _isConnected;
    private bool _isScanning;

    public string Name { get => _name; private set => Set(ref _name, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string Ssid { get => _ssid; private set => Set(ref _ssid, value); }

    public string Bssid { get => _bssid; private set => Set(ref _bssid, value); }

    public string SignalText { get => _signalText; private set => Set(ref _signalText, value); }

    public string BandText { get => _bandText; private set => Set(ref _bandText, value); }

    public string IpText { get => _ipText; private set => Set(ref _ipText, value); }

    public string GuidText { get => _guidText; private set => Set(ref _guidText, value); }

    public string DeviceInstanceId { get => _deviceInstanceId; private set => Set(ref _deviceInstanceId, value); }

    public string BusType { get => _busType; private set => Set(ref _busType, value); }

    public string MacAddress { get => _mac; private set => Set(ref _mac, value); }

    public string LastScanText { get => _lastScanText; private set => Set(ref _lastScanText, value); }

    public string LastConnectText { get => _lastConnectText; private set => Set(ref _lastConnectText, value); }

    public string LastFailure { get => _lastFailure; private set => Set(ref _lastFailure, value); }

    public string SavedProfileText { get => _savedProfileText; private set => Set(ref _savedProfileText, value); }

    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }

    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }

    // Composed display lines: keeping the text ready-made avoids Run bindings in XAML, which are
    // easy to get wrong with x:Bind.
    public string LineConnection => $"状态：{StatusText}　SSID：{Ssid}　信号：{SignalText}";

    public string LineRadio => $"BSSID：{Bssid}　频段：{BandText}";

    public string LineAddresses => $"IP：{IpText}　MAC：{MacAddress}";

    public string LineInterfaceGuid => $"InterfaceGUID：{GuidText}";

    public string LineDeviceId => $"DeviceInstanceId：{DeviceInstanceId}";

    public string LineScan => $"配置数：{SavedProfileText}　上次扫描：{LastScanText}";

    public string LineAttempts => $"上次连接尝试：{LastConnectText}　最近失败：{LastFailure}";

    public void Update(WifiAdapterRuntimeState state)
    {
        _state = state;

        Name = state.Description;
        IsConnected = state.IsConnected;
        IsScanning = state.IsScanInProgress;
        StatusText = state.IsConnected ? "已连接" : "未连接";
        Ssid = state.CurrentSsid ?? "—";
        Bssid = string.IsNullOrEmpty(state.Connection?.Bssid) ? "—" : state.Connection!.Bssid;
        SignalText = state.IsConnected ? $"{state.SignalQuality}% / {state.Connection?.Rssi ?? 0} dBm" : "—";
        BandText = state.Connection is null || state.Connection.Band == NetworkBand.Unknown
            ? "—"
            : $"{state.Connection.Band} ch{state.Connection.Channel} ({state.Connection.FrequencyKhz / 1000} MHz)";
        GuidText = state.InterfaceGuid.ToString("D");
        DeviceInstanceId = state.DeviceInstanceId ?? "—";
        MacAddress = string.IsNullOrEmpty(state.MacAddress) ? "—" : state.MacAddress!;
        LastScanText = state.LastScan?.CompletedAtUtc is { } completed
            ? $"{completed.ToLocalTime():HH:mm:ss}（{state.LastScan.Networks.Count} 个网络）"
            : "尚未扫描";
        LastConnectText = state.LastConnectAttemptUtc is { } attempt
            ? $"{attempt.ToLocalTime():HH:mm:ss} → {state.LastAttemptedProfile}"
            : "—";
        LastFailure = string.IsNullOrEmpty(state.LastFailure) ? "—" : state.LastFailure!;
        SavedProfileText = state.ProfileListKnown
            ? $"{state.SavedProfiles.Count} 个已保存配置"
            : "未知";

        SyncCollection(Profiles, state.SavedProfiles);

        var networks = state.LastScan?.Networks ?? Array.Empty<ScannedNetwork>();
        SyncNetworks(networks);

        Raise(nameof(LineConnection));
        Raise(nameof(LineRadio));
        Raise(nameof(LineAddresses));
        Raise(nameof(LineInterfaceGuid));
        Raise(nameof(LineDeviceId));
        Raise(nameof(LineScan));
        Raise(nameof(LineAttempts));
    }

    public void SetInterfaceDetails(string? ip, string busType)
    {
        IpText = string.IsNullOrEmpty(ip) ? "—" : ip!;
        BusType = string.IsNullOrEmpty(busType) ? "—" : busType;
        Raise(nameof(LineAddresses));
    }

    private void SyncNetworks(IReadOnlyList<ScannedNetwork> networks)
    {
        Networks.Clear();
        foreach (var network in networks)
        {
            Networks.Add(new ScannedNetworkViewModel(network) { Owner = this });
        }
    }

    private static void SyncCollection(ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}

/// <summary>A single visible network on one adapter.</summary>
public sealed class ScannedNetworkViewModel
{
    public ScannedNetworkViewModel(ScannedNetwork network)
    {
        Ssid = network.Ssid;
        SignalText = $"{network.SignalQuality}%";
        RssiText = $"{network.Rssi} dBm";
        BandText = network.Band == NetworkBand.Unknown
            ? "—"
            : $"{network.Band} ch{network.Channel}";
        SecurityText = network.Security.ToString();
        ProfileText = network.HasProfile ? network.ProfileName ?? network.Ssid : "无配置（忽略）";
        ConnectableText = network.Connectable ? "可连接" : "不可连接";
        IsConnected = network.IsCurrentConnection;
        CanConnect = network.HasProfile && network.Connectable && !network.IsCurrentConnection;
        ProfileName = network.ProfileName ?? network.Ssid;
    }

    public string Ssid { get; }

    public string SignalText { get; }

    public string RssiText { get; }

    public string BandText { get; }

    public string SecurityText { get; }

    public string ProfileText { get; }

    public string ConnectableText { get; }

    public bool IsConnected { get; }

    public bool CanConnect { get; }

    public string ProfileName { get; }

    /// <summary>The adapter this network was observed on; needed by the connect button.</summary>
    public WifiAdapterViewModel? Owner { get; init; }
}
