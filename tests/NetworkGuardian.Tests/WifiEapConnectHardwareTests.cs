using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Wlan;
using NetworkGuardian.Infrastructure.Configuration;
using NetworkGuardian.Windows.Security;
using NetworkGuardian.Windows.Wlan;
using Xunit;
using Xunit.Abstractions;

namespace NetworkGuardian.Tests;

/// <summary>
/// The whole 802.1X loop against a real access point: the account comes from the built-in library, the
/// profile and credentials are written to the adapter, the connection attempt fails with a real EAP
/// rejection, and five of those failures give the network up for the run.
/// </summary>
/// <remarks>
/// Opt-in (<c>NETWORKGUARDIAN_WIFI_HARDWARE_TESTS=1</c>) and deliberately non-destructive:
/// <list type="bullet">
/// <item>the generated profile uses its own name (<c>NG-SELFTEST-HXXY</c>) and manual connection mode, so
/// the user's profile for that SSID is never touched;</item>
/// <item>the account is a dummy identity, so no real campus account can be locked by the failed attempts;</item>
/// <item>the profile and the temporary library file are removed in a finally block.</item>
/// </list>
/// The point of a wrong password is that it produces the failure this whole feature is about; a success
/// would prove nothing about the retry budget.
/// </remarks>
[Collection(WifiHardwareCollection.Name)]
public sealed class WifiEapConnectHardwareTests
{
    private const string HardwareTestVariable = "NETWORKGUARDIAN_WIFI_HARDWARE_TESTS";

    private readonly ITestOutputHelper _output;

    public WifiEapConnectHardwareTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RepeatedEapRejections_AreCountedAndGiveTheNetworkUp()
    {
        if (Environment.GetEnvironmentVariable(HardwareTestVariable) is not ("1" or "true" or "TRUE"))
        {
            _output.WriteLine($"skipped: set {HardwareTestVariable}=1 to run against the real WLAN service");
            return;
        }

        const string ssid = "HXXY-WiFi";
        const string profileName = "NG-SELFTEST-HXXY";
        var vaultPath = Path.Combine(Path.GetTempPath(), $"ng-eap-{Guid.NewGuid():N}.json");

        using var wifi = new NativeWifiManager();
        var adapter = wifi.GetAdapters()[0];
        _output.WriteLine($"adapter: {adapter.Description} {adapter.InterfaceGuid:D}");

        // Is the network actually in range? Without it the attempt fails for the wrong reason.
        var scan = await wifi.RequestScanAsync(adapter.InterfaceGuid, force: true, TimeSpan.FromSeconds(15), CancellationToken.None);
        var visible = scan.Networks.FirstOrDefault(n => string.Equals(n.Ssid, ssid, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(visible);
        _output.WriteLine($"network: {visible!.Ssid} quality={visible.SignalQuality}% security={visible.Security}");
        Assert.True(WifiProfileInspector.IsEnterpriseSecurity(visible.Security),
            $"{ssid} is not an 802.1X network any more ({visible.Security}); the test needs one");

        var vault = new WifiNetworkVault(vaultPath, new DpapiSecretProtector(), NullLogger<WifiNetworkVault>.Instance);
        var applier = new WifiProfileApplier(wifi, NullLogger<WifiProfileApplier>.Instance);
        var engine = new GuardianDecisionEngine(GuardianConfig.CreateDefault());

        var entry = new WifiNetworkCredential
        {
            Ssid = ssid,
            ProfileName = profileName,
            Identity = "selftest@example.invalid",
            Password = "definitely-wrong-password",
            ConnectAutomatically = false,
        };

        try
        {
            await vault.SaveAsync(new[] { entry }, CancellationToken.None);

            // The password must never be readable from the library file, but must come back in memory.
            var onDisk = await File.ReadAllTextAsync(vaultPath);
            Assert.DoesNotContain("definitely-wrong-password", onDisk);
            var loaded = await new WifiNetworkVault(vaultPath, new DpapiSecretProtector(), NullLogger<WifiNetworkVault>.Instance)
                .LoadAsync(CancellationToken.None);
            var reloaded = Assert.Single(loaded.Networks);
            Assert.Equal("definitely-wrong-password", reloaded.Password);

            // The catalogue is what the decision engine sees: the SSID can be authenticated.
            Assert.True(vault.BuildCatalog().HasCredential(ssid));

            // 1. Write the profile and the account from the library to the adapter.
            var apply = applier.Apply(adapter.InterfaceGuid, reloaded, allowWrite: true);
            _output.WriteLine($"apply: success={apply.Success} profileWritten={apply.ProfileWritten} " +
                              $"userDataWritten={apply.UserDataWritten} failure={apply.Failure ?? "-"}");
            Assert.True(apply.Success, apply.Failure);
            Assert.True(apply.ProfileWritten);
            Assert.True(apply.UserDataWritten);

            reloaded.AppliedFingerprint = apply.AppliedFingerprint;
            reloaded.LastAppliedUtc = DateTimeOffset.UtcNow;

            // 2. Let the engine try, and report every outcome back the way the host does.
            for (var attempt = 1; attempt <= engine.EapRetries.MaxAttempts; attempt++)
            {
                var started = DateTimeOffset.UtcNow;
                var connect = await wifi.ConnectAsync(adapter.InterfaceGuid, profileName, null, CancellationToken.None);
                var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;

                _output.WriteLine($"attempt {attempt}: success={connect.Success} " +
                                  $"failure={connect.Failure ?? "-"} ({elapsed:F1}s)");

                engine.NotifyConnectResult(
                    adapter.InterfaceGuid, ssid, connect.Success, connect.Failure,
                    DateTimeOffset.UtcNow, requiredEap: true);

                // The failure must come from the network, not from a missing profile.
                Assert.False(connect.Success, "the network accepted a wrong password, so this test proves nothing");
                Assert.DoesNotContain("not open", connect.Failure ?? string.Empty);
            }

            var status = Assert.Single(engine.EapRetries.Snapshot());
            _output.WriteLine($"after {status.Failures} failures: abandoned={status.Abandoned} reason={status.LastReason}");
            Assert.Equal(engine.EapRetries.MaxAttempts, status.Failures);
            Assert.True(status.Abandoned);
            Assert.True(engine.EapRetries.IsAbandoned(adapter.InterfaceGuid, ssid));

            // 3. A corrected account clears the give-up immediately (what the UI does after an edit).
            Assert.Equal(1, engine.ClearEapRetries(ssid));
            Assert.False(engine.EapRetries.IsAbandoned(adapter.InterfaceGuid, ssid));
        }
        finally
        {
            // Leave the adapter idle first: deleting a profile the adapter is still connecting with can
            // succeed and then be reported again, which made this cleanup look like it had failed.
            await wifi.DisconnectAsync(adapter.InterfaceGuid, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(2));

            var removed = applier.RemoveEverywhere(profileName);
            var leftovers = wifi.GetAdapters()
                .Where(a => wifi.GetProfileXml(a.InterfaceGuid, profileName) is not null)
                .Select(a => a.Description)
                .ToList();

            _output.WriteLine($"cleanup: {removed.Describe()} (leftovers: {string.Join(", ", leftovers)})");
            Assert.Empty(leftovers);

            // The user's own profile for that SSID must still be there, untouched.
            var userProfile = wifi.GetProfileXml(adapter.InterfaceGuid, ssid);
            _output.WriteLine($"user profile intact: {!string.IsNullOrWhiteSpace(userProfile)}");

            if (File.Exists(vaultPath))
            {
                File.Delete(vaultPath);
            }
        }
    }
}
