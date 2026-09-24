using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Serialization;
using NetworkGuardian.Core.Wlan;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// The self-maintained wireless network library: profile XML generation for 802.1X/EAP networks, the
/// EAP user credential document, the profile comparison rules, and the bounded retry that gives a
/// rejected network up for the run.
/// </summary>
public sealed class EapCredentialLibraryTests
{
    [Fact]
    public void BuildPersonalProfile_CarriesProtectedAtRestPasswordIntoWindowsProfile()
    {
        var credential = new WifiNetworkCredential
        {
            Ssid = "HomeWiFi",
            Kind = WifiCredentialKind.Personal,
            Security = WifiSecurity.Wpa2Personal,
            Password = "wifi<&password",
        };

        var result = EnterpriseProfileBuilder.BuildProfileXml(credential);

        Assert.True(result.Success, result.Failure);
        Assert.Contains("<authentication>WPA2PSK</authentication>", result.Xml);
        Assert.Contains("<encryption>AES</encryption>", result.Xml);
        Assert.Contains("<useOneX>false</useOneX>", result.Xml);
        Assert.Contains("<keyMaterial>wifi&lt;&amp;password</keyMaterial>", result.Xml);
    }

    [Fact]
    public void BuildOpenProfile_HasNoSharedKey()
    {
        var result = EnterpriseProfileBuilder.BuildProfileXml(new WifiNetworkCredential
        {
            Ssid = "Guest",
            Kind = WifiCredentialKind.Open,
            Security = WifiSecurity.Open,
        });

        Assert.True(result.Success, result.Failure);
        Assert.Contains("<authentication>open</authentication>", result.Xml);
        Assert.Contains("<encryption>none</encryption>", result.Xml);
        Assert.DoesNotContain("sharedKey", result.Xml);
    }

    [Fact]
    public void BuildEnhancedOpenProfile_UsesOweWithoutSharedKey()
    {
        var result = EnterpriseProfileBuilder.BuildProfileXml(new WifiNetworkCredential
        {
            Ssid = "EncryptedGuest",
            Kind = WifiCredentialKind.Open,
            Security = WifiSecurity.EnhancedOpen,
        });

        Assert.True(result.Success, result.Failure);
        Assert.Contains("<authentication>OWE</authentication>", result.Xml);
        Assert.Contains("<encryption>AES</encryption>", result.Xml);
        Assert.DoesNotContain("sharedKey", result.Xml);
    }

    [Fact]
    public void BuildWepProfile_UsesNetworkKeyAndKeyIndex()
    {
        var result = EnterpriseProfileBuilder.BuildProfileXml(new WifiNetworkCredential
        {
            Ssid = "Legacy",
            Kind = WifiCredentialKind.Personal,
            Security = WifiSecurity.Wep,
            Password = "A1B2C3D4E5",
        });

        Assert.True(result.Success, result.Failure);
        Assert.Contains("<authentication>open</authentication>", result.Xml);
        Assert.Contains("<encryption>WEP</encryption>", result.Xml);
        Assert.Contains("<keyType>networkKey</keyType>", result.Xml);
        Assert.Contains("<keyIndex>0</keyIndex>", result.Xml);
    }

    [Fact]
    public void BuildUnknownPersonalProfile_FailsInsteadOfSilentlyUsingWpa2()
    {
        var result = EnterpriseProfileBuilder.BuildProfileXml(new WifiNetworkCredential
        {
            Ssid = "Mystery",
            Kind = WifiCredentialKind.Personal,
            Security = WifiSecurity.Unknown,
            Password = "password123",
        });

        Assert.False(result.Success);
        Assert.Contains("暂不支持安全类型", result.Failure);
    }

    private static WifiNetworkCredential Credential() => new()
    {
        Ssid = "HXXY-WiFi",
        Auth = WifiEnterpriseAuth.Wpa2Enterprise,
        Eap = WifiEapMethod.PeapMschapv2,
        Identity = "20230001@campus.example.edu",
        Password = "p@ss&word<>",
        Domain = "campus",
        ServerNames = { "radius.campus.example.edu" },
        TrustedRootCaThumbprints = { "0a1b 2c3d 4e5f 6071 8293 a4b5 c6d7 e8f9 0a1b 2c3d" },
    };

