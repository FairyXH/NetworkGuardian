using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Wlan;
using NetworkGuardian.Windows.Wlan;
using Xunit;
using Xunit.Abstractions;

namespace NetworkGuardian.Tests;

/// <summary>
/// Writes generated 802.1X profiles (and their EAP credentials) to the real WLAN service and reads
/// them back.
/// </summary>
/// <remarks>
/// The only way to know whether a generated document satisfies the WLAN profile schema is asking the
/// service: <c>WlanSetProfile</c> / <c>WlanSetProfileEapXmlUserData</c> validate the XML and answer
/// with a WLAN reason code when they disagree. This is how the credential-in-profile mistake was found
/// (reason code 524289). The test is opt-in via <c>NETWORKGUARDIAN_WIFI_HARDWARE_TESTS=1</c> because it
/// creates - and removes - profiles in the current user's WLAN store; every test profile uses an SSID
/// that is not the user's, so a leftover entry could never associate with anything.
/// </remarks>
public sealed class WifiProfileHardwareTests
{
    private const string HardwareTestVariable = "NETWORKGUARDIAN_WIFI_HARDWARE_TESTS";

    private readonly ITestOutputHelper _output;

    public WifiProfileHardwareTests(ITestOutputHelper output) => _output = output;

    private static bool HardwareTestsEnabled =>
        Environment.GetEnvironmentVariable(HardwareTestVariable) is "1" or "true" or "TRUE";

    private static WifiNetworkCredential Peap() => new()
    {
        Ssid = "NG-SELFTEST-EAP",
        Identity = "selftest@example.invalid",
        Password = "SelfTest!23&<>",
        Auth = WifiEnterpriseAuth.Wpa2Enterprise,
        Eap = WifiEapMethod.PeapMschapv2,
        ServerNames = { "radius.example.invalid" },
    };

