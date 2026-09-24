using System.Text;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Wlan;

/// <summary>Outcome of a profile or EAP user data generation attempt.</summary>
public readonly record struct EnterpriseProfileBuildResult(
    bool Success,
    string? Xml,
    IReadOnlyList<string> Problems)
{
    public string? Failure => Problems.Count == 0 ? null : string.Join("；", Problems);

    public static EnterpriseProfileBuildResult Fail(params string[] problems) => new(false, null, problems);
}

/// <summary>
/// Generates the two documents an 802.1X/EAP network needs: the WLAN connection profile and the EAP
/// user credentials that carry the account.
/// </summary>
/// <remarks>
/// The structure is not invented - it mirrors a profile the WLAN service itself produced on a real
/// campus network (dumped with <c>tools/dump-wlan-profile.ps1</c>). Two facts about the Windows schemas
/// drive the split:
/// <list type="bullet">
/// <item>Credentials do <b>not</b> belong in the connection profile: a <c>&lt;UserName&gt;</c> inside
/// <c>MsChapV2ConnectionPropertiesV1</c> makes <c>WlanSetProfile</c> fail with reason code 524289
/// ("the profile is invalid according to the schema"). The connection profile only declares
/// <c>UseWinLogonCredentials</c>.</item>
/// <item>The account lives in a separate EAP user data document
/// (<c>EapHostUserCredentials</c>) written with <c>WlanSetProfileEapXmlUserData</c>; the WLAN service
/// encrypts it when it stores it.</item>
/// </list>
/// Only methods that ship in the box are generated: PEAP + inner MSCHAPv2 (type 25/26, account and
/// password) and EAP-TLS (type 13, certificate selection). Anything else has to be supplied verbatim
/// through <see cref="WifiNetworkCredential.ProfileXmlOverride"/>.
/// </remarks>
public static class EnterpriseProfileBuilder
{
    public const string WlanProfileNamespace = "http://www.microsoft.com/networking/WLAN/profile/v1";
    public const string MacRandomizationNamespace = "http://www.microsoft.com/networking/WLAN/profile/v3";

    private const string OneXNamespace = "http://www.microsoft.com/networking/OneX/v1";
    private const string EapHostConfigNamespace = "http://www.microsoft.com/provisioning/EapHostConfig";
    private const string EapHostUserCredentialsNamespace = "http://www.microsoft.com/provisioning/EapHostUserCredentials";
    private const string EapCommonNamespace = "http://www.microsoft.com/provisioning/EapCommon";
    private const string BaseEapConnectionNamespace = "http://www.microsoft.com/provisioning/BaseEapConnectionPropertiesV1";
    private const string BaseEapUserPropertiesNamespace = "http://www.microsoft.com/provisioning/BaseEapUserPropertiesV1";
    private const string MsPeapNamespace = "http://www.microsoft.com/provisioning/MsPeapConnectionPropertiesV1";
    private const string MsPeapV2Namespace = "http://www.microsoft.com/provisioning/MsPeapConnectionPropertiesV2";
    private const string MsPeapV3Namespace = "http://www.microsoft.com/provisioning/MsPeapConnectionPropertiesV3";
    private const string MsPeapUserPropertiesNamespace = "http://www.microsoft.com/provisioning/MsPeapUserPropertiesV1";
    private const string MsChapV2Namespace = "http://www.microsoft.com/provisioning/MsChapV2ConnectionPropertiesV1";
    private const string MsChapV2UserPropertiesNamespace = "http://www.microsoft.com/provisioning/MsChapV2UserPropertiesV1";
    private const string EapTlsNamespace = "http://www.microsoft.com/provisioning/EapTlsConnectionPropertiesV1";
    private const string EapTlsUserPropertiesNamespace = "http://www.microsoft.com/provisioning/EapTlsUserPropertiesV1";

    private const int EapTypeTls = 13;
    private const int EapTypePeap = 25;
    private const int EapTypeMsChapV2 = 26;

    /// <summary>
    /// Builds the WLAN connection profile. It never contains a password; the account is applied
    /// separately with <see cref="BuildEapUserDataXml"/>.
    /// </summary>
    public static EnterpriseProfileBuildResult BuildProfileXml(WifiNetworkCredential credential)
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

