using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Windows.Location;

/// <summary>
/// Reads the Windows Location permission state so the UI can explain why Wi-Fi scanning returns
/// ERROR_ACCESS_DENIED instead of silently showing an empty network list.
/// </summary>
/// <remarks>
/// Windows 11 gates <c>WlanScan</c>, <c>WlanGetNetworkBssList</c> and other APIs that reveal SSID,
/// BSSID or RSSI behind the Location privacy setting. NetworkGuardian never attempts to bypass that
/// setting; it only reports it.
/// </remarks>
public sealed class LocationPermissionService : ILocationPermissionService
{
    private const string ConsentStorePath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";

    private const string NonPackagedSubKey = "NonPackaged";

    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors";

    private readonly ILogger<LocationPermissionService> _logger;
    private readonly Func<LocationPermissionSnapshot>? _nativeObservation;

    public LocationPermissionService(
        Func<LocationPermissionSnapshot>? nativeObservation = null,
        ILogger<LocationPermissionService>? logger = null)
    {
        _nativeObservation = nativeObservation;
        _logger = logger ?? NullLogger<LocationPermissionService>.Instance;
    }

    public Task<LocationPermissionSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public LocationPermissionSnapshot Read()
    {
        bool? globalAllowed = null;
        bool? serviceDisabled = null;
        bool? appAllowed = null;
        var details = new List<string>();

        try
        {
            using var policy = Registry.LocalMachine.OpenSubKey(PolicyPath);
            if (policy?.GetValue("DisableLocation") is int disabled)
            {
                serviceDisabled = disabled == 0;
                details.Add($"group policy DisableLocation={disabled}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read the location group policy");
        }

        try
        {
            using var userKey = Registry.CurrentUser.OpenSubKey(ConsentStorePath);
            var value = userKey?.GetValue("Value") as string;
            if (!string.IsNullOrEmpty(value))
            {
                globalAllowed = value.Equals("Allow", StringComparison.OrdinalIgnoreCase);
                details.Add($"user consent store Value={value}");
            }

            using var machineKey = Registry.LocalMachine.OpenSubKey(ConsentStorePath);
            var machineValue = machineKey?.GetValue("Value") as string;
            if (machineValue is not null && globalAllowed is null)
            {
                globalAllowed = machineValue.Equals("Allow", StringComparison.OrdinalIgnoreCase);
                details.Add($"machine consent store Value={machineValue}");
            }

            appAllowed = ReadPerAppConsent(details);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read the location consent store");
        }

        var native = _nativeObservation?.Invoke();
        var blocked = native?.ScanBlockedByPolicy ?? false;

        if (native?.Detail is { Length: > 0 } nativeDetail)
        {
            details.Add(nativeDetail);
        }

        return new LocationPermissionSnapshot
        {
            AppLocationAllowed = appAllowed ?? globalAllowed,
            LocationServiceEnabled = serviceDisabled,
            ScanBlockedByPolicy = blocked,
            BlockedOperation = native?.BlockedOperation,
            Detail = details.Count == 0 ? null : string.Join("; ", details),
            ObservedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private bool? ReadPerAppConsent(List<string> details)
    {
        try
        {
            using var nonPackaged = Registry.CurrentUser.OpenSubKey($@"{ConsentStorePath}\{NonPackagedSubKey}");
            if (nonPackaged is null)
            {
                return null;
            }

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return null;
            }

            var normalized = processPath.Replace('\\', '#').Replace('/', '#');

            foreach (var name in nonPackaged.GetSubKeyNames())
            {
                if (!name.Contains('#'))
                {
                    continue;
                }

                var candidate = name.Replace(':', '#').Replace('\\', '#');
                var matches = candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                              candidate.EndsWith(Path.GetFileName(processPath), StringComparison.OrdinalIgnoreCase);

                if (!matches)
                {
                    continue;
                }

                using var entry = nonPackaged.OpenSubKey(name);
                var value = entry?.GetValue("Value") as string;
                if (!string.IsNullOrEmpty(value))
                {
                    details.Add($"desktop app consent for {Path.GetFileName(processPath)} = {value}");
                    return value.Equals("Allow", StringComparison.OrdinalIgnoreCase);
                }
            }

            details.Add("no explicit consent entry was found for this desktop executable");
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Human readable guidance shown in the UI when scanning is blocked.</summary>
    public static string BuildGuidance(LocationPermissionSnapshot snapshot)
    {
        if (!snapshot.HasProblem)
        {
            return "Location permission looks fine for Wi-Fi scanning.";
        }

        return "Windows blocked a Wi-Fi API that reveals network identifiers. Open " +
               "Settings > Privacy & security > Location, enable 'Location services' and enable " +
               "'Let desktop apps access your location', then restart NetworkGuardian. " +
               "Windows 11 requires this for Wi-Fi scanning, BSSID and RSSI access.";
    }
}
