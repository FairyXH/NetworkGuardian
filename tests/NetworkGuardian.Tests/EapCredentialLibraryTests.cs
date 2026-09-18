using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Wlan;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// The self-maintained wireless network library: profile XML generation for 802.1X/EAP networks,
/// the profile comparison rules, and the bounded retry that gives a rejected network up for the run.
/// </summary>
public sealed class EapCredentialLibraryTests
{
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
    public void Build_PeapMschapV2_ProducesWindowsEnterpriseProfile()
    {
        var result = EnterpriseProfileBuilder.Build(Credential());

        Assert.True(result.Success, result.Failure);
        var xml = result.Xml!;

        Assert.Contains("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Contains("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">", xml);
        Assert.Contains("<name>HXXY-WiFi</name>", xml);
        Assert.Contains($"<hex>{EnterpriseProfileBuilder.ToHex("HXXY-WiFi")}</hex>", xml);
        Assert.Contains("<connectionMode>auto</connectionMode>", xml);
        Assert.Contains("<authentication>WPA2</authentication>", xml);
        Assert.Contains("<encryption>AES</encryption>", xml);
        Assert.Contains("<useOneX>true</useOneX>", xml);
        Assert.Contains("<OneX xmlns=\"http://www.microsoft.com/networking/OneX/v1\">", xml);

        // PEAP (25) with inner MSCHAPv2 (26) and the stored account.
        Assert.Contains("<Type xmlns=\"http://www.microsoft.com/provisioning/EapCommon\">25</Type>", xml);
        Assert.Contains("<Type>26</Type>", xml);
        Assert.Contains("<UserName>20230001@campus.example.edu</UserName>", xml);
        Assert.Contains("<Domain>campus</Domain>", xml);
        Assert.Contains("<ServerNames>radius.campus.example.edu</ServerNames>", xml);
        Assert.Contains("<TrustedRootCA>0A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D</TrustedRootCA>", xml);
    }

    [Fact]
    public void Build_EscapesValuesThatWouldBreakTheXml()
    {
        var credential = Credential();
        credential.Ssid = "Campus & Co <lab>";

        var xml = EnterpriseProfileBuilder.Build(credential).Xml!;

        Assert.Contains("<name>Campus &amp; Co &lt;lab&gt;</name>", xml);
        // The password is escaped, never written raw.
        Assert.Contains("<Password>p@ss&amp;word&lt;&gt;</Password>", xml);
        Assert.DoesNotContain("p@ss&word", xml);
    }

    [Fact]
    public void Build_NonAsciiSsid_UsesUtf8Hex()
    {
        var credential = Credential();
        credential.Ssid = "校园网";

        var xml = EnterpriseProfileBuilder.Build(credential).Xml!;

        Assert.Contains($"<hex>{EnterpriseProfileBuilder.ToHex("校园网")}</hex>", xml);
        Assert.Contains("<name>校园网</name>", xml);
        Assert.Contains("E6A0A1E59BADE7BD91", EnterpriseProfileBuilder.ToHex("校园网"));
    }

    [Fact]
    public void Build_ManualConnectionMode_WhenAutomaticIsOff()
    {
        var credential = Credential();
        credential.ConnectAutomatically = false;

        var xml = EnterpriseProfileBuilder.Build(credential).Xml!;

        Assert.Contains("<connectionMode>manual</connectionMode>", xml);
    }

    [Fact]
    public void Build_HiddenNetwork_MarksNonBroadcast()
    {
        var credential = Credential();
        credential.Hidden = true;

        var xml = EnterpriseProfileBuilder.Build(credential).Xml!;

        Assert.Contains("<nonBroadcast>true</nonBroadcast>", xml);
    }

    [Fact]
    public void Build_Tls_UsesCertificateStoreAndNoPassword()
    {
        var credential = Credential();
        credential.Eap = WifiEapMethod.Tls;
        credential.Password = null;

        var result = EnterpriseProfileBuilder.Build(credential);

        Assert.True(result.Success, result.Failure);
        Assert.Contains("<Type xmlns=\"http://www.microsoft.com/provisioning/EapCommon\">13</Type>", result.Xml);
        Assert.Contains("<SimpleCertSelection>true</SimpleCertSelection>", result.Xml);
        Assert.DoesNotContain("<Password>", result.Xml);
    }