        if (!string.IsNullOrWhiteSpace(credential.ProfileXmlOverride))
        {
            if (!credential.ProfileXmlOverride.Contains("<WLANProfile", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("自定义配置 XML 中未找到 <WLANProfile> 根元素");
            }

            return problems.Count > 0
                ? EnterpriseProfileBuildResult.Fail(problems.ToArray())
                : new EnterpriseProfileBuildResult(true, credential.ProfileXmlOverride.Trim(), problems);
        }

        if (!credential.IsEnterprise)
        {
            return BuildPersonalOrOpenProfile(credential, problems);
        }

        if (credential.Eap == WifiEapMethod.CustomXml)
        {
            problems.Add("EAP 方法为“自定义 XML”时必须提供完整的配置 XML（EAP-TTLS、PEAP-TLS、" +
                         "证书选择等内置方法未覆盖的场景）");
            return EnterpriseProfileBuildResult.Fail(problems.ToArray());
        }

        // Measured on three different adapters (AIC8800 USB, MediaTek USB, Intel AX201): WPA2-Enterprise
        // with AES/TKIP and WPA-Enterprise with TKIP/AES are accepted by the WLAN service, while every
        // WPA3-Enterprise spelling (WPA3, WPA3ENT, WPA3ENT192 with AES/GCMP/GCMP256, with and without the
        // PMK cache block) is refused with reason code 1206. Generating a document the service rejects
        // would only produce a profile that cannot work, so the operator supplies the campus document.
        if (credential.Auth == WifiEnterpriseAuth.Wpa3Enterprise)
        {
            problems.Add("WPA3-Enterprise 的 Windows 配置在本机参考环境中被 WLAN 服务拒绝（原因码 1206，" +
                         "三种网卡均如此）；请改用 wpa2Enterprise，或把学校/厂商提供的完整配置 XML 填入" +
                         "“自定义配置 XML”");
            return EnterpriseProfileBuildResult.Fail(problems.ToArray());
        }

        if (problems.Count > 0)
        {
            return EnterpriseProfileBuildResult.Fail(problems.ToArray());
        }

        var xml = new StringBuilder(4096);
        var authentication = AuthName(credential.Auth);
        var encryption = EncryptionName(credential.Auth);

        xml.Append("<?xml version=\"1.0\"?>\n");
        xml.Append($"<WLANProfile xmlns=\"{WlanProfileNamespace}\">\n");
        xml.Append($"\t<name>{Escape(credential.EffectiveProfileName)}</name>\n");
        xml.Append("\t<SSIDConfig>\n");
        xml.Append("\t\t<SSID>\n");
        xml.Append($"\t\t\t<hex>{ToHex(credential.Ssid)}</hex>\n");
        xml.Append($"\t\t\t<name>{Escape(credential.Ssid)}</name>\n");
        xml.Append("\t\t</SSID>\n");

        if (credential.Hidden)
        {
            xml.Append("\t\t<nonBroadcast>true</nonBroadcast>\n");
        }

        xml.Append("\t</SSIDConfig>\n");
        xml.Append("\t<connectionType>ESS</connectionType>\n");
        xml.Append($"\t<connectionMode>{(credential.ConnectAutomatically ? "auto" : "manual")}</connectionMode>\n");
        xml.Append("\t<MSM>\n");
        xml.Append("\t\t<security>\n");
        xml.Append("\t\t\t<authEncryption>\n");
        xml.Append($"\t\t\t\t<authentication>{authentication}</authentication>\n");
        xml.Append($"\t\t\t\t<encryption>{encryption}</encryption>\n");
        xml.Append("\t\t\t\t<useOneX>true</useOneX>\n");
        xml.Append("\t\t\t</authEncryption>\n");
        xml.Append("\t\t\t<PMKCacheMode>enabled</PMKCacheMode>\n");
        xml.Append("\t\t\t<PMKCacheTTL>720</PMKCacheTTL>\n");
        xml.Append("\t\t\t<PMKCacheSize>128</PMKCacheSize>\n");
        xml.Append("\t\t\t<preAuthMode>disabled</preAuthMode>\n");
        xml.Append($"\t\t\t<OneX xmlns=\"{OneXNamespace}\">\n");
        xml.Append("\t\t\t\t<authMode>user</authMode>\n");
        xml.Append("\t\t\t\t<EAPConfig>");
        xml.Append($"<EapHostConfig xmlns=\"{EapHostConfigNamespace}\">");
        xml.Append(BuildEapMethod(credential.Eap));
        xml.Append($"<Config xmlns=\"{EapHostConfigNamespace}\">");
        xml.Append(credential.Eap == WifiEapMethod.PeapMschapv2
            ? BuildPeapConnectionConfig(credential)
            : BuildTlsConnectionConfig(credential));
        xml.Append("</Config>");
        xml.Append("</EapHostConfig>");
        xml.Append("</EAPConfig>\n");
        xml.Append("\t\t\t</OneX>\n");
        xml.Append("\t\t</security>\n");
        xml.Append("\t</MSM>\n");
        xml.Append($"\t<MacRandomization xmlns=\"{MacRandomizationNamespace}\">\n");
        xml.Append("\t\t<enableRandomization>false</enableRandomization>\n");
        xml.Append("\t</MacRandomization>\n");
        xml.Append("</WLANProfile>");

        return new EnterpriseProfileBuildResult(true, xml.ToString(), problems);
    }

