using System.Collections.ObjectModel;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.App.ViewModels;

/// <summary>Backing view model for the Ethernet page: physical NICs plus all interfaces.</summary>
public sealed class EthernetViewModel : ObservableObject
{
    private string _summary = "尚未采集";

    public ObservableCollection<InterfaceRowViewModel> EthernetInterfaces { get; } = new();

    public ObservableCollection<InterfaceRowViewModel> AllInterfaces { get; } = new();

    public ObservableCollection<DeviceRowViewModel> EthernetDevices { get; } = new();

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public void Update(GuardianSnapshot snapshot)
    {
        EthernetInterfaces.Clear();
        AllInterfaces.Clear();

        foreach (var iface in snapshot.Interfaces)
        {
            var row = new InterfaceRowViewModel(iface);
            AllInterfaces.Add(row);

            // The "physical Ethernet" card must not list virtual adapters or the Bluetooth PAN, which
            // also report an 802.3 interface type.
            if (iface.Kind == InterfaceKind.Ethernet && iface.IsPhysicalDevice != false)
            {
                EthernetInterfaces.Add(row);
            }
        }

        EthernetDevices.Clear();
        foreach (var device in snapshot.EthernetDevices)
        {
            EthernetDevices.Add(new DeviceRowViewModel(device));
        }

        var up = snapshot.Interfaces.Count(i =>
            i.Kind == InterfaceKind.Ethernet && i.IsPhysicalDevice != false && i.IsUp);
        var withInternet = snapshot.Interfaces.Count(i =>
            i.Kind == InterfaceKind.Ethernet && i.IsPhysicalDevice != false && i.Probe?.IsOnline == true);
        Summary = $"{EthernetInterfaces.Count} 个物理以太网，{up} 个链路已连接，{withInternet} 个可访问外网";
    }
}

/// <summary>One interface row for the tables.</summary>
public sealed class InterfaceRowViewModel
{
    public InterfaceRowViewModel(InterfaceRuntimeState state)
    {
        Name = state.Name;
        Kind = state.Kind.ToString();
        LinkState = state.IsUp ? "Up" : "Down";
        Ipv4 = state.Ipv4Addresses.Count == 0 ? "—" : string.Join(", ", state.Ipv4Addresses);
        Gateway = state.PrimaryGateway ?? "—";
        Dns = state.DnsServers.Count == 0 ? "—" : string.Join(", ", state.DnsServers);
        Mac = string.IsNullOrEmpty(state.MacAddress) ? "—" : state.MacAddress!;
        Metrics = state.MetricDescription;
        IsDefaultRoute = state.IsDefaultRoute ? "是" : "否";
        Probe = state.Probe is null
            ? "未探测"
            : state.Probe.IsOnline ? $"在线（{state.Probe.SuccessCount}/{state.Probe.AttemptCount}）"
            : state.Probe.CaptivePortalSuspected ? "疑似被认证页拦截"
            : $"离线（{state.Probe.SuccessCount}/{state.Probe.AttemptCount}）";
        Description = state.Description;
        Id = state.Id;
        HasInternet = state.Probe?.IsOnline == true;
    }

    public string Name { get; }

    public string Kind { get; }

    public string LinkState { get; }

    public string Ipv4 { get; }

    public string Gateway { get; }

    public string Dns { get; }

    public string Mac { get; }

    public string Metrics { get; }

    public string IsDefaultRoute { get; }

    public string Probe { get; }

    public string Description { get; }

    public string Id { get; }

    public bool HasInternet { get; }
}