    [Fact]
    public void Build_MissingPassword_ReportsAProblemAndNeverEmitsXml()
    {
        var credential = Credential();
        credential.Password = null;

        var result = EnterpriseProfileBuilder.Build(credential);

        Assert.False(result.Success);
        Assert.Null(result.Xml);
        Assert.Contains("密码", result.Failure);
    }

    [Fact]
    public void Build_CustomXmlWithoutOverride_IsRejected()
    {
        var credential = Credential();
        credential.Eap = WifiEapMethod.CustomXml;

        var result = EnterpriseProfileBuilder.Build(credential);

        Assert.False(result.Success);
        Assert.Contains("自定义", result.Failure);
    }

    [Fact]
    public void Build_CustomXmlOverride_IsUsedVerbatim()
    {
        var credential = Credential();
        credential.Eap = WifiEapMethod.CustomXml;
        credential.ProfileXmlOverride = "<WLANProfile xmlns=\"x\"><name>custom</name></WLANProfile>";

        var result = EnterpriseProfileBuilder.Build(credential);

        Assert.True(result.Success, result.Failure);
        Assert.Equal(credential.ProfileXmlOverride, result.Xml);
    }

    [Fact]
    public void Build_WinLogonCredentials_OmitsAccountAndPassword()
    {
        var credential = Credential();
        credential.UseWinLogonCredentials = true;
        credential.Identity = string.Empty;
        credential.Password = null;

        var result = EnterpriseProfileBuilder.Build(credential);

        Assert.True(result.Success, result.Failure);
        Assert.Contains("<UseWinLogonCredentials>true</UseWinLogonCredentials>", result.Xml);
        Assert.DoesNotContain("<UserName>", result.Xml);
        Assert.DoesNotContain("<Password>", result.Xml);
    }

    [Fact]
    public void Inspector_RecognisesAGeneratedProfile()
    {
        var xml = EnterpriseProfileBuilder.Build(Credential()).Xml!;

        Assert.True(WifiProfileInspector.IsEnterprise(xml));
        Assert.True(WifiProfileInspector.HasEapConfiguration(xml));
        Assert.Equal("HXXY-WiFi", WifiProfileInspector.TryReadProfileName(xml));
        Assert.Equal("HXXY-WiFi", WifiProfileInspector.TryReadSsid(xml));
        Assert.Equal("WPA2", WifiProfileInspector.TryReadAuthentication(xml));
        Assert.Equal("25", WifiProfileInspector.TryReadEapMethodType(xml));
        Assert.Equal("20230001@campus.example.edu", WifiProfileInspector.TryReadIdentity(xml));
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
        var desired = WifiProfileInspector.Fingerprint(EnterpriseProfileBuilder.Build(credential).Xml!);

        Assert.Equal(ProfileUpdateReason.Missing, WifiProfileInspector.Evaluate(null, desired, credential));
        Assert.Equal(ProfileUpdateReason.NotEnterprise,
            WifiProfileInspector.Evaluate("<WLANProfile><useOneX>false</useOneX></WLANProfile>", desired, credential));

        // A profile Windows (or another tool) created is taken over once.
        var existing = EnterpriseProfileBuilder.Build(credential).Xml!;
        Assert.Equal(ProfileUpdateReason.LibraryChanged, WifiProfileInspector.Evaluate(existing, desired, credential));

        // After we wrote it, the same XML counts as up to date.
        credential.AppliedFingerprint = desired;
        Assert.Equal(ProfileUpdateReason.UpToDate, WifiProfileInspector.Evaluate(existing, desired, credential));

        // Changing the password invalidates the stored profile.
        credential.Password = "different";
        var newDesired = WifiProfileInspector.Fingerprint(EnterpriseProfileBuilder.Build(credential).Xml!);
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
    public void Credential_PasswordIsNeverSerialized()
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
        var json = NetworkGuardian.Core.Serialization.NetworkGuardianJson.Serialize(library);

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
        Assert.Equal(5, config.Wifi.EapConnectMaxAttempts);
    }
}
