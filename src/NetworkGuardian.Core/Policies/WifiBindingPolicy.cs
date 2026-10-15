using NetworkGuardian.Core.Configuration;

namespace NetworkGuardian.Core.Policies;

public static class WifiBindingPolicy
{
    // Keep the v6 JSON field name so existing installations retain their assignments.
    public static Guid? Owner(WifiSettings settings, string ssid) =>
        settings.CampusWifiAdapterAssignments.TryGetValue(ssid, out var value) &&
        Guid.TryParse(value, out var guid) && guid != Guid.Empty ? guid : null;

    public static bool Allows(WifiSettings settings, string ssid, Guid adapter) =>
        Owner(settings, ssid) is not { } owner || owner == adapter;
}
