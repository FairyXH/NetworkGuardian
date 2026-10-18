using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

/// <summary>One matching contract for sampling, history, reporting, and model observations.</summary>
public sealed record NetworkQualityIdentity(Guid Adapter, string Ssid, NetworkBand Band, InterfaceKind Kind,
    string InterfaceId, string NetworkId, string NetworkName, string? DeviceInstanceId)
{
    public static NetworkQualityIdentity? Create(InterfaceRuntimeState iface, WifiAdapterRuntimeState? wifi = null)
    {
        if (wifi is not null && !wifi.IsConnected) return null;
        var adapter = iface.WlanInterfaceGuid ?? iface.AdapterGuid ?? StableGuid(iface.Id);
        if (iface.Kind == InterfaceKind.Wifi && wifi?.CurrentSsid is { } ssid)
            return new(adapter, ssid, wifi.Connection?.Band ?? NetworkBand.Unknown, iface.Kind,
                iface.Id, "", ssid, iface.DeviceInstanceId);
        var addresses = iface.Ipv4Addresses.Concat(iface.Ipv6Addresses)
            .Where(s => IPAddress.TryParse(s, out var address) && IsUsableSource(address))
            .Order(StringComparer.Ordinal).ToArray();
        var fingerprint = string.Join('|', new[] { iface.Kind.ToString() }.Concat(addresses)
            .Concat(iface.Ipv4Gateways.Order(StringComparer.Ordinal)).Concat(iface.DnsServers.Order(StringComparer.Ordinal)));
        return new(adapter, "", NetworkBand.Unknown, iface.Kind, iface.Id,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))),
            $"{KindName(iface.Kind)} · {iface.PrimaryGateway ?? addresses.FirstOrDefault() ?? iface.Name}", iface.DeviceInstanceId);
    }

    public bool Matches(LinkQualityEstimate quality) => quality.Adapter == Adapter && quality.Kind == Kind &&
        quality.Ssid == Ssid && quality.Band == Band && quality.NetworkId == NetworkId;

    public LinkQualityEstimate? Find(IReadOnlyList<LinkQualityEstimate> quality) => quality.Where(Matches)
        .OrderByDescending(q => q.UpdatedAtUtc).FirstOrDefault();

    public static bool Fresh(DateTimeOffset at, DateTimeOffset now) => at <= now.AddSeconds(10) && now - at <= TimeSpan.FromMinutes(5);

    public static IPAddress? Source(InterfaceRuntimeState iface) => iface.Ipv4Addresses.Concat(iface.Ipv6Addresses)
        .Select(s => IPAddress.TryParse(s, out var address) ? address : null).FirstOrDefault(a => a is not null && IsUsableSource(a));

    private static bool IsUsableSource(IPAddress address) => !IPAddress.IsLoopback(address) &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal &&
        !address.IsIPv6Multicast && (address.AddressFamily != AddressFamily.InterNetwork ||
            address.GetAddressBytes()[0] is > 0 and < 224 && !(address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254));

    public static string? UnavailableReason(InterfaceRuntimeState iface) => iface.Kind == InterfaceKind.Loopback
        ? "仅本机回环，不属于外网链路；外网质量评分不适用。"
        : !iface.IsUp ? "目前未连接，无法评价当前网络质量。"
        : Source(iface) is null ? "尚无可用的 IPv4/IPv6 源地址，无法进行质量采样。"
        : !iface.HasDefaultGateway && !iface.IsDefaultRoute ? "仅局域网接口，未发现默认外网路由；外网质量评分不适用。"
        : null;

    public static string KindName(InterfaceKind kind) => kind switch
    {
        InterfaceKind.Ethernet => "有线网络", InterfaceKind.Wifi => "无线网络", InterfaceKind.Tunnel => "隧道 / VPN",
        InterfaceKind.Virtual => "虚拟网络", InterfaceKind.Loopback => "本机回环", InterfaceKind.Other => "其他网络",
        _ => "未分类网络",
    };

    private static Guid StableGuid(string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 16));
}
