namespace NetworkGuardian.Core.Configuration;

/// <summary>
/// Canonical on-disk locations. Everything NetworkGuardian writes lives under
/// <c>%LOCALAPPDATA%\NetworkGuardian</c> unless the test-only environment variable
/// <c>NETWORKGUARDIAN_CONFIG_ROOT</c> overrides the root.
/// </summary>
public static class GuardianPaths
{
    public const string RootOverrideVariable = "NETWORKGUARDIAN_CONFIG_ROOT";

    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return Path.GetFullPath(overridden);
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.Combine(Path.GetTempPath(), "NetworkGuardian");
            }

            return Path.Combine(localAppData, "NetworkGuardian");
        }
    }

    public static string ConfigFile => Path.Combine(Root, "config.json");

    public static string ConfigBackupFile => Path.Combine(Root, "config.backup.json");

    public static string ConfigCorruptFile => Path.Combine(Root, "config.invalid.json");

    public static string LogDirectory => Path.Combine(Root, "Logs");

    public static string HelperDirectory => Path.Combine(Root, "helper");

    public static string StateFile => Path.Combine(Root, "state.json");

    /// <summary>
    /// The self-maintained wireless network library: 802.1X/EAP accounts and their parameters.
    /// Independent of the Windows profile store; passwords are stored DPAPI-protected.
    /// </summary>
    public static string WifiCredentialFile => Path.Combine(Root, "wifi-networks.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(HelperDirectory);
    }

    public static string CreateHelperRequestPath() =>
        Path.Combine(HelperDirectory, $"request-{Guid.NewGuid():N}.json");

    public static string CreateHelperResponsePath() =>
        Path.Combine(HelperDirectory, $"response-{Guid.NewGuid():N}.json");
}
