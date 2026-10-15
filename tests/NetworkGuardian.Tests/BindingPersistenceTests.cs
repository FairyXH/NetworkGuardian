using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class BindingPersistenceTests
{
    [Fact]
    public void StaleSettingsSavePreservesBindingAndUnrelatedFields()
    {
        var baseline = TestData.Config();
        var latest = ConfigJson.Deserialize(ConfigJson.Serialize(baseline))!;
        latest.Wifi.CampusWifiAdapterAssignments["Home"] = TestData.AdapterA.ToString();
        latest.Wifi.CampusNetworkSsids.Add("Campus");
        var edited = ConfigJson.Deserialize(ConfigJson.Serialize(baseline))!;
        edited.General.HealthSweepSeconds = 60;
        var merged = ConfigMerge.Apply(latest, baseline, edited);
        Assert.Equal(TestData.AdapterA, WifiBindingPolicy.Owner(merged.Wifi, "Home"));
        Assert.Contains("Campus", merged.Wifi.CampusNetworkSsids);
        Assert.Equal(60, merged.General.HealthSweepSeconds);
    }

    [Fact]
    public void BindingIsIndependentOfCampusAndSurvivesSerialization()
    {
        var config = TestData.Config();
        config.Wifi.CampusWifiAdapterAssignments["Home"] = TestData.AdapterA.ToString();
        config = ConfigJson.Deserialize(ConfigJson.Serialize(config))!;
        Assert.Empty(config.Wifi.CampusNetworkSsids);
        Assert.True(WifiBindingPolicy.Allows(config.Wifi, "Home", TestData.AdapterA));
        Assert.False(WifiBindingPolicy.Allows(config.Wifi, "Home", TestData.AdapterB));
    }

    [Fact]
    public void DictionaryEditsMergePerSsidIncludingDeletion()
    {
        var baseline = TestData.Config(c => c.Wifi.CampusWifiAdapterAssignments["Home"] = TestData.AdapterA.ToString());
        var latest = ConfigJson.Deserialize(ConfigJson.Serialize(baseline))!;
        latest.Wifi.CampusWifiAdapterAssignments["Other"] = TestData.AdapterB.ToString();
        var edited = ConfigJson.Deserialize(ConfigJson.Serialize(baseline))!;
        edited.Wifi.CampusWifiAdapterAssignments.Remove("Home");
        var merged = ConfigMerge.Apply(latest, baseline, edited);
        Assert.Null(WifiBindingPolicy.Owner(merged.Wifi, "Home"));
        Assert.Equal(TestData.AdapterB, WifiBindingPolicy.Owner(merged.Wifi, "Other"));
    }
}
