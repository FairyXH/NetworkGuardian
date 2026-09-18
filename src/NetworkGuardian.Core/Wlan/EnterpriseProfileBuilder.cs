using System.Text;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Wlan;

/// <summary>Outcome of a profile XML generation attempt.</summary>
public readonly record struct EnterpriseProfileBuildResult(
    bool Success,
    string? Xml,
    IReadOnlyList<string> Problems)
{
    public string? Failure => Problems.Count == 0 ? null : string.Join("；", Problems);
}

/// <summary>
/// Generates the Windows WLAN profile XML for an 802.1X/EAP network from one library entry.
/// </summary>
/// <remarks>
/// The generated document follows the Windows WLAN profile schema
/// (<c>http://www.microsoft.com/networking/WLAN/profile/v1</c>) plus the OneX/EAP configuration
/// (<c>http://www.microsoft.com/networking/OneX/v1</c> and the <c>provisioning/*</c> EAP schemas).
/// Only methods that ship in the box are generated:
/// <list type="bullet">
/// <item>PEAP + inner MSCHAPv2 (EAP type 25 / 26) - username and password.</item>
/// <item>EAP-TLS (EAP type 13) - certificate based, no password.</item>
/// </list>
/// Everything else (EAP-TTLS with a vendor EAP method, PEAP-TLS, certificate selection, MAC
/// randomization, ...) has to be supplied verbatim through <see cref="WifiNetworkCredential.ProfileXmlOverride"/>:
/// inventing a schema that was never validated against the EAP host would only produce a profile that
/// Windows rejects at connect time.
/// </remarks>
public static class EnterpriseProfileBuilder
{
    public const string WlanProfileNamespace = "http://www.microsoft.com/networking/WLAN/profile/v1";

    private const string OneXNamespace = "http://www.microsoft.com/networking/OneX/v1";
    private const string EapHostConfigNamespace = "http://www.microsoft.com/provisioning/EapHostConfig";
    private const string EapCommonNamespace = "http://www.microsoft.com/provisioning/EapCommon";
    private const string BaseEapConnectionNamespace = "http://www.microsoft.com/provisioning/BaseEapConnectionPropertiesV1";
    private const string MsPeapNamespace = "http://www.microsoft.com/provisioning/MsPeapConnectionPropertiesV1";
    private const string MsChapV2Namespace = "http://www.microsoft.com/provisioning/MsChapV2ConnectionPropertiesV1";
    private const string EapTlsNamespace = "http://www.microsoft.com/provisioning/EapTlsConnectionPropertiesV1";

    private const int EapTypeTls = 13;
    private const int EapTypePeap = 25;
    private const int EapTypeMsChapV2 = 26;

    /// <summary>Builds the profile XML for <paramref name="credential"/>. Passwords never appear in the returned problems.</summary>
    public static EnterpriseProfileBuildResult Build(WifiNetworkCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(credential.Ssid))
        {
            problems.Add("SSID 不能为空");
        }

        if (string.IsNullOrWhiteSpace(credential.EffectiveProfileName))
        {
            problems.Add("配置名称不能为空");
        }

        if (credential.Eap == WifiEapMethod.CustomXml && string.IsNullOrWhiteSpace(credential.ProfileXmlOverride))
        {
            problems.Add("EAP 方法为“自定义 XML”时必须提供完整的配置 XML（EAP-TTLS、PEAP-TLS、" +
                         "证书选择等内置方法未覆盖的场景）");
        }

