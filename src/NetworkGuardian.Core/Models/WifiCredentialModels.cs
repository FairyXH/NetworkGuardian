namespace NetworkGuardian.Core.Models;

public enum WifiCredentialKind
{
    Enterprise = 0,
    Personal,
    Open,
}

/// <summary>802.1X authentication suite used by an enterprise Wi-Fi profile.</summary>
public enum WifiEnterpriseAuth
{
    /// <summary>WPA2-Enterprise (802.1X with CCMP). The common campus configuration.</summary>
    Wpa2Enterprise = 0,

    /// <summary>WPA3-Enterprise (192-bit or transition mode).</summary>
    Wpa3Enterprise,

    /// <summary>Legacy WPA-Enterprise.</summary>
    WpaEnterprise,
}

/// <summary>Outer EAP method (and, where it applies, the inner method) of an enterprise Wi-Fi profile.</summary>
public enum WifiEapMethod
{
    /// <summary>PEAP with inner MSCHAPv2 (EAP type 25 + 26). Username/password based.</summary>
    PeapMschapv2 = 0,

    /// <summary>EAP-TLS (EAP type 13): mutual certificate authentication, no user password.</summary>
    Tls,

    /// <summary>
    /// Anything else: EAP-TTLS (needs a third-party EAP host and is not generated), PEAP-TLS,
    /// certificate selection, MAC randomization. Requires <see cref="WifiNetworkCredential.ProfileXmlOverride"/>.
    /// </summary>
    CustomXml,
}

/// <summary>
/// One entry of the application's own wireless network library.
/// </summary>
/// <remarks>
/// The library is deliberately independent of the Windows profile store: it is the source of truth for
/// 802.1X/EAP credentials, and a Windows WLAN profile is generated from it on demand (see
/// <c>EnterpriseProfileBuilder</c>). Windows itself never prompts for these credentials and the
/// password is stored DPAPI-protected (<c>CryptProtectData</c>, current user), never in plain text.
/// <see cref="Password"/> only exists in memory and is excluded from serialization.
/// </remarks>
public sealed class WifiNetworkCredential
{
    /// <summary>Stable identity of the entry, used by the UI to keep the selection across edits.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>SSID the entry authenticates against. The library is keyed by this value.</summary>
    public string Ssid { get; set; } = string.Empty;

    /// <summary>Name of the Windows profile that is generated. Defaults to <see cref="Ssid"/>.</summary>
    public string? ProfileName { get; set; }

    /// <summary>Authentication family. Enterprise is zero to preserve version-1 library semantics.</summary>
    public WifiCredentialKind Kind { get; set; } = WifiCredentialKind.Enterprise;

    /// <summary>Security observed during scan; used to generate a compatible personal/open profile.</summary>
    public WifiSecurity Security { get; set; } = WifiSecurity.Wpa2Enterprise;

    public WifiEnterpriseAuth Auth { get; set; } = WifiEnterpriseAuth.Wpa2Enterprise;

    public WifiEapMethod Eap { get; set; } = WifiEapMethod.PeapMschapv2;

    /// <summary>User identity (the EAP user name; usually <c>user@realm</c> or <c>DOMAIN\user</c>).</summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>Optional outer (unauthenticated) identity for tunneled methods.</summary>
    public string? AnonymousIdentity { get; set; }

    /// <summary>Optional domain sent with MSCHAPv2.</summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Password in memory only. It is never serialized: the persisted form is
    /// <see cref="PasswordProtected"/>. The property is only populated when the vault decrypted the
    /// stored value (or when the UI is editing the entry).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Password { get; set; }

    /// <summary>Base64 DPAPI blob of the password, scoped to the current user.</summary>
    public string? PasswordProtected { get; set; }

    /// <summary>
    /// True when a stored password exists but could not be decrypted (another user, another machine,
    /// or a reset DPAPI master key). The entry is kept, but it cannot authenticate until the password
    /// is entered again.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PasswordDecryptionFailed { get; set; }

    public DateTimeOffset? PasswordUpdatedUtc { get; set; }

    /// <summary>Use the signed-in Windows account instead of the stored identity.</summary>
    public bool UseWinLogonCredentials { get; set; }

    /// <summary>Server names accepted during PEAP/TTLS server validation (empty = accept any).</summary>
    public List<string> ServerNames { get; set; } = new();

    /// <summary>Trusted root CA SHA-1 thumbprints (40 hex characters, no separators).</summary>
    public List<string> TrustedRootCaThumbprints { get; set; } = new();

    /// <summary>
    /// Client certificate thumbprint for <see cref="WifiEapMethod.Tls"/>. Empty means "let Windows pick
    /// a suitable certificate" (<c>SimpleCertSelection</c>).
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// When true the user is never prompted for server validation. Only sensible with a configured
    /// trust root; with neither a thumbprint nor a prompt an unattended connect cannot succeed.
    /// </summary>
    public bool DisableUserPromptForServerValidation { get; set; }

    /// <summary>Automatic connection (Windows connects on its own once the profile exists).</summary>
    public bool ConnectAutomatically { get; set; } = true;

    /// <summary>Mark the SSID as non-broadcast in the generated profile.</summary>
    public bool Hidden { get; set; }

    /// <summary>When false the entry is kept but never used for a connection.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Complete profile XML used verbatim instead of the generated one. Escape hatch for campus
    /// deployments whose schema the generator does not cover (custom TTLS variants, certificate
    /// selection, MAC randomization, ...).
    /// </summary>
    public string? ProfileXmlOverride { get; set; }

    /// <summary>SHA-256 of the XML written to Windows the last time the profile was applied.</summary>
    public string? AppliedFingerprint { get; set; }

    public DateTimeOffset? LastAppliedUtc { get; set; }

    public string? Notes { get; set; }

    public string EffectiveProfileName => string.IsNullOrWhiteSpace(ProfileName) ? Ssid : ProfileName!;

    /// <summary>True when the entry needs a password to authenticate (EAP-TLS uses a certificate).</summary>
    public bool RequiresPassword => Kind switch
    {
        WifiCredentialKind.Open => false,
        WifiCredentialKind.Personal => true,
        _ => Eap != WifiEapMethod.Tls && !UseWinLogonCredentials,
    };

    public bool IsEnterprise => Kind == WifiCredentialKind.Enterprise;
}

/// <summary>Persisted document of the self-maintained wireless network library.</summary>
public sealed class WifiCredentialLibrary
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public List<WifiNetworkCredential> Networks { get; set; } = new();

    public WifiNetworkCredential? Find(string? ssid) =>
        string.IsNullOrWhiteSpace(ssid)
            ? null
            : Networks.FirstOrDefault(n => string.Equals(n.Ssid, ssid, StringComparison.OrdinalIgnoreCase));

    public WifiNetworkCredential? FindByProfile(string? profileName) =>
        string.IsNullOrWhiteSpace(profileName)
            ? null
            : Networks.FirstOrDefault(n =>
                string.Equals(n.EffectiveProfileName, profileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Ssid, profileName, StringComparison.OrdinalIgnoreCase));
}
