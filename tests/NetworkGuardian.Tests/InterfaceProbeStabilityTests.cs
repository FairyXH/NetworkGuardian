using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class InterfaceProbeStabilityTests
{
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
}
