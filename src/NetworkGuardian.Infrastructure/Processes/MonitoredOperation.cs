namespace NetworkGuardian.Infrastructure.Processes;

/// <summary>Keeps health checks advancing while a bounded recovery operation is in flight.</summary>
public static class MonitoredOperation
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> operation,
        Func<CancellationToken, Task> observe,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Process.Start and native operations can block before their first asynchronous wait.
        var pending = Task.Run(() => operation(lifetime.Token), lifetime.Token);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (!pending.IsCompleted)
            {
                var tick = timer.WaitForNextTickAsync(lifetime.Token).AsTask();
                if (await Task.WhenAny(pending, tick).ConfigureAwait(false) == pending) break;
                if (!await tick.ConfigureAwait(false)) break;
                if (!pending.IsCompleted) await observe(lifetime.Token).ConfigureAwait(false);
            }
            await pending.ConfigureAwait(false);
        }
        catch
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await pending.ConfigureAwait(false); }
            catch { /* Preserve the observer/cancellation failure after draining the operation. */ }
            throw;
        }
    }
}
