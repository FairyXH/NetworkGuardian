using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Policies;

/// <summary>
/// Explicit, thread-safe recovery state machine. The host drives it from one background loop, but
/// the UI and manual commands may also request transitions, so every mutation is guarded.
/// </summary>
public sealed class RecoveryStateMachine
{
    private readonly object _gate = new();
    private readonly List<StateTransition> _history = new();
    private readonly int _historyLimit;

    public RecoveryStateMachine(DateTimeOffset now, int historyLimit = 200)
    {
        Current = RecoveryState.Initializing;
        LastChangedUtc = now;
        EnteredAtUtc = now;
        _historyLimit = Math.Max(10, historyLimit);
        _history.Add(new StateTransition(RecoveryState.Initializing, now, "process start"));
    }

    public RecoveryState Current { get; private set; }

    public DateTimeOffset LastChangedUtc { get; private set; }

    public DateTimeOffset EnteredAtUtc { get; private set; }

    public string? LastReason { get; private set; }

    public event EventHandler<StateTransition>? StateChanged;

    /// <summary>Universal escape hatches: these may be entered from any state.</summary>
    private static readonly HashSet<RecoveryState> UniversalTargets = new()
    {
        RecoveryState.Paused,
        RecoveryState.Error,
        RecoveryState.Initializing,
        RecoveryState.Cooldown,
        RecoveryState.Recovering,
        RecoveryState.VerifyingInternet,
    };

    private static readonly Dictionary<RecoveryState, HashSet<RecoveryState>> Allowed = new()
    {
        [RecoveryState.Initializing] = new()
        {
            RecoveryState.Healthy, RecoveryState.Degraded, RecoveryState.WifiRadioOff,
            RecoveryState.EnablingWifiRadio, RecoveryState.EnablingWifiDevices,
            RecoveryState.WifiScanning, RecoveryState.WifiConnecting, RecoveryState.WaitingForDhcp,
        },
        [RecoveryState.Healthy] = new()
        {
            RecoveryState.Degraded, RecoveryState.EthernetNoInternet, RecoveryState.WifiRadioOff,
            RecoveryState.WifiScanning, RecoveryState.WifiConnecting, RecoveryState.WaitingForDhcp,
        },
        [RecoveryState.Degraded] = new()
        {
            RecoveryState.Healthy, RecoveryState.EthernetNoInternet, RecoveryState.WifiRadioOff,
            RecoveryState.EnablingWifiRadio, RecoveryState.EnablingWifiDevices, RecoveryState.WifiScanning,
            RecoveryState.WifiConnecting, RecoveryState.WaitingForDhcp, RecoveryState.Authenticating,
            RecoveryState.WaitingForAuthentication,
        },
        [RecoveryState.EthernetNoInternet] = new()
        {
            RecoveryState.Healthy, RecoveryState.Degraded, RecoveryState.Authenticating,
            RecoveryState.WaitingForAuthentication, RecoveryState.WifiScanning, RecoveryState.Cooldown,
        },
        [RecoveryState.Authenticating] = new()
        {
            RecoveryState.WaitingForAuthentication, RecoveryState.Healthy, RecoveryState.Degraded,
            RecoveryState.Cooldown,
        },
        [RecoveryState.WaitingForAuthentication] = new()
        {
            RecoveryState.Healthy, RecoveryState.Degraded, RecoveryState.EthernetNoInternet,
            RecoveryState.Authenticating, RecoveryState.Cooldown, RecoveryState.VerifyingInternet,
        },
        [RecoveryState.WifiRadioOff] = new()
        {
            RecoveryState.EnablingWifiRadio, RecoveryState.Degraded, RecoveryState.Healthy,
            RecoveryState.EnablingWifiDevices, RecoveryState.WifiScanning,
        },
        [RecoveryState.EnablingWifiRadio] = new()
        {
            RecoveryState.EnablingWifiDevices, RecoveryState.WifiScanning, RecoveryState.Degraded,
            RecoveryState.Healthy, RecoveryState.WifiRadioOff,
        },
        [RecoveryState.EnablingWifiDevices] = new()
        {
            RecoveryState.WifiScanning, RecoveryState.Degraded, RecoveryState.Healthy,
            RecoveryState.WifiConnecting, RecoveryState.WifiRadioOff,
        },
        [RecoveryState.WifiScanning] = new()
        {
            RecoveryState.WifiConnecting, RecoveryState.Healthy, RecoveryState.Degraded,
            RecoveryState.WaitingForDhcp, RecoveryState.Cooldown,
        },
        [RecoveryState.WifiConnecting] = new()
        {
            RecoveryState.WaitingForDhcp, RecoveryState.WifiScanning, RecoveryState.Healthy,
            RecoveryState.Degraded, RecoveryState.Cooldown,
        },
        [RecoveryState.WaitingForDhcp] = new()
        {
            RecoveryState.VerifyingInternet, RecoveryState.Healthy, RecoveryState.Degraded,
            RecoveryState.WifiConnecting, RecoveryState.Cooldown,
        },
        [RecoveryState.VerifyingInternet] = new()
        {
            RecoveryState.Healthy, RecoveryState.Degraded, RecoveryState.WifiScanning,
            RecoveryState.WifiConnecting, RecoveryState.EnablingWifiDevices, RecoveryState.Cooldown,
        },
        [RecoveryState.Recovering] = new()
        {
            RecoveryState.Healthy, RecoveryState.Degraded, RecoveryState.WifiScanning,
            RecoveryState.WifiConnecting, RecoveryState.EnablingWifiRadio,
            RecoveryState.EnablingWifiDevices, RecoveryState.Authenticating,
            RecoveryState.WaitingForAuthentication, RecoveryState.WaitingForDhcp,
        },
        [RecoveryState.Cooldown] = new()
        {
            RecoveryState.Healthy, RecoveryState.Degraded, RecoveryState.WifiScanning,
            RecoveryState.WifiConnecting, RecoveryState.Authenticating, RecoveryState.WifiRadioOff,
        },
        [RecoveryState.Paused] = new()
        {
            RecoveryState.Healthy, RecoveryState.Degraded, RecoveryState.Initializing,
            RecoveryState.VerifyingInternet,
        },
        [RecoveryState.Error] = new()
        {
            RecoveryState.Healthy, RecoveryState.Degraded, RecoveryState.Initializing,
            RecoveryState.Cooldown,
        },
    };

