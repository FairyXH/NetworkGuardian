using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

/// <summary>Builds a compact, deterministic metric order for physical Internet interfaces.</summary>
public static class InterfaceMetricPlanner
{
    private const int FirstMetric = 10;
    private const int MetricStep = 2;
    private const int InactiveMetric = 60;

    public static IReadOnlyDictionary<string, int> Plan(
        IReadOnlyList<InterfaceRuntimeState> interfaces,
        IReadOnlyList<WifiAdapterRuntimeState> wifiAdapters)
    {
        var physical = interfaces
            .Where(IsManagedPhysicalInterface)
            .ToList();
        var usable = physical
            .Where(i => i.IsUp && i.HasUsableIpv4 && i.HasDefaultGateway)
            .ToList();
        var verified = usable.Where(i => i.Probe?.IsOnline == true).ToList();

        // Until the first per-interface probe completes, prefer a usable wired path. Once probes
        // exist, only verified Internet paths lead the table; this prevents a linked but captive or
        // dead Ethernet adapter from black-holing a working Wi-Fi connection.
        var leaders = verified.Count > 0 ? verified : usable;
        var orderedLeaders = leaders
            .OrderBy(InterfaceClassRank)
            .ThenByDescending(i => LinkScore(i, wifiAdapters))
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < orderedLeaders.Count; index++)
        {
            result[orderedLeaders[index].Id] = FirstMetric + (index * MetricStep);
        }

        var followers = physical
            .Where(i => !result.ContainsKey(i.Id))
            .OrderBy(i => i.IsUp ? 0 : 1)
            .ThenBy(InterfaceClassRank)
            .ThenByDescending(i => LinkScore(i, wifiAdapters))
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < followers.Count; index++)
        {
            result[followers[index].Id] = InactiveMetric + (index * MetricStep);
        }

        return result;
    }

    public static string? ExpectedOutletId(
        IReadOnlyList<InterfaceRuntimeState> interfaces,
        IReadOnlyDictionary<string, int> metrics) =>
        interfaces
            .Where(IsManagedPhysicalInterface)
            .Where(i => i.IsUp && i.HasUsableIpv4 && i.HasDefaultGateway)
            .Where(i => metrics.ContainsKey(i.Id))
            .OrderBy(i => metrics[i.Id])
            .Select(i => i.Id)
            .FirstOrDefault();

    private static bool IsManagedPhysicalInterface(InterfaceRuntimeState state) =>
        state.IsPhysicalDevice != false && state.Kind is InterfaceKind.Ethernet or InterfaceKind.Wifi;

    private static int InterfaceClassRank(InterfaceRuntimeState state) => state.Kind switch
    {
        InterfaceKind.Ethernet => 0,
        InterfaceKind.Wifi => 1,
        _ => 2,
    };

    private static double LinkScore(
        InterfaceRuntimeState state,
        IReadOnlyList<WifiAdapterRuntimeState> wifiAdapters)
    {
        var megabits = state.SpeedBitsPerSecond / 1_000_000d;
        if (state.Kind != InterfaceKind.Wifi)
        {
            return megabits;
        }

        var signal = state.WlanInterfaceGuid is { } guid
            ? wifiAdapters.FirstOrDefault(adapter => adapter.InterfaceGuid == guid)?.SignalQuality ?? 0
            : 0;
        return megabits * Math.Max(1, signal) / 100d;
    }
}