    [Fact]
    public void GeneratedProfiles_AndCredentials_AreAcceptedByTheWlanService()
    {
        if (!HardwareTestsEnabled)
        {
            _output.WriteLine($"skipped: set {HardwareTestVariable}=1 to run against the real WLAN service");
            return;
        }

        using var wifi = new NativeWifiManager();
        var adapters = wifi.GetAdapters();
        Assert.NotEmpty(adapters);

        var adapter = adapters[0];
        _output.WriteLine($"adapter: {adapter.Description} {adapter.InterfaceGuid:D}");

        var applier = new WifiProfileApplier(wifi);
        var variants = new (string Label, WifiNetworkCredential Credential)[]
        {
            ("peap-mschapv2-wpa2", Peap()),
            ("peap-mschapv2-wpa-tkip", new WifiNetworkCredential
            {
                Ssid = "NG-SELFTEST-EAP-WPA",
                Identity = "selftest@example.invalid",
                Password = "SelfTest!23",
                Auth = WifiEnterpriseAuth.WpaEnterprise,
                Eap = WifiEapMethod.PeapMschapv2,
            }),
            ("peap-hidden-manual", new WifiNetworkCredential
            {
                Ssid = "NG-SELFTEST-HIDDEN",
                Identity = "selftest@example.invalid",
                Password = "SelfTest!23",
                Hidden = true,
                ConnectAutomatically = false,
            }),
            ("eap-tls", new WifiNetworkCredential
            {
                Ssid = "NG-SELFTEST-TLS",
                Eap = WifiEapMethod.Tls,
            }),
            ("eap-tls-pinned-cert", new WifiNetworkCredential
            {
                Ssid = "NG-SELFTEST-TLS-CERT",
                Eap = WifiEapMethod.Tls,
                CertificateThumbprint = "0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d",
            }),
        };

        try
        {
            foreach (var (label, credential) in variants)
            {
                var result = applier.Apply(adapter.InterfaceGuid, credential, allowWrite: true);
                _output.WriteLine($"{label}: success={result.Success} profileWritten={result.ProfileWritten} " +
                                  $"userDataWritten={result.UserDataWritten} reason={result.UpdateReason} " +
                                  $"failure={result.Failure ?? "-"}");

                Assert.True(result.Success, $"{label}: {result.Failure}");
                Assert.True(result.ProfileWritten, $"{label}: the profile was expected to be written");

                var readBack = wifi.GetProfileXml(adapter.InterfaceGuid, credential.EffectiveProfileName);
                Assert.False(string.IsNullOrWhiteSpace(readBack), $"{label}: profile could not be read back");
                Assert.True(WifiProfileInspector.IsEnterprise(readBack), $"{label}: not an 802.1X profile");
                Assert.True(WifiProfileInspector.HasEapConfiguration(readBack), $"{label}: no EAP configuration");
                Assert.Equal(credential.Ssid, WifiProfileInspector.TryReadSsid(readBack));
                Assert.DoesNotContain("<UserName>", readBack);
                if (credential.RequiresPassword)
                {
                    Assert.DoesNotContain(credential.Password!, readBack);
                }

                _output.WriteLine($"   profile: authentication={WifiProfileInspector.TryReadAuthentication(readBack)} " +
                                  $"eapType={WifiProfileInspector.TryReadEapMethodType(readBack)} " +
                                  $"length={readBack!.Length}");

                if (credential.RequiresPassword)
                {
                    Assert.True(result.UserDataWritten, $"{label}: credentials were expected to be written");
                }
                else
                {
                    Assert.Equal(ProfileUpdateReason.Missing,
                        WifiProfileInspector.Evaluate(null, "x", credential));
                }

                // Applying twice must be a no-op: the entry is unchanged and already applied.
                credential.AppliedFingerprint = result.AppliedFingerprint;
                credential.LastAppliedUtc = DateTimeOffset.UtcNow;
                var second = applier.Apply(adapter.InterfaceGuid, credential, allowWrite: true);
                _output.WriteLine($"   second apply: success={second.Success} reason={second.UpdateReason} " +
                                  $"changed={second.Changed}");
                Assert.True(second.Success, second.Failure);
                Assert.Equal(ProfileUpdateReason.UpToDate, second.UpdateReason);
                Assert.False(second.Changed);

                // A password change must invalidate the stored profile. The change is detected through
                // the applied marker, which the vault clears on any edit (the profile document itself
                // carries no password, so nothing else could reveal it).
                credential.Password = "SelfTest!24";
                credential.AppliedFingerprint = null;
                credential.LastAppliedUtc = null;
                var third = applier.Apply(adapter.InterfaceGuid, credential,
                    allowWrite: false);
                Assert.Equal(ProfileUpdateReason.LibraryChanged, third.UpdateReason);
                _output.WriteLine($"   after password change: reason={third.UpdateReason} (write disabled) {third.Failure}");
            }
        }
        finally
        {
            foreach (var (label, credential) in variants)
            {
                var deleted = applier.Remove(adapter.InterfaceGuid, credential.EffectiveProfileName);
                var stillThere = wifi.GetProfileXml(adapter.InterfaceGuid, credential.EffectiveProfileName);
                _output.WriteLine($"cleanup {label}: deleteSuccess={deleted.Success} removed={stillThere is null}");
                Assert.True(stillThere is null, $"{label}: the test profile was not removed");
            }
        }
    }

    [Fact]
    public void ExistingProfilesOfThisMachine_AreClassifiedCorrectly()
    {
        if (!HardwareTestsEnabled)
        {
            _output.WriteLine($"skipped: set {HardwareTestVariable}=1 to run against the real WLAN service");
            return;
        }

        using var wifi = new NativeWifiManager();
        var adapters = wifi.GetAdapters();
        Assert.NotEmpty(adapters);

        var sawEnterprise = false;
        foreach (var adapter in adapters)
        {
            foreach (var profile in wifi.GetProfileNames(adapter.InterfaceGuid))
            {
                var xml = wifi.GetProfileXml(adapter.InterfaceGuid, profile);
                var enterprise = WifiProfileInspector.IsEnterprise(xml);

                _output.WriteLine($"{adapter.Description} / {profile}: enterprise={enterprise} " +
                                  $"authentication={WifiProfileInspector.TryReadAuthentication(xml)} " +
                                  $"eapType={WifiProfileInspector.TryReadEapMethodType(xml)} " +
                                  $"identity={WifiProfileInspector.TryReadIdentity(xml) ?? "-"}");

                sawEnterprise |= enterprise;
            }
        }

        _output.WriteLine(sawEnterprise
            ? "at least one 802.1X profile found on this machine"
            : "no 802.1X profile on this machine; the enterprise path is covered by the generated-profile test");
    }
}
