using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Helper;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Windows.Devices;

namespace NetworkGuardian.Windows.Network;

/// <summary>A bounded helper-side lease, independent of the UI's normal shutdown path.</summary>
internal static class WfpOutletLease
{
    internal static bool IsEligibleAddress(string value, out IPAddress? address)
    {
        if (!IPAddress.TryParse(value, out address) || IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv4MappedToIPv6 ||
            address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
            (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] >= 224))
        { address = null; return false; }
        return true;
    }

    internal static IPAddress[] SelectTargets(string deviceId, IReadOnlyCollection<string> requested,
        IReadOnlyList<InterfaceRuntimeState> interfaces, IReadOnlyList<DefaultRouteInfo> routes)
    {
        if (requested.Count is 0 or > 32 || string.IsNullOrWhiteSpace(deviceId)) return Array.Empty<IPAddress>();
        var old = interfaces.FirstOrDefault(i => i.IsPhysicalDevice == true &&
            i.Kind is InterfaceKind.Ethernet or InterfaceKind.Wifi &&
            string.Equals(i.DeviceInstanceId, deviceId, StringComparison.OrdinalIgnoreCase));
        var v4Outlet = DefaultRouteSelector.Order(routes.Where(r => r.AddressFamily == 2)).FirstOrDefault();
        if (old is null || v4Outlet is null || DefaultRouteSelector.MatchesInterface(v4Outlet, old)) return Array.Empty<IPAddress>();
        var owned = old.Ipv4Addresses.Concat(old.Ipv6Addresses).Where(a => IsEligibleAddress(a, out _))
            .Select(a => OutletEndpointTracker.Canonical(IPAddress.Parse(a))).ToHashSet(StringComparer.Ordinal);
        var activeAddresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in new[] { 2, 23 })
        {
            var route = DefaultRouteSelector.Order(routes.Where(r => r.AddressFamily == family)).FirstOrDefault();
            var active = DefaultRouteSelector.FindInterface(route, interfaces);
            if (active is null) continue;
            foreach (var value in (family == 2 ? active.Ipv4Addresses : active.Ipv6Addresses))
                if (IPAddress.TryParse(value, out var address)) activeAddresses.Add(OutletEndpointTracker.Canonical(address));
        }
        return requested.Where(a => IsEligibleAddress(a, out _)).Select(IPAddress.Parse)
            .Where(a => owned.Contains(OutletEndpointTracker.Canonical(a)))
            .Where(a => !activeAddresses.Contains(OutletEndpointTracker.Canonical(a)))
            .DistinctBy(OutletEndpointTracker.Canonical).ToArray();
    }

    internal static string? ReleaseReason(TimeSpan elapsed, int minimumSeconds, int maximumSeconds,
        bool ownerAlive, bool cancelled, bool outletStillSuperseded, int remaining)
    {
        if (!ownerAlive) return "owner exited";
        if (cancelled) return "cancelled";
        if (!outletStillSuperseded) return "outlet/address changed";
        if (elapsed.TotalSeconds >= maximumSeconds) return "maximum lease reached";
        if (elapsed.TotalSeconds >= minimumSeconds && remaining == 0) return "observed old endpoints released";
        return null;
    }

    internal static string Run(HelperRequest request, string requestPath, Action<string> log)
    {
        var settings = request.ConnectionMigration;
        if (settings is null || !settings.WfpBlockOnSwitch || settings.RestartOldAdapterOnSwitch ||
            settings.WfpBlockSeconds is < 1 or > 30 || settings.WfpMaxBlockSeconds < settings.WfpBlockSeconds ||
            settings.WfpMaxBlockSeconds > 120 || request.OldLocalAddresses.Length is 0 or > 32 ||
            request.OldLocalAddresses.Any(a => !IsEligibleAddress(a, out _)) ||
            !int.TryParse(request.RequestedBy, out var ownerPid) || request.OwnerProcessStartTimeUtcTicks <= 0 ||
            DateTimeOffset.UtcNow - request.RequestedAtUtc > TimeSpan.FromMinutes(2))
            throw new ArgumentException("Invalid or expired WFP lease request.");
        using var owner = Process.GetProcessById(ownerPid);
        if (owner.StartTime.ToUniversalTime().Ticks != request.OwnerProcessStartTimeUtcTicks ||
            !string.Equals(owner.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("WFP lease owner identity does not match this application.");

        var devices = new PnpDeviceInventory().Enumerate().Where(d => Guid.TryParse(d.NetCfgInstanceId, out _))
            .GroupBy(d => Guid.Parse(d.NetCfgInstanceId!)).ToDictionary(g => g.Key, g => g.First());
        var provider = new NetworkInterfaceProvider(guid => devices.GetValueOrDefault(guid));
        IPAddress[] Targets() => SelectTargets(request.DeviceInstanceId, request.OldLocalAddresses,
            provider.GetInterfaces(), provider.GetDefaultRoutes().Concat(provider.GetDefaultRoutes(23)).ToArray());
        var addresses = Targets();
        if (addresses.Length == 0) return "No superseded physical outlet addresses remain; no filters installed.";
        if (owner.HasExited || !File.Exists(requestPath)) return "Cancelled before creating WFP filters.";
        var original = OutletEndpointTracker.Read(addresses);
        var remaining = original.Count;
        var started = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.WfpMaxBlockSeconds);
        string? reason;
        using (var block = new WfpDynamicBlock(addresses))
        {
            log($"WFP dynamic lease started: filters={block.FilterIds.Count}, endpoints={original.Count}, " +
                $"addresses={string.Join(',', addresses.Select(a => a.ToString()))}, max={settings.WfpMaxBlockSeconds}s");
            while (true)
            {
                var currentTargets = Targets().Select(OutletEndpointTracker.Canonical).ToHashSet(StringComparer.Ordinal);
                var stillSuperseded = addresses.All(a => currentTargets.Contains(OutletEndpointTracker.Canonical(a)));
                reason = ReleaseReason(DateTimeOffset.UtcNow >= deadline ? TimeSpan.FromSeconds(settings.WfpMaxBlockSeconds) : started.Elapsed,
                    settings.WfpBlockSeconds, settings.WfpMaxBlockSeconds, !owner.HasExited,
                    !File.Exists(requestPath), stillSuperseded, remaining);
                if (reason is not null) break;
                Thread.Sleep(1000);
                // Endpoints disappearing is evidence of release; an existing UDP endpoint is not traffic activity.
                original.IntersectWith(OutletEndpointTracker.Read(addresses));
                remaining = original.Count;
            }
        }
        var detail = $"WFP filters removed: {reason}; elapsed={started.Elapsed.TotalSeconds:F1}s; " +
            $"remaining observed endpoints={remaining}. UDP wildcard bindings/activity and application reconnection are not verified.";
        log(detail);
        return detail;
    }
}
