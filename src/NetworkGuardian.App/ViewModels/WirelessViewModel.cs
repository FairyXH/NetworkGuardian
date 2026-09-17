using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using NetworkGuardian.App.Services;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.App.ViewModels;

/// <summary>Backing view model for the Wireless Adapters page.</summary>
public sealed class WirelessViewModel : ObservableObject
{
    private readonly GuardianHostService _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<Guid, WifiAdapterViewModel> _byGuid = new();

    private string _status = "就绪";
    private bool _isBusy;

    public WirelessViewModel(GuardianHostService host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<WifiAdapterViewModel> Adapters { get; } = new();

    public ObservableCollection<string> SavedProfiles { get; } = new();

    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    /// <summary>Physical Wi-Fi devices, including ones that are present but disabled.</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    public void Update(GuardianSnapshot snapshot)
    {
        var seen = new HashSet<Guid>();

        foreach (var adapter in snapshot.WifiAdapters)
        {
            seen.Add(adapter.InterfaceGuid);
            var iface = snapshot.Interfaces.FirstOrDefault(i => i.WlanInterfaceGuid == adapter.InterfaceGuid);

            if (!_byGuid.TryGetValue(adapter.InterfaceGuid, out var vm))
            {
                vm = new WifiAdapterViewModel(adapter);
                _byGuid[adapter.InterfaceGuid] = vm;
                Adapters.Add(vm);
            }
            else
            {
                vm.Update(adapter);
            }

            vm.SetInterfaceDetails(iface?.PrimaryIpv4Address, iface?.MetricDescription ?? "—");
        }

        foreach (var guid in _byGuid.Keys.Where(g => !seen.Contains(g)).ToList())
        {
            Adapters.Remove(_byGuid[guid]);
            _byGuid.Remove(guid);
        }

        SyncDevices(snapshot.WifiDevices);
    }

    private void SyncDevices(IReadOnlyList<ManagedDevice> devices)
    {
        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(new DeviceRowViewModel(device));
        }
    }

    public async Task ScanAsync(WifiAdapterViewModel adapter, bool rescanAll = false)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = rescanAll ? "正在重新扫描所有无线网卡…" : $"正在扫描 {adapter.Name}…";

        try
        {
            if (rescanAll)
            {
                await _host.RescanAllAsync(CancellationToken.None);
            }
            else
            {
                var result = await _host.ScanAdapterAsync(adapter.InterfaceGuid, CancellationToken.None);
                Status = result.Failed
                    ? $"扫描失败：{result.FailureReason}"
                    : $"扫描完成：{result.Networks.Count} 个网络，用时 {result.Duration.TotalSeconds:F1}s";
            }

            if (rescanAll)
            {
                Status = "全部无线网卡扫描完成";
            }
        }
        catch (Exception ex)
        {
            Status = $"扫描异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ConnectAsync(WifiAdapterViewModel adapter, string profileName)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = $"正在连接 {profileName}…";

        try
        {
            var result = await _host.ConnectAsync(adapter.InterfaceGuid, profileName, CancellationToken.None);
            Status = result.Success
                ? $"已连接 {profileName}"
                : $"连接失败：{result.Failure}";
        }
        catch (Exception ex)
        {
            Status = $"连接异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync(WifiAdapterViewModel adapter)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = $"正在断开 {adapter.Name}…";

        try
        {
            var result = await _host.DisconnectAsync(adapter.InterfaceGuid, CancellationToken.None);
            Status = result.Success ? "已断开" : $"断开失败：{result.Failure}";
        }
        catch (Exception ex)
        {
            Status = $"断开异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task EnableDeviceAsync(DeviceRowViewModel device)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = $"正在请求启用 {device.Name}（可能出现 UAC 提示）…";

        try
        {
            var result = await _host.EnableDeviceAsync(device.DeviceInstanceId, CancellationToken.None);
            Status = result.Success
                ? $"启用请求完成：{result.Outcome}"
                : $"启用失败：{result.Outcome} {result.Detail} {result.Win32Message}";
        }
        catch (Exception ex)
        {
            Status = $"启用异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task RefreshAsync()
    {
        _host.RequestImmediateCycle();
        Status = "已请求刷新";
        return Task.CompletedTask;
    }

    public void ReportDispatchFailure(Exception ex) => _dispatcher.TryEnqueue(() => Status = $"UI 更新失败：{ex.Message}");
}

/// <summary>Row for a PnP network device, physical or filtered out.</summary>
public sealed class DeviceRowViewModel
{
    public DeviceRowViewModel(ManagedDevice device)
    {
        DeviceInstanceId = device.Record.DeviceInstanceId;
        Name = device.Record.FriendlyName ?? device.Record.DeviceDescription ?? DeviceInstanceId;
        Category = device.Classification.Category.ToString();
        IsPhysical = device.Classification.IsPhysical;
        IsEnabled = device.IsEnabled;
        ProblemCode = device.Record.ProblemCode;
        StatusText = device.Record.IsPresent
            ? device.IsEnabled ? "已启用" : $"未启用（problem {device.Record.ProblemCode}）"
            : "当前不存在";
        Rule = device.Classification.Rule;
        Reason = device.Classification.Reason;
        Service = device.Record.Service ?? "—";
        Enumerator = device.Record.EnumeratorName ?? "—";
        MediaType = device.Record.PhysicalMediaType?.ToString() ?? "—";
        NetCfgInstanceId = device.Record.NetCfgInstanceId ?? "—";
    }

    public string DeviceInstanceId { get; }

    public string Name { get; }

    public string Category { get; }

    public bool IsPhysical { get; }

    public bool IsEnabled { get; }

    public uint ProblemCode { get; }

    public string StatusText { get; }

    public string Rule { get; }

    public string Reason { get; }

    public string Service { get; }

    public string Enumerator { get; }

    public string MediaType { get; }

    public string NetCfgInstanceId { get; }

    public string Summary =>
        $"{Name}｜{StatusText}｜{(IsPhysical ? "物理设备" : "已过滤")}｜{DeviceInstanceId}";

    public string DetailLine =>
        $"规则：{Rule}　服务：{Service}　枚举器：{Enumerator}　mediaType：{MediaType}";
}
