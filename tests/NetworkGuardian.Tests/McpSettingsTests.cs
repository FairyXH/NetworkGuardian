using NetworkGuardian.Core.Configuration;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class McpSettingsTests
{
    [Fact]
    public void DefaultIsDisabledWithAFixedPort()
    {
        var config = ConfigJson.Deserialize("{}")!;
        Assert.False(config.Mcp.Enabled);
        Assert.Equal(18765, config.Mcp.Port);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(65536, 65535)]
    [InlineData(18765, 18765)]
    public void SettingsSurviveSaveAndPortIsValidated(int requested, int expected)
    {
        var config = GuardianConfig.CreateDefault();
        config.Mcp.Enabled = true;
        config.Mcp.Port = requested;
        new ConfigValidator().Normalize(config);
        var saved = ConfigJson.Deserialize(ConfigJson.Serialize(config))!;
        Assert.True(saved.Mcp.Enabled);
        Assert.Equal(expected, saved.Mcp.Port);
        Assert.DoesNotContain("listenAddress", ConfigJson.Serialize(saved), StringComparison.OrdinalIgnoreCase);
    }
}