    public bool IsTransitionAllowed(RecoveryState next)
    {
        lock (_gate)
        {
            if (next == Current)
            {
                return true;
            }

            if (UniversalTargets.Contains(next))
            {
                return true;
            }

            return Allowed.TryGetValue(Current, out var set) && set.Contains(next);
        }
    }

    /// <summary>
    /// Requests a transition. Returns true when the state actually changed. Illegal transitions are
    /// rejected (and logged by the caller) unless <paramref name="force"/> is set.
    /// </summary>
    public bool Transition(RecoveryState next, DateTimeOffset now, string reason, bool force = false)
    {
        StateTransition? change = null;

        lock (_gate)
        {
            if (next == Current)
            {
                LastReason = reason;
                return false;
            }

            var allowed = UniversalTargets.Contains(next) ||
                          (Allowed.TryGetValue(Current, out var set) && set.Contains(next));

            if (!allowed && !force)
            {
                return false;
            }

            var previous = Current;
            Current = next;
            LastChangedUtc = now;
            EnteredAtUtc = now;
            LastReason = reason;
            change = new StateTransition(next, now, reason, previous, force && !allowed);

            _history.Add(change);
            if (_history.Count > _historyLimit)
            {
                _history.RemoveAt(0);
            }
        }

        StateChanged?.Invoke(this, change);
        return true;
    }

    public TimeSpan TimeInState(DateTimeOffset now) => now - EnteredAtUtc;

    public IReadOnlyList<StateTransition> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToArray();
            }
        }
    }

    public override string ToString() => $"{Current} since {EnteredAtUtc:HH:mm:ss} ({LastReason})";
}

public sealed record StateTransition(
    RecoveryState State,
    DateTimeOffset AtUtc,
    string Reason,
    RecoveryState? From = null,
    bool Forced = false);
