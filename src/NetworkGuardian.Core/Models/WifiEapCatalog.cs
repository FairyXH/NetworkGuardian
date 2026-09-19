namespace NetworkGuardian.Core.Models;

/// <summary>
/// The set of networks the built-in library can authenticate for. The decision engine is pure, so it
/// receives this summary instead of the library itself (which holds passwords).
/// </summary>
public sealed record WifiEapCatalog
{
    public static WifiEapCatalog Empty { get; } = new();

    /// <summary>SSIDs with a usable account (credentials present and decryptable).</summary>
    public IReadOnlySet<string> SsidsWithCredentials { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Authoritative Windows profile name for each SSID.</summary>
    public IReadOnlyDictionary<string, string> ProfileNamesBySsid { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the library can authenticate for that SSID.</summary>
    public bool HasCredential(string? ssid) =>
        !string.IsNullOrWhiteSpace(ssid) && SsidsWithCredentials.Contains(ssid);

    public string? ProfileNameFor(string? ssid) =>
        !string.IsNullOrWhiteSpace(ssid) && ProfileNamesBySsid.TryGetValue(ssid, out var profile)
            ? profile
            : null;

    public int Count => SsidsWithCredentials.Count;

    public string Describe() => Count == 0
        ? "自维护无线网络库为空"
        : $"自维护无线网络库包含 {Count} 个 802.1X 网络";
}
