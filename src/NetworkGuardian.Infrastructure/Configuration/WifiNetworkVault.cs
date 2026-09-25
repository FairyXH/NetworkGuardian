using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Serialization;

namespace NetworkGuardian.Infrastructure.Configuration;

/// <summary>
/// The application's own wireless network library: one entry per known Wi-Fi network, stored in
/// <c>wifi-networks.json</c> next to <c>config.json</c>, independent of the Windows profile store.
/// </summary>
/// <remarks>
/// Passwords leave this class only through <see cref="WifiNetworkCredential.Password"/>, which is never
/// serialized; on disk only the <see cref="ISecretProtector"/> blob appears. Loading reverses that, so a
/// blob written by another user or on another machine leaves the entry usable-but-unauthenticated
/// instead of failing the whole load.
/// <para>
/// Any edit of an entry clears its "applied" marker. That marker is what tells the WLAN layer whether
/// the profile on the adapter still matches the library - and because the profile document carries no
/// password, a password change could not be detected otherwise.
/// </para>
/// </remarks>
public sealed class WifiNetworkVault
{
    private readonly ILogger<WifiNetworkVault> _logger;
    private readonly ISecretProtector? _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private WifiCredentialLibrary _library = new();

    public WifiNetworkVault(
        string? path = null,
        ISecretProtector? protector = null,
        ILogger<WifiNetworkVault>? logger = null)
    {
        _logger = logger ?? NullLogger<WifiNetworkVault>.Instance;
        _protector = protector;
        Path = path ?? GuardianPaths.WifiCredentialFile;
    }

    public string Path { get; }

    public string BackupPath => Path + ".bak";

    /// <summary>True when the file had to be discarded because it could not be parsed.</summary>
    public bool RecoveredFromCorruption { get; private set; }

    /// <summary>Notes from the most recent load (corruption, undecryptable passwords).</summary>
    public IReadOnlyList<string> LastLoadIssues { get; private set; } = Array.Empty<string>();

    /// <summary>Name of the protection mechanism in use, for the UI.</summary>
    public string ProtectionName => _protector?.Name ?? "未配置（密码不会保存）";

    /// <summary>
    /// A copy of every entry. Copies matter: edits have to be handed back through
    /// <see cref="SaveAsync"/>, where they are compared against the stored state - an editor that could
    /// mutate the live entries would make "this entry changed" undetectable, and the applied marker
    /// would survive a password change.
    /// </summary>
    public IReadOnlyList<WifiNetworkCredential> Entries
    {
        get
        {
            lock (_stateGate)
            {
                return _library.Networks.Select(Clone).ToList();
            }
        }
    }

    public WifiNetworkCredential? Find(string? ssid)
    {
        lock (_stateGate)
        {
            var match = _library.Find(ssid);
            return match is null ? null : Clone(match);
        }
    }

    public WifiNetworkCredential? FindByProfile(string? profileName)
    {
        lock (_stateGate)
        {
            var match = _library.FindByProfile(profileName);
            return match is null ? null : Clone(match);
        }
    }

    public int Count
    {
        get
        {
            lock (_stateGate)
            {
                return _library.Networks.Count;
            }
        }
    }

    public async Task<WifiCredentialLibrary> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var issues = new List<string>();
            RecoveredFromCorruption = false;

            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            WifiCredentialLibrary? library = null;
            if (File.Exists(Path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(Path, Encoding.UTF8, cancellationToken)
                        .ConfigureAwait(false);
                    library = NetworkGuardianJson.Deserialize<WifiCredentialLibrary>(json);
                }
                catch (Exception ex)
                {
                    issues.Add($"无线网络库无法解析（{ex.GetType().Name}），已重置为空库。");
                    _logger.LogError(ex, "Failed to parse the wireless network library at {Path}", Path);
                }
            }

            if (library is null)
            {
                if (File.Exists(Path))
                {
                    RecoveredFromCorruption = true;
                    TryQuarantine();
                }

                library = new WifiCredentialLibrary();
            }