        if (!string.IsNullOrWhiteSpace(credential.ProfileXmlOverride))
        {
            var overrideXml = credential.ProfileXmlOverride!;
            if (!overrideXml.Contains("<WLANProfile", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("自定义配置 XML 中未找到 <WLANProfile> 根元素");
            }

            if (problems.Count > 0)
            {
                return new EnterpriseProfileBuildResult(false, null, problems);
            }

            // Verbatim use: the operator owns this document, only whitespace is normalised away.
            return new EnterpriseProfileBuildResult(true, overrideXml.Trim(), problems);
        }

        if (credential.Eap is WifiEapMethod.PeapMschapv2)
        {
            if (!credential.UseWinLogonCredentials && string.IsNullOrWhiteSpace(credential.Identity))
            {
                problems.Add("PEAP-MSCHAPv2 需要账号（身份）");
            }

            if (credential.RequiresPassword && string.IsNullOrEmpty(credential.Password))
            {
                problems.Add("PEAP-MSCHAPv2 需要密码");
            }
        }

        if (problems.Count > 0)
        {
            return new EnterpriseProfileBuildResult(false, null, problems);
        }

        var xml = new StringBuilder(2048);
        var authentication = AuthName(credential.Auth);
        var encryption = EncryptionName(credential.Auth);

        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine($"<WLANProfile xmlns=\"{WlanProfileNamespace}\">");
        xml.AppendLine($"  <name>{Escape(credential.EffectiveProfileName)}</name>");
        xml.AppendLine("  <SSIDConfig>");
        xml.AppendLine("    <SSID>");
        xml.AppendLine($"      <hex>{ToHex(credential.Ssid)}</hex>");
        xml.AppendLine($"      <name>{Escape(credential.Ssid)}</name>");
        xml.AppendLine("    </SSID>");

        if (credential.Hidden)
        {
            xml.AppendLine("    <nonBroadcast>true</nonBroadcast>");
        }

        xml.AppendLine("  </SSIDConfig>");
        xml.AppendLine("  <connectionType>ESS</connectionType>");
        xml.AppendLine($"  <connectionMode>{(credential.ConnectAutomatically ? "auto" : "manual")}</connectionMode>");
        xml.AppendLine("  <MSM>");
        xml.AppendLine("    <security>");
        xml.AppendLine("      <authEncryption>");
        xml.AppendLine($"        <authentication>{authentication}</authentication>");
        xml.AppendLine($"        <encryption>{encryption}</encryption>");
        xml.AppendLine("        <useOneX>true</useOneX>");
        xml.AppendLine("      </authEncryption>");
        xml.AppendLine($"      <OneX xmlns=\"{OneXNamespace}\">");
        xml.AppendLine("        <authEncryption>");
        xml.AppendLine($"          <authentication>{authentication}</authentication>");
        xml.AppendLine($"          <encryption>{encryption}</encryption>");
        xml.AppendLine("          <useOneX>true</useOneX>");
        xml.AppendLine("          <cacheUserData>true</cacheUserData>");
        xml.AppendLine("        </authEncryption>");
        xml.AppendLine("        <EAPConfig>");
        xml.AppendLine($"          <EapHostConfig xmlns=\"{EapHostConfigNamespace}\">");
        xml.AppendLine("            <EapMethod>");
        xml.AppendLine($"              <Type xmlns=\"{EapCommonNamespace}\">{EapMethodType(credential.Eap)}</Type>");
        xml.AppendLine($"              <VendorId xmlns=\"{EapCommonNamespace}\">0</VendorId>");
        xml.AppendLine($"              <VendorType xmlns=\"{EapCommonNamespace}\">0</VendorType>");
        xml.AppendLine($"              <AuthorId xmlns=\"{EapCommonNamespace}\">0</AuthorId>");
        xml.AppendLine("            </EapMethod>");
        xml.AppendLine($"            <Config xmlns=\"{EapHostConfigNamespace}\">");

        if (credential.Eap == WifiEapMethod.PeapMschapv2)
        {
            AppendPeapConfiguration(xml, credential);
        }
        else
        {
            AppendTlsConfiguration(xml, credential);
        }

        xml.AppendLine("            </Config>");
        xml.AppendLine("          </EapHostConfig>");
        xml.AppendLine("        </EAPConfig>");
        xml.AppendLine("      </OneX>");
        xml.AppendLine("    </security>");
        xml.AppendLine("  </MSM>");
        xml.AppendLine("</WLANProfile>");

        return new EnterpriseProfileBuildResult(true, xml.ToString(), problems);
    }

    private static void AppendPeapConfiguration(StringBuilder xml, WifiNetworkCredential credential)
    {
        xml.AppendLine($"              <Eap xmlns=\"{BaseEapConnectionNamespace}\">");
        xml.AppendLine($"                <Type>{EapTypePeap}</Type>");
        xml.AppendLine($"                <EapType xmlns=\"{MsPeapNamespace}\">");
        xml.AppendLine("                  <ServerValidation>");
        xml.AppendLine($"                    <DisableUserPromptForServerValidation>{(credential.DisableUserPromptForServerValidation ? "true" : "false")}</DisableUserPromptForServerValidation>");

        if (credential.ServerNames.Count > 0)
        {
            var names = string.Join(";", credential.ServerNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => Escape(n.Trim())));
            xml.AppendLine($"                    <ServerNames>{names}</ServerNames>");
        }
        else
        {
            xml.AppendLine("                    <ServerNames></ServerNames>");
        }

        foreach (var thumbprint in credential.TrustedRootCaThumbprints.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            xml.AppendLine($"                    <TrustedRootCA>{Escape(NormaliseThumbprint(thumbprint))}</TrustedRootCA>");
        }

