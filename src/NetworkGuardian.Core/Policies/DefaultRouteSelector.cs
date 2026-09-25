using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

public static class DefaultRouteSelector
{
    public static IReadOnlyList<DefaultRouteInfo> Order(IEnumerable<DefaultRouteInfo> routes) => routes
        .OrderBy(route => route.EffectiveMetric ?? int.MaxValue)
        .ThenBy(route => route.RouteMetric ?? int.MaxValue)
        .ThenBy(route => route.InterfaceMetric ?? int.MaxValue)
        .ThenBy(route => route.InterfaceIndex)
        .ToList();

    public static bool MatchesInterface(DefaultRouteInfo route, InterfaceRuntimeState state)
    {
        if (route.InterfaceLuid is { } luid)
        {
            return string.Equals(state.Id, $"luid:{luid}", StringComparison.OrdinalIgnoreCase);
        }

        return route.InterfaceIndex != 0 && route.InterfaceIndex == state.InterfaceIndex;
    }

    public static InterfaceRuntimeState? FindInterface(
        DefaultRouteInfo? route,
        IEnumerable<InterfaceRuntimeState> interfaces) =>
        route is null ? null : interfaces.FirstOrDefault(state => MatchesInterface(route, state));
}
