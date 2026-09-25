using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;

namespace NetworkGuardian.Infrastructure.Configuration;

/// <summary>
/// JSON configuration store with atomic writes, a rolling backup and corrupt-file recovery so that
/// a damaged config.json can never stop the application from starting.
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    private readonly ILogger<JsonConfigStore> _logger;
    private readonly ConfigValidator _validator;
    private readonly ConfigMigrator _migrator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonConfigStore(
        string? path = null,
        ILogger<JsonConfigStore>? logger = null,
        ConfigValidator? validator = null,
        ConfigMigrator? migrator = null)
    {
        _logger = logger ?? NullLogger<JsonConfigStore>.Instance;
        _validator = validator ?? new ConfigValidator();
        _migrator = migrator ?? new ConfigMigrator();
        ConfigPath = path ?? GuardianPaths.ConfigFile;
        Current = GuardianConfig.CreateDefault();
    }

    public string ConfigPath { get; }

    public string BackupPath => ConfigPath + ".bak";

    public GuardianConfig Current { get; private set; }

    /// <summary>Issues reported by the most recent load (validation, migration, recovery).</summary>
    public IReadOnlyList<string> LastLoadIssues { get; private set; } = Array.Empty<string>();

    public bool RecoveredFromBackup { get; private set; }

    public bool RecoveredFromCorruption { get; private set; }

    /// <summary>
    /// Where an unparseable configuration file is preserved. It always lives next to the active
    /// config file so that a custom root (or a test) never writes into the user's real profile.
    /// </summary>
    public string CorruptFilePath
    {
        get
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            var name = Path.GetFileNameWithoutExtension(ConfigPath) + ".invalid.json";
            return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
        }
    }

    public async Task<GuardianConfig> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var issues = new List<string>();
            RecoveredFromBackup = false;
            RecoveredFromCorruption = false;

            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(ConfigPath))
            {
                _logger.LogInformation("No configuration found at {Path}; writing defaults", ConfigPath);
                var fresh = GuardianConfig.CreateDefault();
                _validator.Normalize(fresh);
                await SaveInternalAsync(fresh, cancellationToken).ConfigureAwait(false);
                Current = fresh;
                LastLoadIssues = new[] { "config.json did not exist and was created with default values." };
                return fresh;
            }

            GuardianConfig? config = null;
            try
            {
                var json = await File.ReadAllTextAsync(ConfigPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                config = ConfigJson.Deserialize(json);
            }
            catch (Exception ex)
            {
                issues.Add($"config.json could not be parsed ({ex.GetType().Name}: {ex.Message}).");
                _logger.LogError(ex, "Failed to parse {Path}", ConfigPath);
            }

            if (config is null && File.Exists(BackupPath))
            {
                try
                {
                    var backupJson = await File.ReadAllTextAsync(BackupPath, Encoding.UTF8, cancellationToken)
                        .ConfigureAwait(false);
                    config = ConfigJson.Deserialize(backupJson);
                    if (config is not null)
                    {
                        RecoveredFromBackup = true;
                        issues.Add("config.json was restored from the last good backup.");
                        _logger.LogWarning("Restored configuration from {Backup}", BackupPath);
                    }
                }
                catch (Exception ex)
                {
                    issues.Add($"the backup configuration could not be parsed either ({ex.GetType().Name}).");
                    _logger.LogError(ex, "Failed to parse the backup configuration");
                }
            }

            if (config is null)
            {
                RecoveredFromCorruption = true;
                TryQuarantine();
                config = GuardianConfig.CreateDefault();
                issues.Add("configuration was reset to defaults; the previous file was moved to " +
                           Path.GetFileNameWithoutExtension(ConfigPath) + ".invalid.json");
            }

            issues.AddRange(_migrator.Migrate(config));
            issues.AddRange(_validator.Normalize(config));

            Current = config;

            if (RecoveredFromBackup || RecoveredFromCorruption || issues.Count > 0)
            {
                await SaveInternalAsync(config, cancellationToken).ConfigureAwait(false);
            }

            LastLoadIssues = issues;
            _logger.LogInformation("Configuration loaded from {Path} (version {Version}, {IssueCount} note(s))",
                ConfigPath, config.Version, issues.Count);

            return config;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(GuardianConfig config, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(config, cancellationToken).ConfigureAwait(false);
            Current = config;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveInternalAsync(GuardianConfig config, CancellationToken cancellationToken)
    {
        // Validate before persisting so an invalid in-memory state never reaches disk unclamped.
        _validator.Normalize(config);
        var json = ConfigJson.Serialize(config);

        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = ConfigPath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        if (File.Exists(ConfigPath))
        {
            try
            {
                // Atomic on NTFS: replaces the target and keeps a backup copy in one operation.
                File.Replace(tempPath, ConfigPath, BackupPath, ignoreMetadataErrors: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "File.Replace failed for {Path}; falling back to a plain move", ConfigPath);
                File.Copy(ConfigPath, BackupPath, overwrite: true);
                File.Move(tempPath, ConfigPath, overwrite: true);
            }
        }
        else
        {
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
    }

    private void TryQuarantine()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                File.Copy(ConfigPath, CorruptFilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to quarantine the invalid configuration file");
        }
    }
}