        xml.AppendLine("                  </ServerValidation>");
        xml.AppendLine("                  <FastReconnect>true</FastReconnect>");
        xml.AppendLine("                  <InnerEapOptional>false</InnerEapOptional>");
        xml.AppendLine($"                  <Eap xmlns=\"{BaseEapConnectionNamespace}\">");
        xml.AppendLine($"                    <Type>{EapTypeMsChapV2}</Type>");
        xml.AppendLine($"                    <EapType xmlns=\"{MsChapV2Namespace}\">");
        xml.AppendLine($"                      <UseWinLogonCredentials>{(credential.UseWinLogonCredentials ? "true" : "false")}</UseWinLogonCredentials>");

        if (!string.IsNullOrWhiteSpace(credential.Identity))
        {
            xml.AppendLine($"                      <UserName>{Escape(credential.Identity.Trim())}</UserName>");
        }

        if (credential.RequiresPassword && !string.IsNullOrEmpty(credential.Password))
        {
            xml.AppendLine($"                      <Password>{Escape(credential.Password!)}</Password>");
        }

        if (!string.IsNullOrWhiteSpace(credential.Domain))
        {
            xml.AppendLine($"                      <Domain>{Escape(credential.Domain!.Trim())}</Domain>");
        }

        xml.AppendLine("                    </EapType>");
        xml.AppendLine("                  </Eap>");
        xml.AppendLine("                  <EnableQuarantineChecks>false</EnableQuarantineChecks>");
        xml.AppendLine("                  <RequireCryptoBinding>false</RequireCryptoBinding>");
        xml.AppendLine("                </EapType>");
        xml.AppendLine("              </Eap>");
    }

    private static void AppendTlsConfiguration(StringBuilder xml, WifiNetworkCredential credential)
    {
        xml.AppendLine($"              <Eap xmlns=\"{BaseEapConnectionNamespace}\">");
        xml.AppendLine($"                <Type>{EapTypeTls}</Type>");
        xml.AppendLine($"                <EapType xmlns=\"{EapTlsNamespace}\">");
        xml.AppendLine("                  <ServerValidation>");
        xml.AppendLine($"                    <DisableUserPromptForServerValidation>{(credential.DisableUserPromptForServerValidation ? "true" : "false")}</DisableUserPromptForServerValidation>");

        if (credential.ServerNames.Count > 0)
        {
            var names = string.Join(";", credential.ServerNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => Escape(n.Trim())));
            xml.AppendLine($"                    <ServerNames>{names}</ServerNames>");
        }
        else
        {
            xml.AppendLine("                    <ServerNames></ServerNames>");
        }

        foreach (var thumbprint in credential.TrustedRootCaThumbprints.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            xml.AppendLine($"                    <TrustedRootCA>{Escape(NormaliseThumbprint(thumbprint))}</TrustedRootCA>");
        }

        xml.AppendLine("                  </ServerValidation>");
        xml.AppendLine("                  <CredentialsSource>");
        xml.AppendLine("                    <CertificateStore>");
        xml.AppendLine("                      <SimpleCertSelection>true</SimpleCertSelection>");
        xml.AppendLine("                    </CertificateStore>");
        xml.AppendLine("                  </CredentialsSource>");
        xml.AppendLine("                </EapType>");
        xml.AppendLine("              </Eap>");
    }

    private static int EapMethodType(WifiEapMethod method) => method switch
    {
        WifiEapMethod.PeapMschapv2 => EapTypePeap,
        WifiEapMethod.Tls => EapTypeTls,
        _ => EapTypePeap,
    };

    private static string AuthName(WifiEnterpriseAuth auth) => auth switch
    {
        WifiEnterpriseAuth.Wpa3Enterprise => "WPA3",
        WifiEnterpriseAuth.WpaEnterprise => "WPA",
        _ => "WPA2",
    };

    private static string EncryptionName(WifiEnterpriseAuth auth) => auth switch
    {
        WifiEnterpriseAuth.Wpa3Enterprise => "AES",
        WifiEnterpriseAuth.WpaEnterprise => "TKIP",
        _ => "AES",
    };

    /// <summary>Removes separators from a certificate thumbprint; Windows expects 40 plain hex digits.</summary>
    private static string NormaliseThumbprint(string thumbprint) =>
        new(thumbprint.Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>SSID as continuous upper case hex, which is how the WLAN schema carries non-ASCII SSIDs.</summary>
    public static string ToHex(string ssid)
    {
        var bytes = Encoding.UTF8.GetBytes(ssid);
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("X2"));
        }

        return builder.ToString();
    }

    /// <summary>XML text escaping; every value from the library passes through here.</summary>
    internal static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);
}