    private static EnterpriseProfileBuildResult BuildPersonalOrOpenProfile(
        WifiNetworkCredential credential,
        List<string> problems)
    {
        if (credential.Kind == WifiCredentialKind.Personal && string.IsNullOrEmpty(credential.Password))
        {
            problems.Add("个人网络必须填写 Wi-Fi 密码");
        }

        var (authentication, encryption) = credential.Kind == WifiCredentialKind.Open
            ? ("open", "none")
            : credential.Security switch
            {
                WifiSecurity.WpaPersonal => ("WPAPSK", "TKIP"),
                WifiSecurity.Wpa3Personal => ("WPA3SAE", "AES"),
                _ => ("WPA2PSK", "AES"),
            };

        if (problems.Count > 0)
        {
            return EnterpriseProfileBuildResult.Fail(problems.ToArray());
        }

        var xml = new StringBuilder(2048);
        xml.Append("<?xml version=\"1.0\"?>\n");
        xml.Append($"<WLANProfile xmlns=\"{WlanProfileNamespace}\">\n");
        xml.Append($"\t<name>{Escape(credential.EffectiveProfileName)}</name>\n");
        xml.Append("\t<SSIDConfig><SSID>");
        xml.Append($"<hex>{ToHex(credential.Ssid)}</hex><name>{Escape(credential.Ssid)}</name>");
        xml.Append("</SSID>");
        if (credential.Hidden) xml.Append("<nonBroadcast>true</nonBroadcast>");
        xml.Append("</SSIDConfig>\n");
        xml.Append("\t<connectionType>ESS</connectionType>\n");
        xml.Append($"\t<connectionMode>{(credential.ConnectAutomatically ? "auto" : "manual")}</connectionMode>\n");
        xml.Append("\t<MSM><security><authEncryption>");
        xml.Append($"<authentication>{authentication}</authentication><encryption>{encryption}</encryption><useOneX>false</useOneX>");
        xml.Append("</authEncryption>");
        if (credential.Kind == WifiCredentialKind.Personal)
        {
            xml.Append("<sharedKey><keyType>passPhrase</keyType><protected>false</protected>");
            xml.Append($"<keyMaterial>{Escape(credential.Password!)}</keyMaterial></sharedKey>");
        }
        xml.Append("</security></MSM>\n</WLANProfile>");
        return new EnterpriseProfileBuildResult(true, xml.ToString(), problems);
    }

