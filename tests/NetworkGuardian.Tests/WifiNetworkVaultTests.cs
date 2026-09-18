using System.Text;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Serialization;
using NetworkGuardian.Infrastructure.Configuration;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// The self-maintained wireless network library on disk: passwords are protected, edits invalidate the
/// "already applied" marker, and an unreadable or foreign file degrades instead of exploding.
/// </summary>
public sealed class WifiNetworkVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ng-vault-" + Guid.NewGuid().ToString("N")[..8]);

    public WifiNetworkVaultTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp directory is not a test failure.
        }
    }

    private string FilePath => Path.Combine(_root, "wifi-networks.json");

    private static WifiNetworkCredential Entry() => new()
    {
        Ssid = "HXXY-WiFi",
        Identity = "20230001@campus.example.edu",
        Domain = "campus",
        Password = "topsecret",
        ServerNames = { "radius.campus.example.edu" },
    };

    [Fact]
    public async Task Save_StoresThePasswordProtectedAndNeverInClearText()
    {
        var vault = new WifiNetworkVault(FilePath, new FakeProtector());
        await vault.SaveAsync(new[] { Entry() }, CancellationToken.None);

        Assert.True(File.Exists(FilePath));
        var json = await File.ReadAllTextAsync(FilePath);
        Assert.DoesNotContain("topsecret", json);
        Assert.Contains("passwordProtected", json);
        Assert.Contains("HXXY-WiFi", json);

        // The document is what the application's serializer produces, including the source generated context.
        var document = NetworkGuardianJson.Deserialize<WifiCredentialLibrary>(json);
        var stored = Assert.Single(document!.Networks);
        Assert.Null(stored.Password);
        Assert.False(string.IsNullOrEmpty(stored.PasswordProtected));

        // Loading decrypts it back into memory only.
        var reloaded = await new WifiNetworkVault(FilePath, new FakeProtector()).LoadAsync(CancellationToken.None);
        var entry = Assert.Single(reloaded.Networks);
        Assert.Equal("topsecret", entry.Password);
        Assert.False(entry.PasswordDecryptionFailed);
    }

    [Fact]
    public async Task Save_WithoutAProtector_RefusesToWriteThePasswordAtAll()
    {
        var vault = new WifiNetworkVault(FilePath, protector: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vault.SaveAsync(new[] { Entry() }, CancellationToken.None));

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Load_WithAForeignBlob_KeepsTheEntryAndReportsTheProblem()
    {
        await new WifiNetworkVault(FilePath, new FakeProtector(0x11)).SaveAsync(new[] { Entry() }, CancellationToken.None);

        var vault = new WifiNetworkVault(FilePath, new FakeProtector(0x22));
        var library = await vault.LoadAsync(CancellationToken.None);

        var entry = Assert.Single(library.Networks);
        Assert.Null(entry.Password);
        Assert.True(entry.PasswordDecryptionFailed);
        Assert.Single(vault.LastLoadIssues);
        Assert.Contains("无法解密", vault.LastLoadIssues[0]);

        // An entry that cannot authenticate is not advertised to the decision engine.
        Assert.False(vault.BuildCatalog().HasCredential("HXXY-WiFi"));
    }

    [Fact]
    public async Task Save_AfterAnEdit_ClearsTheAppliedMarker()
    {
        var protector = new FakeProtector();
        var vault = new WifiNetworkVault(FilePath, protector);

        // A first save introduces the entry; the marker is then recorded the way the host does it.
        await vault.SaveAsync(new[] { Entry() }, CancellationToken.None);
        var entries = vault.Entries;
        entries[0].AppliedFingerprint = "ABC";
        entries[0].LastAppliedUtc = TestData.Now;
        await vault.SaveAsync(entries, CancellationToken.None);
        Assert.Equal("ABC", Assert.Single(vault.Entries).AppliedFingerprint);

        // Saving the untouched entry keeps the marker (otherwise every cycle would rewrite the profile).
        await vault.SaveAsync(vault.Entries, CancellationToken.None);
        Assert.Equal("ABC", Assert.Single(vault.Entries).AppliedFingerprint);

        // Changing the password must invalidate it: the profile document carries no password, so the
        // WLAN layer could not notice the change in any other way.
        var edited = vault.Entries;
        WifiNetworkVault.ApplyPassword(edited[0], "topsecret2");
        await vault.SaveAsync(edited, CancellationToken.None);

        var afterEdit = Assert.Single(vault.Entries);
        Assert.Null(afterEdit.AppliedFingerprint);
        Assert.Null(afterEdit.LastAppliedUtc);
        Assert.Equal("topsecret2", afterEdit.Password);
    }

    [Fact]
    public async Task Save_ClearingThePassword_RemovesTheStoredBlob()
    {
        var vault = new WifiNetworkVault(FilePath, new FakeProtector());
        await vault.SaveAsync(new[] { Entry() }, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(Assert.Single(vault.Entries).PasswordProtected));

        var entries = vault.Entries;
        WifiNetworkVault.ApplyPassword(entries[0], string.Empty);
        await vault.SaveAsync(entries, CancellationToken.None);

        Assert.Null(Assert.Single(vault.Entries).PasswordProtected);
    }

    [Fact]
    public async Task Catalog_ListsOnlyNetworksThatCanAuthenticate()
    {
        var vault = new WifiNetworkVault(FilePath, new FakeProtector());
        var usable = Entry();
        var disabled = new WifiNetworkCredential
        {
            Ssid = "Campus-Guest",
            Identity = "guest",
            Password = "guest",
            Enabled = false,
        };
        var certificate = new WifiNetworkCredential
        {
            Ssid = "Campus-EAP-TLS",
            Eap = WifiEapMethod.Tls,
        };
        var withoutPassword = new WifiNetworkCredential
        {
            Ssid = "Campus-Unknown",
            Identity = "user",
        };

        await vault.SaveAsync(new[] { usable, disabled, certificate, withoutPassword }, CancellationToken.None);

        var catalog = vault.BuildCatalog();
        Assert.True(catalog.HasCredential("hxxy-wifi"));
        Assert.True(catalog.HasCredential("Campus-EAP-TLS"));
        Assert.False(catalog.HasCredential("Campus-Guest"));
        Assert.False(catalog.HasCredential("Campus-Unknown"));
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public async Task MarkApplied_RecordsTheFingerprintWithoutInvalidatingTheEntry()
    {
        var vault = new WifiNetworkVault(FilePath, new FakeProtector());
        await vault.SaveAsync(new[] { Entry() }, CancellationToken.None);
        var id = Assert.Single(vault.Entries).Id;

        await vault.MarkAppliedAsync(id, "FINGERPRINT", TestData.Now, CancellationToken.None);

        var stored = Assert.Single(vault.Entries);
        Assert.Equal("FINGERPRINT", stored.AppliedFingerprint);
        Assert.Equal(TestData.Now, stored.LastAppliedUtc);
        Assert.Equal("topsecret", stored.Password);
    }

    [Fact]
    public async Task Load_WithACorruptFile_StartsEmptyAndQuarantinesTheFile()
    {
        await File.WriteAllTextAsync(FilePath, "{ this is not json", Encoding.UTF8);

        var vault = new WifiNetworkVault(FilePath, new FakeProtector());
        var library = await vault.LoadAsync(CancellationToken.None);

        Assert.Empty(library.Networks);
        Assert.True(vault.RecoveredFromCorruption);
        Assert.True(File.Exists(Path.Combine(_root, "wifi-networks.invalid.json")));
    }

    private sealed class FakeProtector : ISecretProtector
    {
        private readonly byte _key;

        public FakeProtector(byte key = 0x5A) => _key = key;

        public string Name => "fake";

        // The key is part of the blob header, so a blob written with another key cannot be decrypted -
        // which is what DPAPI does for a blob created by another user or machine.
        private string Prefix => $"NGF1:{_key:X2}:";

        public string Protect(string clearText) =>
            Prefix + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(clearText).Select(b => (byte)(b ^ _key)).ToArray());

        public string? Unprotect(string protectedBlob)
        {
            if (!protectedBlob.StartsWith(Prefix, StringComparison.Ordinal))
            {
                // Blob written by another key/user: exactly what DPAPI returns for a foreign blob.
                return null;
            }

            try
            {
                var bytes = Convert.FromBase64String(protectedBlob[Prefix.Length..]);
                return Encoding.UTF8.GetString(bytes.Select(b => (byte)(b ^ _key)).ToArray());
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
