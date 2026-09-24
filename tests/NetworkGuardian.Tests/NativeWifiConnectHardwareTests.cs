using NetworkGuardian.Windows.Wlan;
using Xunit;
using Xunit.Abstractions;

namespace NetworkGuardian.Tests;

[Collection(WifiHardwareCollection.Name)]
public sealed class NativeWifiConnectHardwareTests
{
    private const string ProfileVariable = "NETWORKGUARDIAN_WIFI_CONNECT_PROFILE";
    private readonly ITestOutputHelper _output;

    public NativeWifiConnectHardwareTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ExistingProfile_ReachesAStableConnectionThroughWlanApi()
    {
        var profileName = Environment.GetEnvironmentVariable(ProfileVariable);
        if (string.IsNullOrWhiteSpace(profileName))
        {
            _output.WriteLine($"skipped: set {ProfileVariable} to an existing profile name");
            return;
        }

        using var wifi = new NativeWifiManager();
        var adapter = wifi.GetAdapters()
            .FirstOrDefault(candidate => wifi.GetProfileNames(candidate.InterfaceGuid)
                .Contains(profileName, StringComparer.OrdinalIgnoreCase));

        Assert.NotNull(adapter);
        _output.WriteLine($"adapter: {adapter!.Description} {adapter.InterfaceGuid:D}");

        var result = await wifi.ConnectAsync(adapter.InterfaceGuid, profileName, null, CancellationToken.None);
        _output.WriteLine($"success={result.Success} failure={result.Failure ?? "-"}");

        Assert.True(result.Success, result.Failure);
        var connection = wifi.GetConnection(adapter.InterfaceGuid);
        Assert.NotNull(connection);
        Assert.True(connection!.IsConnected);
        Assert.Equal(profileName, connection.ProfileName, ignoreCase: true);
    }
}