    /// <summary>
    /// Builds the EAP user credentials document for PEAP-MSCHAPv2 (account, password, optional domain).
    /// Returns a result with <c>Success=false</c> and a reason when the entry does not carry credentials
    /// of its own (EAP-TLS uses a certificate, Windows-logon credentials come from the session).
    /// </summary>
    public static EnterpriseProfileBuildResult BuildEapUserDataXml(WifiNetworkCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (credential.Eap == WifiEapMethod.CustomXml)
        {
            return EnterpriseProfileBuildResult.Fail("自定义 XML 的网络没有可生成的 EAP 用户数据");
        }

        if (credential.Eap == WifiEapMethod.Tls)
        {
            return BuildTlsUserDataXml(credential);
        }

        if (credential.UseWinLogonCredentials)
        {
            return EnterpriseProfileBuildResult.Fail("已选择使用 Windows 登录凭据，无需写入账号密码");
        }

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(credential.Identity))
        {
            problems.Add("缺少账号（身份）");
        }

        if (string.IsNullOrEmpty(credential.Password))
        {
            problems.Add("缺少密码");
        }

        if (problems.Count > 0)
        {
            return EnterpriseProfileBuildResult.Fail(problems.ToArray());
        }

        var xml = new StringBuilder(1024);
        xml.Append($"<EapHostUserCredentials xmlns=\"{EapHostUserCredentialsNamespace}\">");

        // 1. EapMethod is a child of the root document (not of EapHostConfig), and its children live in
        //    the EapCommon namespace.
        xml.Append("<EapMethod>");
        xml.Append($"<Type xmlns=\"{EapCommonNamespace}\">{EapTypePeap}</Type>");
        xml.Append($"<VendorId xmlns=\"{EapCommonNamespace}\">0</VendorId>");
        xml.Append($"<VendorType xmlns=\"{EapCommonNamespace}\">0</VendorType>");
        xml.Append($"<AuthorId xmlns=\"{EapCommonNamespace}\">0</AuthorId>");
        xml.Append("</EapMethod>");

        // 2. Credentials use the EAP *user properties* namespaces, not the connection ones the profile
        //    document uses: MSCHAPv2 spells the account "Username" and adds "LogonDomain", while the
        //    outer PEAP identity is "RoutingIdentity".
        xml.Append("<Credentials>");
        xml.Append($"<Eap xmlns=\"{BaseEapUserPropertiesNamespace}\">");
        xml.Append($"<Type>{EapTypePeap}</Type>");
        xml.Append($"<EapType xmlns=\"{MsPeapUserPropertiesNamespace}\">");

        if (!string.IsNullOrWhiteSpace(credential.AnonymousIdentity))
        {
            xml.Append($"<RoutingIdentity>{Escape(credential.AnonymousIdentity!.Trim())}</RoutingIdentity>");
        }

        xml.Append($"<Eap xmlns=\"{BaseEapUserPropertiesNamespace}\">");
        xml.Append($"<Type>{EapTypeMsChapV2}</Type>");
        xml.Append($"<EapType xmlns=\"{MsChapV2UserPropertiesNamespace}\">");
        xml.Append($"<Username>{Escape(credential.Identity.Trim())}</Username>");
        xml.Append($"<Password>{Escape(credential.Password!)}</Password>");

        if (!string.IsNullOrWhiteSpace(credential.Domain))
        {
            xml.Append($"<LogonDomain>{Escape(credential.Domain!.Trim())}</LogonDomain>");
        }

        xml.Append("</EapType>");
        xml.Append("</Eap>");
        xml.Append("</EapType>");
        xml.Append("</Eap>");
        xml.Append("</Credentials>");
        xml.Append("</EapHostUserCredentials>");

