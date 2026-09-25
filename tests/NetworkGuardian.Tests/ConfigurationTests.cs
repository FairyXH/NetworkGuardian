using System.Text.Json;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Infrastructure.Configuration;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void OutOfRangeValues_AreClampedAndReported()
    {
        var config = GuardianConfig.CreateDefault();
        config.General.HealthSweepSeconds = 0;
        config.Probe.IntervalSeconds = 100000;
        config.Recovery.InternetFailureThreshold = 0;
        config.CampusAuth.MaxRunsPerHour = 0;

        var issues = new ConfigValidator().Normalize(config);

        Assert.Equal(5, config.General.HealthSweepSeconds);
        Assert.Equal(3600, config.Probe.IntervalSeconds);
        Assert.Equal(1, config.Recovery.InternetFailureThreshold);
        Assert.Equal(1, config.CampusAuth.MaxRunsPerHour);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void EmptyProbeEndpoints_AreRestored()
    {
        var config = GuardianConfig.CreateDefault();
        config.ProbeEndpoints.Clear();

        new ConfigValidator().Normalize(config);

        Assert.NotEmpty(config.ProbeEndpoints);
        Assert.Contains(config.ProbeEndpoints, e => e.Enabled);
        Assert.Contains(config.ProbeEndpoints, e => e.Name == "BaiduHttps");
        Assert.Contains(config.ProbeEndpoints, e => e.Name == "BingHttps");
    }

    [Fact]
    public void RequiredSuccessCount_IsBoundedByEnabledEndpoints()
    {
        var config = GuardianConfig.CreateDefault();
        foreach (var endpoint in config.ProbeEndpoints)
        {
            endpoint.Enabled = false;
        }

        config.ProbeEndpoints[0].Enabled = true;
        config.Probe.PingTargets = new List<string> { "127.0.0.1" };
        config.Probe.RequiredSuccessCount = 99;

        new ConfigValidator().Normalize(config);

        Assert.Equal(1, config.Probe.RequiredSuccessCount);
    }

    [Fact]
    public void NullSections_AreRecreated()
    {
        var config = GuardianConfig.CreateDefault();
        config.Wifi = null!;
        config.Recovery = null!;

        new ConfigValidator().Normalize(config);

        Assert.NotNull(config.Wifi);
        Assert.NotNull(config.Recovery);
    }

    [Fact]
    public void DuplicateCommandIds_AreRegenerated()
    {
        var config = GuardianConfig.CreateDefault();
        config.OfflineCommands.Add(new CommandDefinition { Id = "same", Name = "a" });
        config.OfflineCommands.Add(new CommandDefinition { Id = "same", Name = "b" });

        new ConfigValidator().Normalize(config);

        Assert.Equal(2, config.OfflineCommands.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void MetricManagementPreference_IsPreserved()
    {
        var config = GuardianConfig.CreateDefault();
        config.General.ManageInterfaceMetrics = false;

        new ConfigValidator().Normalize(config);

        Assert.False(config.General.ManageInterfaceMetrics);
    }
}

public sealed class ConfigMigratorTests
{
    [Fact]
    public void OldDocument_IsUpgradedToCurrentVersion()
    {
        var config = new GuardianConfig { Version = 0 };
        config.ProbeEndpoints.Clear();

        var applied = new ConfigMigrator().Migrate(config);

        Assert.Equal(GuardianConfig.CurrentVersion, config.Version);
        Assert.NotEmpty(config.ProbeEndpoints);
        Assert.True(config.Wifi.StickyConnection);
        Assert.True(config.Probe.PreferNpcapRawProbe);
        Assert.True(config.Probe.NpcapRawTcpTargets.Count >= 2);
        Assert.NotEmpty(applied);
    }

    [Fact]
    public void StickyConnection_IsForcedOnForMigratedDocuments()
    {
        var config = new GuardianConfig { Version = 1 };
        config.Wifi.StickyConnection = false;

        new ConfigMigrator().Migrate(config);

        Assert.True(config.Wifi.StickyConnection);
    }

    [Fact]
    public void NewerDocument_IsNotDowngraded()
    {
        var config = new GuardianConfig { Version = GuardianConfig.CurrentVersion + 5 };

        var notes = new ConfigMigrator().Migrate(config);

        Assert.Equal(GuardianConfig.CurrentVersion + 5, config.Version);
        Assert.Contains(notes, n => n.Contains("newer"));
    }
}

public sealed class ConfigJsonTests
{
    [Fact]
    public void RoundTrip_PreservesValues()
    {
        var config = GuardianConfig.CreateDefault();
        config.CampusAuth.Enabled = true;
        config.CampusAuth.ExecutablePath = @"C:\campus\auth.exe";
        config.Probe.PingTimeoutMs = 1750;
        config.Probe.PingTargets = new List<string> { "www.baidu.com", "1.1.1.1" };
        config.Probe.PreferNpcapRawProbe = false;
        config.OfflineCommands.Add(new CommandDefinition
        {
            Id = "c1",
            Name = "portal",
            Kind = CommandKind.Shell,
            ExecutablePath = "curl http://portal/login",
            MaxRunsPerHour = 4,
        });

        var json = ConfigJson.Serialize(config);
        var restored = ConfigJson.Deserialize(json);

        Assert.NotNull(restored);
        Assert.True(restored!.CampusAuth.Enabled);
        Assert.Equal(@"C:\campus\auth.exe", restored.CampusAuth.ExecutablePath);
        Assert.Equal(1750, restored.Probe.PingTimeoutMs);
        Assert.Equal(new[] { "www.baidu.com", "1.1.1.1" }, restored.Probe.PingTargets);
        Assert.False(restored.Probe.PreferNpcapRawProbe);
        var command = Assert.Single(restored.OfflineCommands);
        Assert.Equal(CommandKind.Shell, command.Kind);
        Assert.Equal(4, command.MaxRunsPerHour);
    }

    [Fact]
    public void SerializedConfig_CarriesNoSecretFields()
    {
        // The program never stores or exports a Wi-Fi key; this guards against someone adding one.
        var json = ConfigJson.Serialize(GuardianConfig.CreateDefault()).ToLowerInvariant();

        Assert.DoesNotContain("psk", json);
        Assert.DoesNotContain("password", json);
        Assert.DoesNotContain("keymaterial", json);
        Assert.DoesNotContain("wpa", json);
    }

    [Fact]
    public void PartialDocument_DeserializesWithDefaultsForMissingSections()
    {
        const string partial = """
            { "version": 3, "probe": { "intervalSeconds": 30 } }
            """;

        var config = ConfigJson.Deserialize(partial);
        Assert.NotNull(config);

        // The store always validates after deserializing, which is what restores the missing pieces.
        new ConfigValidator().Normalize(config!);

        Assert.Equal(30, config!.Probe.IntervalSeconds);
        Assert.NotNull(config.Wifi);
        Assert.NotEmpty(config.ProbeEndpoints);
    }
}

public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _root;

    public JsonConfigStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NetworkGuardianTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Test cleanup is best effort.
        }
    }

    private string ConfigPath => Path.Combine(_root, "config.json");

    [Fact]
    public async Task MissingFile_CreatesDefaults()
    {
        var store = new JsonConfigStore(ConfigPath);

        var config = await store.LoadAsync(CancellationToken.None);

        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(GuardianConfig.CurrentVersion, config.Version);
        Assert.NotEmpty(config.ProbeEndpoints);
    }

    [Fact]
    public async Task CorruptFile_IsQuarantinedAndDefaultsAreUsed()
    {
        await File.WriteAllTextAsync(ConfigPath, "{ this is not json ");

        var store = new JsonConfigStore(ConfigPath);
        var config = await store.LoadAsync(CancellationToken.None);

        Assert.True(
            store.RecoveredFromCorruption,
            $"expected corruption recovery; issues: {string.Join(" | ", store.LastLoadIssues)}");
        Assert.Equal(GuardianConfig.CurrentVersion, config.Version);
        Assert.Equal(Path.Combine(_root, "config.invalid.json"), store.CorruptFilePath);
        Assert.True(File.Exists(store.CorruptFilePath));
    }

    [Fact]
    public async Task Backup_IsUsedWhenThePrimaryFileIsUnreadable()
    {
        var store = new JsonConfigStore(ConfigPath);
        var config = await store.LoadAsync(CancellationToken.None);
        config.Wifi.SignalHysteresis = 17;
        await store.SaveAsync(config, CancellationToken.None);

        // A first save created the file; a second save creates the .bak copy through File.Replace.
        var updated = await store.LoadAsync(CancellationToken.None);
        updated.Wifi.SignalHysteresis = 21;
        await store.SaveAsync(updated, CancellationToken.None);

        Assert.True(File.Exists(store.BackupPath));

        await File.WriteAllTextAsync(ConfigPath, "broken");

        var recovered = new JsonConfigStore(ConfigPath);
        var loaded = await recovered.LoadAsync(CancellationToken.None);

        Assert.True(recovered.RecoveredFromBackup);
        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task Save_IsAtomicAndLeavesNoTempFile()
    {
        var store = new JsonConfigStore(ConfigPath);
        var config = await store.LoadAsync(CancellationToken.None);
        config.OfflineCommands.Add(new CommandDefinition { Id = "x", Name = "x" });

        await store.SaveAsync(config, CancellationToken.None);

        Assert.False(File.Exists(ConfigPath + ".tmp"));
        var reloaded = ConfigJson.Deserialize(await File.ReadAllTextAsync(ConfigPath));
        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.OfflineCommands);
    }

    [Fact]
    public async Task Save_PersistsNormalizedValues()
    {
        var store = new JsonConfigStore(ConfigPath);
        var config = GuardianConfig.CreateDefault();
        config.General.HealthSweepSeconds = -5;
        config.General.ManageInterfaceMetrics = false;

        await store.SaveAsync(config, CancellationToken.None);

        var persisted = ConfigJson.Deserialize(await File.ReadAllTextAsync(ConfigPath));
        Assert.NotNull(persisted);
        Assert.Equal(5, persisted!.General.HealthSweepSeconds);
        Assert.False(persisted.General.ManageInterfaceMetrics);
    }

    [Fact]
    public async Task InvalidValuesOnDisk_AreClampedOnLoad()
    {
        var config = GuardianConfig.CreateDefault();
        config.General.HealthSweepSeconds = -5;
        await File.WriteAllTextAsync(ConfigPath, ConfigJson.Serialize(config));

        var store = new JsonConfigStore(ConfigPath);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(5, loaded.General.HealthSweepSeconds);
        Assert.NotEmpty(store.LastLoadIssues);
    }
}

public sealed class GuardianPathsTests
{
    [Fact]
    public void RootOverride_IsHonoured()
    {
        var original = Environment.GetEnvironmentVariable(GuardianPaths.RootOverrideVariable);
        try
        {
            var expected = Path.Combine(Path.GetTempPath(), "ng-root-test");
            Environment.SetEnvironmentVariable(GuardianPaths.RootOverrideVariable, expected);

            Assert.Equal(Path.GetFullPath(expected), GuardianPaths.Root);
            Assert.EndsWith("config.json", GuardianPaths.ConfigFile);
            Assert.EndsWith("Logs", GuardianPaths.LogDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GuardianPaths.RootOverrideVariable, original);
        }
    }
}
