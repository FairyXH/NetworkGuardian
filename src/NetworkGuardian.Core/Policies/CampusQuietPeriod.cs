using NetworkGuardian.Core.Configuration;

namespace NetworkGuardian.Core.Policies;

public static class CampusQuietPeriod
{
    public static bool IsActive(WifiSettings settings, DateTimeOffset now)
    {
        if (!settings.CampusQuietPeriodEnabled)
        {
            return false;
        }

        var local = now.ToLocalTime();
        var minute = (local.Hour * 60) + local.Minute;
        var start = settings.CampusQuietStartMinutes;
        var end = settings.CampusQuietEndMinutes;

        return start == end || (start < end
            ? minute >= start && minute < end
            : minute >= start || minute < end);
    }

    public static bool IsCampusSsid(WifiSettings settings, string ssid) =>
        settings.CampusNetworkSsids.Any(value =>
            string.Equals(value, ssid, StringComparison.OrdinalIgnoreCase));
}