        return new EnterpriseProfileBuildResult(true, xml.ToString(), problems);
    }

    /// <summary>
    /// EAP-TLS credentials document: pins the client certificate by its hash
    /// (<c>EapTlsUserPropertiesV1</c> / <c>UserCert</c>, an <c>xs:hexBinary</c>).
    /// </summary>
    private static EnterpriseProfileBuildResult BuildTlsUserDataXml(WifiNetworkCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.CertificateThumbprint))
        {
            // Nothing to pin: the profile's SimpleCertSelection lets Windows pick the certificate.
            return EnterpriseProfileBuildResult.Fail(
                "EAP-TLS 未指定客户端证书指纹，由 Windows 自动选择证书即可，无需写入 EAP 用户数据");
        }

        var xml = new StringBuilder(512);
        xml.Append($"<EapHostUserCredentials xmlns=\"{EapHostUserCredentialsNamespace}\">");
        xml.Append("<EapMethod>");
        xml.Append($"<Type xmlns=\"{EapCommonNamespace}\">{EapTypeTls}</Type>");
        xml.Append($"<VendorId xmlns=\"{EapCommonNamespace}\">0</VendorId>");
        xml.Append($"<VendorType xmlns=\"{EapCommonNamespace}\">0</VendorType>");
        xml.Append($"<AuthorId xmlns=\"{EapCommonNamespace}\">0</AuthorId>");
        xml.Append("</EapMethod>");
        xml.Append("<Credentials>");
        xml.Append($"<Eap xmlns=\"{BaseEapUserPropertiesNamespace}\">");
        xml.Append($"<Type>{EapTypeTls}</Type>");
        xml.Append($"<EapType xmlns=\"{EapTlsUserPropertiesNamespace}\">");
        xml.Append($"<UserCert>{FormatThumbprint(credential.CertificateThumbprint)}</UserCert>");
        xml.Append("</EapType>");
        xml.Append("</Eap>");
        xml.Append("</Credentials>");
        xml.Append("</EapHostUserCredentials>");

        return new EnterpriseProfileBuildResult(true, xml.ToString(), new List<string>());
    }

    private static string BuildEapMethod(WifiEapMethod method)
    {
        var type = method switch
        {
            WifiEapMethod.Tls => EapTypeTls,
            _ => EapTypePeap,
        };

        return "<EapMethod>" +
               $"<Type xmlns=\"{EapCommonNamespace}\">{type}</Type>" +
               $"<VendorId xmlns=\"{EapCommonNamespace}\">0</VendorId>" +
               $"<VendorType xmlns=\"{EapCommonNamespace}\">0</VendorType>" +
               $"<AuthorId xmlns=\"{EapCommonNamespace}\">0</AuthorId>" +
               "</EapMethod>";
    }

    /// <summary>PEAP (25) wrapping MSCHAPv2 (26); no credentials here, by schema.</summary>
    private static string BuildPeapConnectionConfig(WifiNetworkCredential credential)
    {
        var xml = new StringBuilder(1024);
        var validateServer = credential.TrustedRootCaThumbprints.Any(t => !string.IsNullOrWhiteSpace(t)) ||
                             credential.ServerNames.Any(n => !string.IsNullOrWhiteSpace(n));

        xml.Append($"<Eap xmlns=\"{BaseEapConnectionNamespace}\">");
        xml.Append($"<Type>{EapTypePeap}</Type>");
        xml.Append($"<EapType xmlns=\"{MsPeapNamespace}\">");
        xml.Append("<ServerValidation>");
        xml.Append($"<DisableUserPromptForServerValidation>{(credential.DisableUserPromptForServerValidation ? "true" : "false")}</DisableUserPromptForServerValidation>");
        xml.Append($"<ServerNames>{ServerNameList(credential)}</ServerNames>");
        foreach (var thumbprint in credential.TrustedRootCaThumbprints.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            xml.Append($"<TrustedRootCA>{FormatThumbprint(thumbprint)}</TrustedRootCA>");
        }

        xml.Append("</ServerValidation>");
        xml.Append("<FastReconnect>true</FastReconnect>");
        xml.Append("<InnerEapOptional>false</InnerEapOptional>");
        xml.Append($"<Eap xmlns=\"{BaseEapConnectionNamespace}\">");
        xml.Append($"<Type>{EapTypeMsChapV2}</Type>");
        xml.Append($"<EapType xmlns=\"{MsChapV2Namespace}\">");
        xml.Append($"<UseWinLogonCredentials>{(credential.UseWinLogonCredentials ? "true" : "false")}</UseWinLogonCredentials>");
        xml.Append("</EapType>");
        xml.Append("</Eap>");
        xml.Append("<EnableQuarantineChecks>false</EnableQuarantineChecks>");
        xml.Append("<RequireCryptoBinding>false</RequireCryptoBinding>");
        xml.Append("<PeapExtensions>");
        xml.Append($"<PerformServerValidation xmlns=\"{MsPeapV2Namespace}\">{(validateServer ? "true" : "false")}</PerformServerValidation>");

        // With no server name configured, an accepted CA is enough: AcceptServerName=true is exactly
        // what Windows stores for such a profile.
        var acceptAnyName = !credential.ServerNames.Any(n => !string.IsNullOrWhiteSpace(n));
        xml.Append($"<AcceptServerName xmlns=\"{MsPeapV2Namespace}\">{Lower(acceptAnyName)}</AcceptServerName>");
        xml.Append($"<PeapExtensionsV2 xmlns=\"{MsPeapV2Namespace}\">");
        xml.Append($"<AllowPromptingWhenServerCANotFound xmlns=\"{MsPeapV3Namespace}\">true</AllowPromptingWhenServerCANotFound>");
        xml.Append("</PeapExtensionsV2>");
        xml.Append("</PeapExtensions>");
        xml.Append("</EapType>");
        xml.Append("</Eap>");

        return xml.ToString();
    }

    /// <summary>EAP-TLS (13): certificate selection, no password and no user data document.</summary>
    private static string BuildTlsConnectionConfig(WifiNetworkCredential credential)
    {
        var xml = new StringBuilder(512);

        xml.Append($"<Eap xmlns=\"{BaseEapConnectionNamespace}\">");
        xml.Append($"<Type>{EapTypeTls}</Type>");
        xml.Append($"<EapType xmlns=\"{EapTlsNamespace}\">");

        // Element order is enforced by %SystemRoot%\schemas\EAPMethods\eaptlsconnectionpropertiesv1.xsd:
        // CredentialsSource must come before ServerValidation, otherwise WlanSetProfile answers 1206.
        xml.Append("<CredentialsSource>");
        xml.Append("<CertificateStore>");
        xml.Append($"<SimpleCertSelection>{(string.IsNullOrWhiteSpace(credential.CertificateThumbprint) ? "true" : "false")}</SimpleCertSelection>");
        xml.Append("</CertificateStore>");
        xml.Append("</CredentialsSource>");

        xml.Append("<ServerValidation>");
        xml.Append($"<DisableUserPromptForServerValidation>{(credential.DisableUserPromptForServerValidation ? "true" : "false")}</DisableUserPromptForServerValidation>");
        xml.Append($"<ServerNames>{ServerNameList(credential)}</ServerNames>");

        foreach (var thumbprint in credential.TrustedRootCaThumbprints.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            // xs:hexBinary allows the space separated form Windows itself stores.
            xml.Append($"<TrustedRootCA>{FormatThumbprint(thumbprint)}</TrustedRootCA>");
        }

        xml.Append("</ServerValidation>");
        xml.Append("</EapType>");
        xml.Append("</Eap>");

        return xml.ToString();
    }

    private static string ServerNameList(WifiNetworkCredential credential) =>
        string.Join(";", credential.ServerNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => Escape(n.Trim())));

    private static string AuthName(WifiEnterpriseAuth auth) => auth switch
    {
        WifiEnterpriseAuth.Wpa3Enterprise => "WPA3",
        WifiEnterpriseAuth.WpaEnterprise => "WPA",
        _ => "WPA2",
    };

    private static string EncryptionName(WifiEnterpriseAuth auth) => auth switch
    {
        WifiEnterpriseAuth.WpaEnterprise => "TKIP",
        _ => "AES",
    };

    private static string Lower(bool value) => value ? "true" : "false";

    /// <summary>
    /// Formats a certificate thumbprint the way the WLAN service stores it: space separated hex pairs
    /// with a trailing space (<c>b2 f9 55 ... a2 </c>).
    /// </summary>
    public static string FormatThumbprint(string thumbprint)
    {
        var hex = NormaliseHex(thumbprint);
        var builder = new StringBuilder(hex.Length + (hex.Length / 2) + 1);

        for (var i = 0; i < hex.Length; i += 2)
        {
            builder.Append(hex, i, Math.Min(2, hex.Length - i)).Append(' ');
        }

        return builder.ToString();
    }

    /// <summary>Uppercase hex without separators.</summary>
    internal static string NormaliseHex(string value) =>
        new(value.Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant).ToArray());

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
