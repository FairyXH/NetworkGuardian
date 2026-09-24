using NetworkGuardian.Core.Policies;
using NetworkGuardian.Core.Models;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class InterfaceProbeStabilityTests
{
    [Fact]
    public void RouteOrderFailsOverQuicklyAndFailsBackOnlyAfterObservation()
    {
        var tracker = new InterfaceProbeStability();
        var now = DateTimeOffset.UtcNow;
        var wifi = TestData.WifiInterface(TestData.AdapterA, probe: TestData.OnlineProbe());
        var wifiAdapters = new[] { TestData.ConnectedAdapter(TestData.AdapterA, "WiFi", "WiFi", 80) };

        var initial = tracker.Apply(TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now);
        AssertEthernetLeads(initial, wifi, wifiAdapters);

        var oneFailure = tracker.Apply(TestData.OfflineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(2));
        AssertEthernetLeads(oneFailure, wifi, wifiAdapters);

        var failed = tracker.Apply(TestData.OfflineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(4));
        AssertWifiLeads(failed, wifi, wifiAdapters);

        var recovering = tracker.Apply(TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(6));
        tracker.Apply(TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(8));
        AssertWifiLeads(recovering, wifi, wifiAdapters);

        var recovered = tracker.Apply(TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(12));
        AssertEthernetLeads(recovered, wifi, wifiAdapters);
    }

    [Fact]
    public void SingleFailureDoesNotDisplaceStableInterface()
    {
        var tracker = new InterfaceProbeStability();
        var now = DateTimeOffset.UtcNow;
        tracker.Apply(TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now);

        var result = tracker.Apply(
            TestData.OfflineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(2));

        Assert.True(result.IsStableOnline);
        Assert.Equal(1, result.ConsecutiveFailures);
    }

    [Fact]
    public void TwoFailuresTriggerFastFailover()
    {
        var tracker = new InterfaceProbeStability();
        var now = DateTimeOffset.UtcNow;
        tracker.Apply(TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now);
        tracker.Apply(TestData.OfflineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(2));

        var result = tracker.Apply(
            TestData.OfflineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(4));

        Assert.False(result.IsStableOnline);
        Assert.Equal(2, result.ConsecutiveFailures);
    }

    [Fact]
    public void RecoveredInterfaceMustPassThresholdAndHoldWindow()
    {
        var tracker = new InterfaceProbeStability();
        var now = DateTimeOffset.UtcNow;
        tracker.Apply(TestData.OfflineProbe(), 2, 2, TimeSpan.FromSeconds(6), now);

        var first = tracker.Apply(
            TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(2));
        var second = tracker.Apply(
            TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(4));
        var held = tracker.Apply(
            TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(8));

        Assert.False(first.IsStableOnline);
        Assert.False(second.IsStableOnline);
        Assert.True(held.IsStableOnline);
        Assert.Equal(3, held.ConsecutiveSuccesses);
    }

    [Fact]
    public void FailureRestartsRecoveryObservationWindow()
    {
        var tracker = new InterfaceProbeStability();
        var now = DateTimeOffset.UtcNow;
        tracker.Apply(TestData.OfflineProbe(), 2, 2, TimeSpan.FromSeconds(6), now);
        tracker.Apply(TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(2));
        tracker.Apply(TestData.OfflineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(4));
        tracker.Apply(TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(6));

        var result = tracker.Apply(
            TestData.OnlineProbe(), 2, 2, TimeSpan.FromSeconds(6), now.AddSeconds(10));

        Assert.False(result.IsStableOnline);
    }

    private static void AssertEthernetLeads(
        ConnectivityProbeReport ethernetProbe,
        InterfaceRuntimeState wifi,
        IReadOnlyList<WifiAdapterRuntimeState> wifiAdapters)
    {
        var ethernet = TestData.EthernetInterface(probe: ethernetProbe);
        var metrics = InterfaceMetricPlanner.Plan(new[] { ethernet, wifi }, wifiAdapters);
        Assert.True(metrics[ethernet.Id] < metrics[wifi.Id]);
    }

    private static void AssertWifiLeads(
        ConnectivityProbeReport ethernetProbe,
        InterfaceRuntimeState wifi,
        IReadOnlyList<WifiAdapterRuntimeState> wifiAdapters)
    {
        var ethernet = TestData.EthernetInterface(probe: ethernetProbe);
        var metrics = InterfaceMetricPlanner.Plan(new[] { ethernet, wifi }, wifiAdapters);
        Assert.True(metrics[wifi.Id] < metrics[ethernet.Id]);
    }
}
