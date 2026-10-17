using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Helper;
using NetworkGuardian.Windows.Devices;

namespace NetworkGuardian.Windows.Network;

/// <summary>Serializes short-lived elevated leases without blocking route monitoring.</summary>
public sealed class WfpConnectionMigrator : IAsyncDisposable
{
    private readonly ILogger<WfpConnectionMigrator> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private Task _tail = Task.CompletedTask;
    private DateTimeOffset _retryAfter;
    private bool _disposed;

    public WfpConnectionMigrator(ILogger<WfpConnectionMigrator> logger) => _logger = logger;

    public void Start(string deviceId, IReadOnlyCollection<string> oldAddresses,
        ConnectionMigrationSettings settings, CancellationToken cancellationToken)
    {
        var addresses = oldAddresses.Where(a => WfpOutletLease.IsEligibleAddress(a, out _)).Distinct().Take(32).ToArray();
        lock (_gate)
        {
            _current?.Cancel();
            if (_disposed || !settings.WfpBlockOnSwitch || settings.RestartOldAdapterOnSwitch ||
                addresses.Length == 0 || DateTimeOffset.UtcNow < _retryAfter) return;
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _current = source;
            var previous = _tail;
            var copy = new ConnectionMigrationSettings { WfpBlockOnSwitch = true,
                WfpBlockSeconds = Math.Clamp(settings.WfpBlockSeconds, 1, 30),
                WfpMaxBlockSeconds = Math.Clamp(settings.WfpMaxBlockSeconds, Math.Clamp(settings.WfpBlockSeconds, 1, 30), 120) };
            _tail = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    source.Token.ThrowIfCancellationRequested();
                    using var owner = Process.GetCurrentProcess();
                    var response = await new HelperClient().SendAsync(new HelperRequest
                    {
                        Operation = HelperOperations.BlockStaleOutlet, DeviceInstanceId = deviceId,
                        OldLocalAddresses = addresses, ConnectionMigration = copy,
                        RequestedBy = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        OwnerProcessStartTimeUtcTicks = owner.StartTime.ToUniversalTime().Ticks,
                    }, TimeSpan.FromSeconds(copy.WfpMaxBlockSeconds + 15), source.Token).ConfigureAwait(false);
                    if (source.IsCancellationRequested) return;
                    if (!response.Success)
                    {
                        lock (_gate) _retryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
                        _logger.LogWarning("WFP migration failed; retry deferred five minutes: {Detail}", response.Detail ?? response.Message);
                    }
                    else
                    {
                        _logger.LogInformation("WFP migration complete: {Detail}", response.Detail);
                        if (response.Detail?.Contains("maximum lease reached", StringComparison.Ordinal) == true)
                            _logger.LogWarning("WFP lease reached its safety limit; filters removed even though observed endpoints may remain");
                    }
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested) { }
                catch (Exception ex) { _logger.LogWarning(ex, "WFP migration failed"); }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_current, source)) _current = null;
                        source.Dispose();
                    }
                }
            }, CancellationToken.None);
        }
    }

    public void Cancel()
    {
        lock (_gate) _current?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate)
        {
            _disposed = true;
            _current?.Cancel();
            pending = _tail;
        }
        // A pending UAC dialog must not hold the main process open. The helper also watches owner exit.
        await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
    }
}