            library.Networks ??= new List<WifiNetworkCredential>();
            library.Version = library.Networks.Count > 0 && library.Version <= 0
                ? WifiCredentialLibrary.CurrentVersion
                : library.Version;

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in library.Networks)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !seenIds.Add(entry.Id))
                {
                    entry.Id = Guid.NewGuid().ToString("N");
                    seenIds.Add(entry.Id);
                    issues.Add($"「{entry.Ssid}」的条目 ID 缺失或重复，已重新生成。");
                }

                if (!TryUnprotect(entry))
                {
                    issues.Add($"「{entry.Ssid}」的密码无法解密（可能由其他用户或其他电脑保存），" +
                               "需要重新填写后才能用于 Wi-Fi 认证。");
                }
            }

            lock (_stateGate)
            {
                _library = library;
            }

            LastLoadIssues = issues;
            if (issues.Count > 0)
            {
                foreach (var issue in issues)
                {
                    _logger.LogWarning("Wireless library note: {Issue}", issue);
                }
            }

            _logger.LogInformation("Wireless network library loaded from {Path} ({Count} 个网络, 保护方式 {Protection})",
                Path, library.Networks.Count, ProtectionName);

            return library;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists the given entries. A password that is present in memory is protected here; an entry
    /// whose in-memory password is null keeps the blob it already had.
    /// </summary>
    public async Task SaveAsync(IEnumerable<WifiNetworkCredential> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> previous;
            lock (_stateGate)
            {
                previous = _library.Networks
                    .GroupBy(e => e.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => Signature(group.Last()), StringComparer.Ordinal);
            }
            var library = new WifiCredentialLibrary { Version = WifiCredentialLibrary.CurrentVersion };
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var source in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = Clone(source);
                entry.Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
                if (!seenIds.Add(entry.Id))
                {
                    entry.Id = Guid.NewGuid().ToString("N");
                    seenIds.Add(entry.Id);
                }

                if (entry.Password is not null)
                {
                    if (_protector is null)
                    {
                        throw new InvalidOperationException(
                            "没有可用的密码保护实现；拒绝以明文保存 Wi-Fi 密码。");
                    }

                    entry.PasswordProtected = entry.Password.Length == 0
                        ? null
                        : _protector.Protect(entry.Password);

                    if (entry.Password.Length > 0)
                    {
                        entry.PasswordUpdatedUtc = DateTimeOffset.UtcNow;
                    }
                }

                // An edited entry must be re-applied to Windows: the profile document itself carries no
                // password, so a changed password is invisible to a profile comparison.
                if (!previous.TryGetValue(entry.Id, out var signature) || !string.Equals(signature, Signature(entry), StringComparison.Ordinal))
                {
                    entry.AppliedFingerprint = null;
                    entry.LastAppliedUtc = null;
                }

                library.Networks.Add(entry);
            }

            await WriteAtomicAsync(library, cancellationToken).ConfigureAwait(false);

            lock (_stateGate)
            {
                _library = library;
            }

            _logger.LogInformation("Wireless network library saved to {Path} ({Count} 个网络)", Path, library.Networks.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records that a profile was applied, so the next cycle does not rewrite it.</summary>
    public async Task MarkAppliedAsync(
        string entryId,
        string? fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WifiCredentialLibrary updated;
            lock (_stateGate)
            {
                updated = new WifiCredentialLibrary
                {
                    Version = _library.Version,
                    Networks = _library.Networks.Select(Clone).ToList(),
                };
            }

            var entry = updated.Networks.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
            if (entry is null)
            {
                return;
            }

            entry.AppliedFingerprint = fingerprint;
            entry.LastAppliedUtc = now;
            await WriteAtomicAsync(updated, cancellationToken).ConfigureAwait(false);

            lock (_stateGate)
            {
                _library = updated;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fills <see cref="WifiNetworkCredential.Password"/> from the stored blob.</summary>
    public bool TryUnprotect(WifiNetworkCredential entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrEmpty(entry.PasswordProtected))
        {
            entry.PasswordDecryptionFailed = false;
            return true;
        }

        if (_protector is null)
        {
            entry.PasswordDecryptionFailed = true;
            return false;
        }

        var clearText = _protector.Unprotect(entry.PasswordProtected);
        if (clearText is null)
        {
            entry.PasswordDecryptionFailed = true;
            return false;
        }

        entry.Password = clearText;
        entry.PasswordDecryptionFailed = false;
        return true;
    }

    /// <summary>
    /// Sets the in-memory password of an entry. An empty string clears the stored password; null keeps
    /// whatever is stored.
    /// </summary>
    public static void ApplyPassword(WifiNetworkCredential entry, string? clearText)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Password = clearText;
        entry.PasswordDecryptionFailed = false;
    }

    /// <summary>The catalogue the decision engine sees: which SSIDs the library can authenticate.</summary>
    public WifiEapCatalog BuildCatalog()
    {
        List<WifiNetworkCredential> usableEntries;
        lock (_stateGate)
        {
            usableEntries = _library.Networks
                .Where(e => e.Enabled && !e.PasswordDecryptionFailed)
                .Where(e => !e.RequiresPassword || !string.IsNullOrEmpty(e.Password))
                .Where(e => !string.IsNullOrWhiteSpace(e.Ssid))
                .Select(Clone)
                .ToList();
        }
        var usable = usableEntries.Select(e => e.Ssid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profiles = usableEntries
            .GroupBy(e => e.Ssid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().EffectiveProfileName,
                StringComparer.OrdinalIgnoreCase);

        return new WifiEapCatalog
        {
            SsidsWithCredentials = usable,
            ProfileNamesBySsid = profiles,
        };
    }

    private async Task WriteAtomicAsync(WifiCredentialLibrary library, CancellationToken cancellationToken)
    {
        var json = NetworkGuardianJson.Serialize(library);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        if (File.Exists(Path))
        {
            try
            {
                File.Replace(tempPath, Path, BackupPath, ignoreMetadataErrors: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "File.Replace failed for {Path}; falling back to a plain move", Path);
                File.Copy(Path, BackupPath, overwrite: true);
                File.Move(tempPath, Path, overwrite: true);
            }
        }
        else
        {
            File.Move(tempPath, Path, overwrite: true);
        }
    }

    private void TryQuarantine()
    {
        try
        {
            var invalid = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Path) ?? ".",
                System.IO.Path.GetFileNameWithoutExtension(Path) + ".invalid.json");

            File.Copy(Path, invalid, overwrite: true);
            _logger.LogWarning("Preserved the unreadable wireless library as {Path}", invalid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to quarantine the unreadable wireless library");
        }
    }

    /// <summary>
    /// Everything that makes an entry "different from what was applied". Compared in memory only; the
    /// password participates here (so editing it forces a re-apply) but is never persisted in this form.
    /// </summary>
    private static string Signature(WifiNetworkCredential entry) => string.Join('\u001f',
        entry.Ssid,
        entry.ProfileName ?? string.Empty,
        entry.Kind.ToString(),
        entry.Security.ToString(),
        entry.Auth.ToString(),
        entry.Eap.ToString(),
        entry.Identity,
        entry.AnonymousIdentity ?? string.Empty,
        entry.Domain ?? string.Empty,
        entry.Password ?? string.Empty,
        entry.UseWinLogonCredentials.ToString(),
        string.Join(',', entry.ServerNames),
        string.Join(',', entry.TrustedRootCaThumbprints),
        entry.CertificateThumbprint ?? string.Empty,
        entry.DisableUserPromptForServerValidation.ToString(),
        entry.ConnectAutomatically.ToString(),
        entry.Hidden.ToString(),
        entry.Enabled.ToString(),
        entry.ProfileXmlOverride ?? string.Empty);

    private static WifiNetworkCredential Clone(WifiNetworkCredential source) => new()
    {
        Id = source.Id,
        Ssid = source.Ssid,
        ProfileName = source.ProfileName,
        Kind = source.Kind,
        Security = source.Security,
        Auth = source.Auth,
        Eap = source.Eap,
        Identity = source.Identity,
        AnonymousIdentity = source.AnonymousIdentity,
        Domain = source.Domain,
        Password = source.Password,
        PasswordProtected = source.PasswordProtected,
        PasswordDecryptionFailed = source.PasswordDecryptionFailed,
        PasswordUpdatedUtc = source.PasswordUpdatedUtc,
        UseWinLogonCredentials = source.UseWinLogonCredentials,
        ServerNames = source.ServerNames.ToList(),
        TrustedRootCaThumbprints = source.TrustedRootCaThumbprints.ToList(),
        CertificateThumbprint = source.CertificateThumbprint,
        DisableUserPromptForServerValidation = source.DisableUserPromptForServerValidation,
        ConnectAutomatically = source.ConnectAutomatically,
        Hidden = source.Hidden,
        Enabled = source.Enabled,
        ProfileXmlOverride = source.ProfileXmlOverride,
        AppliedFingerprint = source.AppliedFingerprint,
        LastAppliedUtc = source.LastAppliedUtc,
        Notes = source.Notes,
    };
}
