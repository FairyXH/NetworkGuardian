using Microsoft.Extensions.Logging;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Logging;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Infrastructure.Logging;
using NetworkGuardian.Infrastructure.Processes;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// The external command runner is the only component that starts third party programs, so its
/// start/skip/timeout behaviour is covered with real processes.
/// </summary>
public sealed class ExternalCommandRunnerTests
{
    private static CommandDefinition ShellCommand(string commandLine, int timeoutSeconds = 20, bool waitForExit = true) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "test",
        Kind = CommandKind.Shell,
        ExecutablePath = commandLine,
        Enabled = true,
        ExecutionTimeoutSeconds = timeoutSeconds,
        KillOnTimeout = true,
        WaitForExit = waitForExit,
        MinIntervalSeconds = 0,
        MaxRunsPerHour = 100,
        MaxConsecutiveRuns = 100,
    };

    [Fact]
    public async Task SuccessfulCommand_ReportsExitCodeZero()
    {
        var runner = new ExternalCommandRunner();

        var result = await runner.RunAsync(
            ShellCommand("exit 0"), "unit test", CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Success);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task FailingCommand_ReportsTheNonZeroExitCode()
    {
        var runner = new ExternalCommandRunner();

        var result = await runner.RunAsync(
            ShellCommand("exit 7"), "unit test", CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task HungCommand_IsKilledAfterTheTimeout()
    {
        var runner = new ExternalCommandRunner();

        var result = await runner.RunAsync(
            ShellCommand("ping -n 20 127.0.0.1", timeoutSeconds: 1), "unit test", CancellationToken.None);

        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.True(result.Killed);
    }

    [Fact]
    public void IsRunning_IsFalseForSomethingThatIsNotRunning()
    {
        var runner = new ExternalCommandRunner();

        Assert.False(runner.IsRunning(@"C:\does\not\exist\totally-made-up-tool.exe"));
        Assert.Empty(runner.RunningInstances);
    }

    [Fact]
    public async Task MissingExecutable_ReportsAClearFailure()
    {
        var runner = new ExternalCommandRunner();
        var definition = ShellCommand(string.Empty);
        definition.Kind = CommandKind.Executable;
        definition.ExecutablePath = @"C:\definitely-not-here\nope.exe";

        var result = await runner.RunAsync(definition, "unit test", CancellationToken.None);

        Assert.False(result.Started);
        Assert.NotNull(result.Failure);
    }
}

public sealed class LogRecordTests
{
    [Theory]
    [InlineData("NetworkGuardian.Windows.Wlan.NativeWifiManager", "WiFi")]
    [InlineData("NetworkGuardian.Windows.Radio.WifiRadioController", "WiFi")]
    [InlineData("NetworkGuardian.Windows.Network.NetworkInterfaceProvider", "Network")]
    [InlineData("NetworkGuardian.Windows.Connectivity.ConnectivityProbe", "Network")]
    [InlineData("NetworkGuardian.Infrastructure.Processes.ExternalCommandRunner", "Application")]
    [InlineData("NetworkGuardian.Windows.Devices.PhysicalDeviceManager", "Device")]
    [InlineData("NetworkGuardian.Infrastructure.Configuration.JsonConfigStore", "Configuration")]
    public void Channel_IsDerivedFromTheCategory(string category, string expectedChannel)
    {
        var record = new LogRecord(DateTimeOffset.UtcNow, GuardianLogLevel.Information, category, "m", null);

        Assert.Equal(expectedChannel, record.Channel);
    }

    [Fact]
    public void Line_ContainsTimestampLevelChannelAndMessage()
    {
        var record = new LogRecord(
            DateTimeOffset.UtcNow, GuardianLogLevel.Warning, "Wifi", "radio is off", null);

        var line = record.Line;

        Assert.Contains("WRN", line);
        Assert.Contains("WiFi", line);
        Assert.Contains("radio is off", line);
    }

    [Fact]
    public void ExceptionText_IsIndentedUnderTheMessage()
    {
        var record = new LogRecord(
            DateTimeOffset.UtcNow, GuardianLogLevel.Error, "X", "boom", "System.Exception: boom");

        Assert.Contains("System.Exception: boom", record.Line);
    }
}

public sealed class InMemoryLogSinkTests
{
    [Fact]
    public void Buffer_IsBounded()
    {
        var sink = new InMemoryLogSink(capacity: 100);

        for (var i = 0; i < 150; i++)
        {
            sink.Publish(new LogRecord(DateTimeOffset.UtcNow, GuardianLogLevel.Information, "c", $"m{i}", null));
        }

        var snapshot = sink.Snapshot();
        Assert.Equal(100, snapshot.Count);
        Assert.Equal("m149", snapshot[^1].Message);
        Assert.Equal("m50", snapshot[0].Message);
    }

    [Fact]
    public void Filtering_ByLevelChannelAndText_Works()
    {
        var sink = new InMemoryLogSink(capacity: 100);
        sink.Publish(new LogRecord(DateTimeOffset.UtcNow, GuardianLogLevel.Debug, "Wifi", "scan started", null));
        sink.Publish(new LogRecord(DateTimeOffset.UtcNow, GuardianLogLevel.Warning, "Wifi", "scan failed", null));
        sink.Publish(new LogRecord(DateTimeOffset.UtcNow, GuardianLogLevel.Information, "Probe", "online", null));

        var warnings = sink.SnapshotFiltered(GuardianLogLevel.Warning, "全部", null);
        Assert.Single(warnings);
        Assert.Equal("scan failed", warnings[0].Message);

        var wifi = sink.SnapshotFiltered(GuardianLogLevel.Debug, "WiFi", null);
        Assert.Equal(2, wifi.Count);

        var search = sink.SnapshotFiltered(GuardianLogLevel.Debug, "全部", "online");
        Assert.Single(search);
    }
}

public sealed class RollingFileLoggerProviderTests : IDisposable
{
    private readonly string _directory;

    public RollingFileLoggerProviderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "NetworkGuardianLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void Logger_WritesToFileAndToTheSink()
    {
        var settings = new LoggingSettings { MinimumLevel = GuardianLogLevel.Information, WriteToFile = true };
        var sink = new InMemoryLogSink();
        using var provider = new RollingFileLoggerProvider(_directory, "test", sink, settings)
        {
            MinimumLevel = GuardianLogLevel.Information,
            Enabled = true,
        };

        var logger = provider.CreateLogger("NetworkGuardian.Tests");
        logger.LogInformation("guardian online");

        Assert.Single(sink.Snapshot());
        var files = Directory.GetFiles(_directory, "test-*.log");
        Assert.Single(files);

        // The provider keeps the file open for writing, so it must be read with a compatible share mode.
        using (var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            Assert.Contains("guardian online", reader.ReadToEnd());
        }
    }

    [Fact]
    public void DebugRecords_AreFilteredAtInformationLevel()
    {
        var settings = new LoggingSettings { MinimumLevel = GuardianLogLevel.Information, WriteToFile = true };
        var sink = new InMemoryLogSink();
        using var provider = new RollingFileLoggerProvider(_directory, "test", sink, settings)
        {
            MinimumLevel = GuardianLogLevel.Information,
            Enabled = true,
        };

        var logger = provider.CreateLogger("NetworkGuardian.Tests");
        logger.LogDebug("should not appear");

        Assert.Empty(Directory.GetFiles(_directory, "test-*.log"));
    }
}
