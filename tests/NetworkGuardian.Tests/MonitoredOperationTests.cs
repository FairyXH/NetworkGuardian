using NetworkGuardian.Infrastructure.Processes;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class MonitoredOperationTests
{
    [Fact]
    public async Task ObservationsContinueUntilRecoveryCompletes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        await MonitoredOperation.RunAsync(
            token => release.Task.WaitAsync(token),
            _ =>
            {
                if (++observations == 3) release.SetResult();
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(10), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, observations);
    }

    [Fact]
    public async Task ObserverFailureCancelsAndDrainsRecovery()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => MonitoredOperation.RunAsync(
            async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { drained.SetResult(); }
            }, _ => throw new InvalidOperationException("probe failed"),
            TimeSpan.FromMilliseconds(10), CancellationToken.None));
        Assert.Equal("probe failed", failure.Message);
        Assert.True(drained.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RecoveryFailureIsPropagated()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => MonitoredOperation.RunAsync(
            _ => throw new InvalidOperationException("recovery failed"),
            _ => Task.CompletedTask, TimeSpan.FromSeconds(1), CancellationToken.None));
    }
}