    [Fact]
    public void BuildProfile_PeapMschapV2_ProducesWindowsEnterpriseProfileWithoutCredentials()
    {
        var result = EnterpriseProfileBuilder.BuildProfileXml(Credential());

        Assert.True(result.Success, result.Failure);
        var xml = result.Xml!;

        Assert.Contains("<?xml version=\"1.0\"?>", xml);
        Assert.Contains("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">", xml);
        Assert.Contains("<name>HXXY-WiFi</name>", xml);
        Assert.Contains($"<hex>{EnterpriseProfileBuilder.ToHex("HXXY-WiFi")}</hex>", xml);
        Assert.Contains("<connectionMode>auto</connectionMode>", xml);
        Assert.Contains("<authentication>WPA2</authentication>", xml);
        Assert.Contains("<encryption>AES</encryption>", xml);
        Assert.Contains("<useOneX>true</useOneX>", xml);
        Assert.Contains("<OneX xmlns=\"http://www.microsoft.com/networking/OneX/v1\">", xml);
        Assert.Contains("<authMode>user</authMode>", xml);
        Assert.Contains("<PMKCacheMode>enabled</PMKCacheMode>", xml);

        // PEAP (25) wrapping MSCHAPv2 (26) plus the server trust configuration.
        Assert.Contains("<Type xmlns=\"http://www.microsoft.com/provisioning/EapCommon\">25</Type>", xml);
        Assert.Contains("<Type>26</Type>", xml);
        Assert.Contains("<ServerNames>radius.campus.example.edu</ServerNames>", xml);
        Assert.Contains("<TrustedRootCA>0A 1B 2C 3D 4E 5F 60 71 82 93 A4 B5 C6 D7 E8 F9 0A 1B 2C 3D </TrustedRootCA>", xml);
        Assert.Contains("<UseWinLogonCredentials>false</UseWinLogonCredentials>", xml);
        Assert.Contains("<enableRandomization>false</enableRandomization>", xml);

        // Credentials must not be part of the profile: the WLAN service rejects that with reason 524289.
        Assert.DoesNotContain("<UserName>", xml);
        Assert.DoesNotContain("<Password>", xml);
        Assert.DoesNotContain("p@ss&word", xml);
    }

    [Fact]
    public void BuildEapUserData_CarriesTheAccountAndPassword()
    {
        var result = EnterpriseProfileBuilder.BuildEapUserDataXml(Credential());

        Assert.True(result.Success, result.Failure);
        var xml = result.Xml!;

        // The document shape is fixed by %SystemRoot%\schemas\EAPHost\eaphostusercredentials.xsd.
        Assert.Contains("<EapHostUserCredentials xmlns=\"http://www.microsoft.com/provisioning/EapHostUserCredentials\">", xml);
        Assert.Contains("<Credentials>", xml);
        Assert.Contains("<Type xmlns=\"http://www.microsoft.com/provisioning/EapCommon\">25</Type>", xml);

        // The credentials use the *UserProperties* schemas, where the account element is "Username".
        Assert.Contains("<Eap xmlns=\"http://www.microsoft.com/provisioning/BaseEapUserPropertiesV1\">", xml);
        Assert.Contains("<EapType xmlns=\"http://www.microsoft.com/provisioning/MsPeapUserPropertiesV1\">", xml);
        Assert.Contains("<EapType xmlns=\"http://www.microsoft.com/provisioning/MsChapV2UserPropertiesV1\">", xml);
        Assert.Contains("<Username>20230001@campus.example.edu</Username>", xml);
        Assert.Contains("<Password>p@ss&amp;word&lt;&gt;</Password>", xml);
        Assert.Contains("<LogonDomain>campus</LogonDomain>", xml);

        // The password is escaped, never written raw, and the connection namespaces stay out.
        Assert.DoesNotContain("p@ss&word", xml);
        Assert.DoesNotContain("ConnectionProperties", xml);
    }

    [Fact]
    public void BuildEapUserData_CarriesTheAnonymousOuterIdentity()
    {
        var credential = Credential();
        credential.AnonymousIdentity = "anonymous@campus.example.edu";

        var xml = EnterpriseProfileBuilder.BuildEapUserDataXml(credential).Xml!;

        Assert.Contains("<RoutingIdentity>anonymous@campus.example.edu</RoutingIdentity>", xml);
    }

    [Fact]
    public void BuildProfile_EscapesValuesThatWouldBreakTheXml()
    {
        var credential = Credential();
        credential.Ssid = "Campus & Co <lab>";

        var xml = EnterpriseProfileBuilder.BuildProfileXml(credential).Xml!;

        Assert.Contains("<name>Campus &amp; Co &lt;lab&gt;</name>", xml);
    }

    [Fact]
    public void BuildProfile_NonAsciiSsid_UsesUtf8Hex()
    {
        var credential = Credential();
        credential.Ssid = "校园网";

        var xml = EnterpriseProfileBuilder.BuildProfileXml(credential).Xml!;

        Assert.Contains("<name>校园网</name>", xml);
        Assert.Contains("<hex>E6A0A1E59BADE7BD91</hex>", xml);
        Assert.Equal("E6A0A1E59BADE7BD91", EnterpriseProfileBuilder.ToHex("校园网"));
    }

    [Fact]
    public void BuildProfile_ManualConnectionMode_WhenAutomaticIsOff()
    {
        var credential = Credential();
        credential.ConnectAutomatically = false;

        var xml = EnterpriseProfileBuilder.BuildProfileXml(credential).Xml!;

        Assert.Contains("<connectionMode>manual</connectionMode>", xml);
    }

    [Fact]
    public void BuildProfile_HiddenNetwork_MarksNonBroadcast()
    {
        var credential = Credential();
        credential.Hidden = true;

        var xml = EnterpriseProfileBuilder.BuildProfileXml(credential).Xml!;

        Assert.Contains("<nonBroadcast>true</nonBroadcast>", xml);
    }

    [Theory]
    [InlineData(WifiEnterpriseAuth.Wpa2Enterprise, "WPA2", "AES")]
    [InlineData(WifiEnterpriseAuth.WpaEnterprise, "WPA", "TKIP")]
    public void BuildProfile_MapsTheVerifiedAuthenticationPairs(WifiEnterpriseAuth auth, string expectedAuth, string expectedCipher)
    {
        // These are the pairs the WLAN service accepted on the reference machine (three different NICs).
        var credential = Credential();
        credential.Auth = auth;

        var xml = EnterpriseProfileBuilder.BuildProfileXml(credential).Xml!;

        Assert.Contains($"<authentication>{expectedAuth}</authentication>", xml);
        Assert.Contains($"<encryption>{expectedCipher}</encryption>", xml);
    }

    [Fact]
    public void BuildProfile_Wpa3Enterprise_IsRefusedInsteadOfEmittingARejectedDocument()
    {
        // Every WPA3-Enterprise spelling was rejected by the WLAN service (reason code 1206) on all three
        // adapters of the reference machine, so the builder refuses rather than writing a profile that
        // cannot work; the operator uses wpa2Enterprise or the raw XML override.
        var credential = Credential();
        credential.Auth = WifiEnterpriseAuth.Wpa3Enterprise;

        var result = EnterpriseProfileBuilder.BuildProfileXml(credential);

        Assert.False(result.Success);
        Assert.Null(result.Xml);
        Assert.Contains("WPA3-Enterprise", result.Failure);

        // With a document from the campus (or a WPA3-capable driver) the entry still works.
        credential.Eap = WifiEapMethod.CustomXml;
        credential.ProfileXmlOverride = "<WLANProfile xmlns=\"x\"><name>campus</name></WLANProfile>";
        Assert.True(EnterpriseProfileBuilder.BuildProfileXml(credential).Success);
    }

    [Fact]
    public void BuildProfile_Tls_UsesCertificateStoreAndNoPassword()
    {
        var credential = Credential();
        credential.Eap = WifiEapMethod.Tls;
        credential.Password = null;

        var result = EnterpriseProfileBuilder.BuildProfileXml(credential);

        Assert.True(result.Success, result.Failure);
        Assert.Contains("<Type xmlns=\"http://www.microsoft.com/provisioning/EapCommon\">13</Type>", result.Xml);
        Assert.Contains("<SimpleCertSelection>true</SimpleCertSelection>", result.Xml);
        Assert.DoesNotContain("<Password>", result.Xml);

        // The eaptlsconnectionpropertiesv1 schema requires CredentialsSource before ServerValidation.
        Assert.True(result.Xml!.IndexOf("<CredentialsSource>", StringComparison.Ordinal) <
                    result.Xml.IndexOf("<ServerValidation>", StringComparison.Ordinal));

        // EAP-TLS authenticates with a certificate: there is no user data document.
        Assert.False(EnterpriseProfileBuilder.BuildEapUserDataXml(credential).Success);
    }

    [Fact]
    public void BuildProfile_TlsWithPinnedCertificate_UsesTheUserDataDocument()
    {
        var credential = Credential();
        credential.Eap = WifiEapMethod.Tls;
        credential.CertificateThumbprint = "0a1b2c3d";

        var xml = EnterpriseProfileBuilder.BuildProfileXml(credential).Xml!;

        Assert.Contains("<SimpleCertSelection>false</SimpleCertSelection>", xml);

        // The certificate itself is pinned through the EAP user data (EapTlsUserPropertiesV1 / UserCert).
        var userData = EnterpriseProfileBuilder.BuildEapUserDataXml(credential);
        Assert.True(userData.Success, userData.Failure);
        Assert.Contains("<UserCert>0A 1B 2C 3D </UserCert>", userData.Xml);
        Assert.Contains("<Type>13</Type>", userData.Xml);
    }

    [Fact]
    public void BuildProfile_MissingPassword_IsStillAValidProfile()
    {
        var credential = Credential();
        credential.Password = null;

        // The profile itself carries no credentials, so it is valid...
        Assert.True(EnterpriseProfileBuilder.BuildProfileXml(credential).Success);

        // ...but the account document must fail loudly: without it an unattended handshake can only end
        // in a Windows prompt or a failure.
        var result = EnterpriseProfileBuilder.BuildEapUserDataXml(credential);
        Assert.False(result.Success);
        Assert.Null(result.Xml);
        Assert.Contains("密码", result.Failure);
    }

    [Fact]
    public void BuildProfile_CustomXmlWithoutOverride_IsRejected()
    {
        var credential = Credential();
        credential.Eap = WifiEapMethod.CustomXml;

        var result = EnterpriseProfileBuilder.BuildProfileXml(credential);

        Assert.False(result.Success);
        Assert.Contains("自定义", result.Failure);
    }

    [Fact]
    public void BuildProfile_CustomXmlOverride_IsUsedVerbatim()
    {
        var credential = Credential();
        credential.Eap = WifiEapMethod.CustomXml;
        credential.ProfileXmlOverride = "<WLANProfile xmlns=\"x\"><name>custom</name></WLANProfile>";

        var result = EnterpriseProfileBuilder.BuildProfileXml(credential);

        Assert.True(result.Success, result.Failure);
        Assert.Equal(credential.ProfileXmlOverride, result.Xml);
    }

    [Fact]
    public void BuildProfile_WinLogonCredentials_OmitsAccountAndPassword()
    {
        var credential = Credential();
        credential.UseWinLogonCredentials = true;
        credential.Identity = string.Empty;
        credential.Password = null;

        var result = EnterpriseProfileBuilder.BuildProfileXml(credential);

        Assert.True(result.Success, result.Failure);
        Assert.Contains("<UseWinLogonCredentials>true</UseWinLogonCredentials>", result.Xml);
        Assert.DoesNotContain("<UserName>", result.Xml);
        Assert.DoesNotContain("<Password>", result.Xml);

        // Windows-logon credentials come from the session: there is no user data document to write.
        Assert.False(EnterpriseProfileBuilder.BuildEapUserDataXml(credential).Success);
    }

    [Fact]
    public void Inspector_RecognisesAGeneratedProfile()
    {
        var xml = EnterpriseProfileBuilder.BuildProfileXml(Credential()).Xml!;

        Assert.True(WifiProfileInspector.IsEnterprise(xml));
        Assert.True(WifiProfileInspector.HasEapConfiguration(xml));
        Assert.Equal("HXXY-WiFi", WifiProfileInspector.TryReadProfileName(xml));
        Assert.Equal("HXXY-WiFi", WifiProfileInspector.TryReadSsid(xml));
        Assert.Equal("WPA2", WifiProfileInspector.TryReadAuthentication(xml));
        Assert.Equal("25", WifiProfileInspector.TryReadEapMethodType(xml));

        // The generated profile stores no account: it lives in the EAP user data document.
        Assert.Null(WifiProfileInspector.TryReadIdentity(xml));
    }

    [Fact]
    public void Inspector_ReadsAnIdentityThatWindowsStoredInTheProfile()
    {
        const string withIdentity = """
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>HXXY-WiFi</name>
              <SSIDConfig><SSID><name>HXXY-WiFi</name></SSID></SSIDConfig>
              <MSM><security><authEncryption><useOneX>true</useOneX></authEncryption>
                <OneX xmlns="http://www.microsoft.com/networking/OneX/v1"><EAPConfig><EapHostConfig>
                  <Config><Eap xmlns="x"><Type>25</Type><EapType><Eap xmlns="x"><Type>26</Type>
                    <EapType><UserName>student01</UserName></EapType>
                  </Eap></EapType></Eap></Config>
                </EapHostConfig></EAPConfig></OneX>
              </security></MSM>
            </WLANProfile>
            """;

        Assert.Equal("student01", WifiProfileInspector.TryReadIdentity(withIdentity));
        Assert.Equal("HXXY-WiFi", WifiProfileInspector.TryReadSsid(withIdentity));
    }

    [Fact]
    public void Inspector_ImportsServerValidationFromAnExistingProfile()
    {
        const string xml = "<EapType><ServerNames>radius.example;backup.example</ServerNames>" +
                           "<TrustedRootCA>b2 f9 55 2a</TrustedRootCA>" +
                           "<TrustedRootCA>AA-BB-CC-DD</TrustedRootCA></EapType>";

        Assert.Equal(new[] { "radius.example", "backup.example" },
            WifiProfileInspector.ReadServerNames(xml));
        Assert.Equal(new[] { "B2F9552A", "AABBCCDD" },
            WifiProfileInspector.ReadTrustedRootCaThumbprints(xml));
    }

    [Fact]
    public void Inspector_RejectsPersonalProfile()
    {
        const string personal = """
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>ZhangAndroid</name>
              <SSIDConfig><SSID><name>ZhangAndroid</name></SSID></SSIDConfig>
              <MSM><security><authEncryption>
                <authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX>
              </authEncryption></security></MSM>
            </WLANProfile>
            """;

        Assert.False(WifiProfileInspector.IsEnterprise(personal));
        Assert.Equal("ZhangAndroid", WifiProfileInspector.TryReadSsid(personal));
    }

    [Fact]
    public void ProfileUpdate_Reason_IsMissingThenNotEnterpriseThenChangedThenUpToDate()
    {
        var credential = Credential();
        var desired = WifiProfileInspector.Fingerprint(EnterpriseProfileBuilder.BuildProfileXml(credential).Xml!);

        Assert.Equal(ProfileUpdateReason.Missing, WifiProfileInspector.Evaluate(null, desired, credential));
        Assert.Equal(ProfileUpdateReason.NotEnterprise,
            WifiProfileInspector.Evaluate("<WLANProfile><useOneX>false</useOneX></WLANProfile>", desired, credential));

        // A profile Windows (or another tool) created is taken over once.
        var existing = EnterpriseProfileBuilder.BuildProfileXml(credential).Xml!;
        Assert.Equal(ProfileUpdateReason.LibraryChanged, WifiProfileInspector.Evaluate(existing, desired, credential));

        // After we wrote it, the same XML counts as up to date.
        credential.AppliedFingerprint = desired;
        Assert.Equal(ProfileUpdateReason.UpToDate, WifiProfileInspector.Evaluate(existing, desired, credential));

        // Changing the password invalidates the stored profile (the library edit clears the fingerprint).
        credential.Password = "different";
        credential.AppliedFingerprint = null;
        var newDesired = WifiProfileInspector.Fingerprint(EnterpriseProfileBuilder.BuildProfileXml(credential).Xml!);
        Assert.Equal(ProfileUpdateReason.LibraryChanged, WifiProfileInspector.Evaluate(existing, newDesired, credential));
    }

    [Fact]
    public void EapRetry_FiveFailuresAbandonTheNetworkForThisRun()
    {
        var policy = new EapConnectRetryPolicy();
        var guid = Guid.NewGuid();
        var now = TestData.Now;
        var abandonedEvents = 0;

        for (var i = 1; i <= 5; i++)
        {
            var outcome = policy.RecordFailure(guid, "HXXY-WiFi", now.AddMinutes(i), "802.1X authentication failed");
            Assert.Equal(i, outcome.Failures);
            Assert.Equal(5 - i, outcome.AttemptsRemaining);
            if (outcome.AbandonedNow)
            {
                abandonedEvents++;
            }
        }

        Assert.Equal(1, abandonedEvents);
        Assert.True(policy.IsAbandoned(guid, "HXXY-WiFi"));
        Assert.Equal(5, policy.FailureCount(guid, "HXXY-WiFi"));

        // Further failures do not change the decision.
        var extra = policy.RecordFailure(guid, "HXXY-WiFi", now.AddHours(1), "again");
        Assert.True(extra.AlreadyAbandoned);
        Assert.False(extra.AbandonedNow);
        Assert.Equal(5, extra.Failures);
    }

    [Fact]
    public void EapRetry_SuccessAndForgetClearTheAbandonment()
    {
        var policy = new EapConnectRetryPolicy(2);
        var guid = Guid.NewGuid();
        var other = Guid.NewGuid();

        policy.RecordFailure(guid, "HXXY-WiFi", TestData.Now, "fail");
        policy.RecordFailure(guid, "HXXY-WiFi", TestData.Now, "fail");
        Assert.True(policy.IsAbandoned(guid, "HXXY-WiFi"));

        // Another adapter is tracked separately: the give-up is per adapter.
        Assert.False(policy.IsAbandoned(other, "HXXY-WiFi"));

        policy.RecordFailure(guid, "ZhangAndroid", TestData.Now, "fail");
        Assert.True(policy.Forget(guid, "HXXY-WiFi"));
        Assert.False(policy.IsAbandoned(guid, "HXXY-WiFi"));

        policy.RecordSuccess(guid, "ZhangAndroid");
        Assert.Equal(0, policy.FailureCount(guid, "ZhangAndroid"));
    }

    [Fact]
    public void EapRetry_IsNeverPersisted()
    {
        // The policy has no serialization surface at all: a new instance is the state after a restart,
        // which is what "temporarily give up, try again on the next start" requires.
        var policy = new EapConnectRetryPolicy(1);
        policy.RecordFailure(Guid.NewGuid(), "HXXY-WiFi", TestData.Now, "fail");

        var fresh = new EapConnectRetryPolicy(1);
        Assert.Equal(0, fresh.AbandonedCount);
        Assert.Empty(fresh.Snapshot());
    }

    [Fact]
    public void EapRetry_Snapshot_ReportsTheCurrentStateForTheUi()
    {
        var policy = new EapConnectRetryPolicy(1);
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        policy.RecordFailure(guid, "HXXY-WiFi", TestData.Now, "802.1X 认证失败");

        var status = Assert.Single(policy.Snapshot());
        Assert.Equal(guid, status.InterfaceGuid);
        Assert.Equal("HXXY-WiFi", status.Ssid);
        Assert.True(status.Abandoned);
        Assert.Equal("802.1X 认证失败", status.LastReason);
    }

    [Fact]
    public void Library_FindsEntriesBySsidOrProfileName()
    {
        var library = new WifiCredentialLibrary();
        library.Networks.Add(new WifiNetworkCredential { Ssid = "HXXY-WiFi", ProfileName = "HXXY-WiFi-8021X" });

        Assert.NotNull(library.Find("hxxy-wifi"));
        Assert.NotNull(library.FindByProfile("HXXY-WiFi-8021X"));
        Assert.NotNull(library.FindByProfile("hxxy-wifi"));
        Assert.Null(library.Find("ZhangAndroid"));
    }

    [Fact]
    public void Library_PasswordIsNeverSerialized()
    {
        var library = new WifiCredentialLibrary();
        library.Networks.Add(new WifiNetworkCredential
        {
            Ssid = "HXXY-WiFi",
            Identity = "user",
            Password = "topsecret",
            PasswordProtected = "BASE64BLOB",
        });

        // The library document goes through the app's source generated serializer.
        var json = NetworkGuardianJson.Serialize(library);

        Assert.Contains("BASE64BLOB", json);
        Assert.DoesNotContain("topsecret", json);
    }

    [Fact]
    public void Configuration_DefaultsMatchTheDocumentedEapBehaviour()
    {
        var config = GuardianConfig.CreateDefault();

        Assert.True(config.General.RadioWatchdogEnabled);
        Assert.Equal(3, config.General.RadioWatchdogSeconds);
        Assert.True(config.Wifi.UseCredentialLibraryForEap);
        Assert.True(config.Wifi.ApplyEapProfileOnConnect);
        Assert.Equal(5, config.Wifi.EapConnectMaxAttempts);
    }
}
