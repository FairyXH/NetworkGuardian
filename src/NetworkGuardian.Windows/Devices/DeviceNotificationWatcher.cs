using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Windows.Native;
using static NetworkGuardian.Windows.Native.CfgMgr32Native;
using static NetworkGuardian.Windows.Native.SetupApiNative;

namespace NetworkGuardian.Windows.Devices;

public sealed record DeviceChangeEvent
{
    public required string Action { get; init; }

    public required string Description { get; init; }

    public string? InstanceId { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    public override string ToString() => $"{Action}: {Description}";
}

/// <summary>
/// PnP device notification watcher. Uses <c>CM_Register_Notification</c> for the network class so
/// USB Wi-Fi hot-plug, driver restarts and device removals are noticed immediately instead of
/// waiting for the next enumeration sweep.
/// </summary>
public sealed class DeviceNotificationWatcher : IDisposable
{
    private readonly ILogger<DeviceNotificationWatcher> _logger;
    private readonly ConcurrentQueue<DeviceChangeEvent> _pending = new();
    private readonly SemaphoreSlim _signal = new(0, 1024);
    private readonly CmNotifyCallback _callback;
    private readonly GCHandle _selfHandle;

    private IntPtr _notificationHandle;
    private IntPtr _filterPointer;
    private bool _registered;
    private bool _disposed;

    public DeviceNotificationWatcher(ILogger<DeviceNotificationWatcher>? logger = null)
    {
        _logger = logger ?? NullLogger<DeviceNotificationWatcher>.Instance;
        _callback = OnNotification;
        _selfHandle = GCHandle.Alloc(this);
    }

    public bool IsRegistered => _registered;

    public bool TryStart()
    {
        if (_registered)
        {
            return true;
        }

        try
        {
            var filter = new CM_NOTIFY_FILTER
            {
                cbSize = 416,
                FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
                // DEVICEINTERFACE filters require an interface-class GUID. GUID_DEVCLASS_NET is a
                // setup-class GUID and registers successfully but does not deliver adapter arrivals.
                ClassGuid = GuidDevInterfaceNet,
            };

            var size = Marshal.SizeOf<CM_NOTIFY_FILTER>();
            _filterPointer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(filter, _filterPointer, false);

            var status = CM_Register_Notification(
                _filterPointer,
                GCHandle.ToIntPtr(_selfHandle),
                _callback,
                out _notificationHandle);

            if (status != CR_SUCCESS)
            {
                _logger.LogWarning(
                    "CM_Register_Notification failed ({Status}); device changes will be detected by the " +
                    "periodic enumeration sweep only.",
                    Describe(status));
                FreeFilter();
                return false;
            }

            _registered = true;
            _logger.LogInformation("Registered PnP device notifications for the network interface class");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register device notifications");
            FreeFilter();
            return false;
        }
    }

    /// <summary>Waits for a device change or the timeout, whichever comes first.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);

    /// <summary>
    /// Pumps queued device change events on the caller's thread. Returns true when at least one
    /// event indicated that the device tree changed and an enumeration refresh is worthwhile.
    /// </summary>
    public bool Drain(out IReadOnlyList<DeviceChangeEvent> events)
    {
        var drained = new List<DeviceChangeEvent>();
        var interesting = false;

        while (_pending.TryDequeue(out var change))
        {
            drained.Add(change);
            switch (change.Action)
            {
                case "interface-arrival":
                case "interface-removal":
                case "device-removed":
                case "device-started":
                case "device-enumerated":
                case "remove-complete":
                    interesting = true;
                    break;
            }
        }

        events = drained;
        return interesting;
    }

    private uint OnNotification(IntPtr context, uint action, IntPtr eventData, IntPtr eventDataSize)
    {
        // Invoked on a system thread. Never throw, never block, never call back into CfgMgr32.
        try
        {
            var (name, description) = DescribeAction(action);
            var instanceId = TryReadInstanceId(action, eventData, eventDataSize);
            var change = new DeviceChangeEvent
            {
                Action = name,
                Description = description,
                InstanceId = instanceId,
                TimestampUtc = DateTimeOffset.UtcNow,
            };

            _pending.Enqueue(change);

            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // The consumer will drain the queue even without a fresh signal.
            }
        }
        catch (Exception)
        {
            // Swallowing here is deliberate: exceptions must not cross the native callback boundary.
        }

        return 0;
    }

    private static string? TryReadInstanceId(uint action, IntPtr eventData, IntPtr eventDataSize)
    {
        if (eventData == IntPtr.Zero || eventDataSize == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            switch (action)
            {
                case CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED:
                case CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED:
                case CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED:
                {
                    var info = Marshal.PtrToStructure<CM_NOTIFY_EVENT_DATA>(eventData);
                    unsafe
                    {
                        var pointer = info.InstanceId;
                        return pointer is null ? null : new string(pointer).TrimEnd('\0');
                    }
                }

                default:
                    return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CM_NOTIFY_EVENT_DATA
    {
        public uint cbSize;
        public uint FilterType;
        public uint Reserved;
        public Guid ClassGuid;
        public fixed char InstanceId[MAX_DEVICE_ID_LEN];
    }

    private static (string Name, string Description) DescribeAction(uint action) => action switch
    {
        CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL => ("interface-arrival", "a device interface arrived"),
        CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL => ("interface-removal", "a device interface was removed"),
        CM_NOTIFY_ACTION_DEVICEQUERYREMOVE => ("query-remove", "a device is about to be removed"),
        CM_NOTIFY_ACTION_DEVICEQUERYREMOVEFAILED => ("query-remove-failed", "a pending removal was cancelled"),
        CM_NOTIFY_ACTION_DEVICEREMOVEPENDING => ("remove-pending", "a device is being removed"),
        CM_NOTIFY_ACTION_DEVICEREMOVECOMPLETE => ("remove-complete", "a device was removed"),
        CM_NOTIFY_ACTION_DEVICECUSTOMEVENT => ("custom-event", "a device reported a custom event"),
        CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED => ("device-enumerated", "a device instance was enumerated"),
        CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED => ("device-started", "a device instance started"),
        CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED => ("device-removed", "a device instance was removed"),
        _ => ($"action-{action}", $"unmapped device notification action {action}"),
    };

    private void FreeFilter()
    {
        if (_filterPointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_filterPointer);
            _filterPointer = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_registered && _notificationHandle != IntPtr.Zero)
        {
            var status = CM_Unregister_Notification(_notificationHandle);
            if (status != CR_SUCCESS)
            {
                _logger.LogWarning("CM_Unregister_Notification returned {Status}", Describe(status));
            }

            _notificationHandle = IntPtr.Zero;
            _registered = false;
        }

        FreeFilter();
        _signal.Dispose();

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        GC.KeepAlive(_callback);
    }
}
