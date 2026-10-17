using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Helper;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class WfpConfigurationTests
{
    [Fact]
    public void RestartAndWfpAreMutuallyExclusiveAndLeaseIsBounded()
    {
        var config = new GuardianConfig();
        config.ConnectionMigration.RestartOldAdapterOnSwitch = true;
        config.ConnectionMigration.WfpBlockOnSwitch = true;
        config.ConnectionMigration.WfpBlockSeconds = 200;
        config.ConnectionMigration.WfpMaxBlockSeconds = -1;
        new ConfigValidator().Normalize(config);
        Assert.False(config.ConnectionMigration.WfpBlockOnSwitch);
        Assert.True(config.ConnectionMigration.RestartOldAdapterOnSwitch);
        Assert.Equal(30, config.ConnectionMigration.WfpBlockSeconds);
        Assert.Equal(30, config.ConnectionMigration.WfpMaxBlockSeconds);
    }

    [Fact]
    public void WfpLeaseContractRoundTripsAndOldSettingsDoNotEnableIt()
    {
        var original = new HelperRequest
        {
            Operation = HelperOperations.BlockStaleOutlet,
            OwnerProcessStartTimeUtcTicks = DateTime.UtcNow.Ticks,
            OldLocalAddresses = new[] { "192.0.2.1", "2001:db8::1" },
            ConnectionMigration = new() { WfpBlockOnSwitch = true, WfpBlockSeconds = 3, WfpMaxBlockSeconds = 10 },
        };
        var parsed = HelperProtocol.DeserializeRequest(HelperProtocol.SerializeRequest(original))!;
        Assert.Equal(original.OwnerProcessStartTimeUtcTicks, parsed.OwnerProcessStartTimeUtcTicks);
        Assert.Equal(original.OldLocalAddresses, parsed.OldLocalAddresses);
        Assert.True(parsed.ConnectionMigration!.WfpBlockOnSwitch);
        Assert.Equal(3, parsed.ConnectionMigration.WfpBlockSeconds);
        Assert.Equal(10, parsed.ConnectionMigration.WfpMaxBlockSeconds);
        Assert.False(new ConnectionMigrationSettings().WfpBlockOnSwitch);
    }
}
