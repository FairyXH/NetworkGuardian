using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Native;
using static NetworkGuardian.Windows.Native.WlanApiNative;

namespace NetworkGuardian.Windows.Wlan;

/// <summary>
/// Reliable wrapper over the Native Wi-Fi API. Owns the WLAN client handle, the notification
/// registration, per-adapter scan state and the memory returned by the WLAN service.
/// </summary>
public sealed class NativeWifiManager : INativeWifiService
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan DefaultDisconnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<NativeWifiManager> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, ScanState> _scanStates = new();
    private readonly ConcurrentQueue<WlanNotificationEvent> _notifications = new();
    private readonly SemaphoreSlim _notificationSignal = new(0, 4096);
    private readonly WlanNotificationCallback _callback;
    private readonly GCHandle _selfHandle;
    private readonly List<WifiAdapterInfo> _adapters = new();

    private WlanClientHandle? _client;
    private uint _negotiatedVersion;
    private bool _disposed;
    private bool _notificationRegistered;
    private LocationPermissionSnapshot _locationPermission = new() { ObservedAtUtc = DateTimeOffset.UtcNow };

    public NativeWifiManager(ILogger<NativeWifiManager>? logger = null)
    {
        _logger = logger ?? NullLogger<NativeWifiManager>.Instance;
        _callback = OnNotification;
        _selfHandle = GCHandle.Alloc(this);
        Open();
    }

    public event EventHandler<WlanNotificationEvent>? NotificationReceived;

    public LocationPermissionSnapshot LocationPermission
    {
        get
        {
            lock (_gate)
            {
                return _locationPermission;
            }
        }
    }

    /// <summary>True when the WLAN service handle is usable.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _client is { IsInvalid: false, IsClosed: false };
            }
        }
    }

    /// <summary>Number of notifications waiting to be drained.</summary>
    public int PendingNotificationCount => _notifications.Count;

    private void Open()
    {
        uint negotiated;
        var result = WlanOpenHandle(WlanClientVersionWindows7, IntPtr.Zero, out negotiated, out var handle);
        if (result != ERROR_SUCCESS)
        {
            _logger.LogError("WlanOpenHandle failed: {Error}", Win32Error.Describe((int)result));
            return;
        }

        lock (_gate)
        {
            _client = new WlanClientHandle(handle);
            _negotiatedVersion = negotiated;
        }

        _logger.LogInformation("WLAN client handle opened (negotiated version {Version})", negotiated);

        result = WlanRegisterNotification(
            handle,
            WLAN_NOTIFICATION_SOURCE_ACM | WLAN_NOTIFICATION_SOURCE_MSM,
            1,
            _callback,
            GCHandle.ToIntPtr(_selfHandle),
            IntPtr.Zero,
            out _);

        if (result != ERROR_SUCCESS)
        {
            _logger.LogWarning("WlanRegisterNotification failed: {Error}; falling back to polling only",
                Win32Error.Describe((int)result));
        }
        else
        {
            _notificationRegistered = true;
            _logger.LogInformation("Registered WLAN notifications for ACM and MSM sources");
        }

        RefreshAdapters();
    }

    public int RefreshAdapters()
    {
        IntPtr listPointer = IntPtr.Zero;
        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var result = WlanEnumInterfaces(handle, IntPtr.Zero, out listPointer);
            if (result != ERROR_SUCCESS)
            {
                _logger.LogWarning("WlanEnumInterfaces failed: {Error}", Win32Error.Describe((int)result));
                return 0;
            }

            var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST_HEADER>(listPointer);
            var itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
            var discovered = new List<WifiAdapterInfo>((int)header.dwNumberOfItems);

            for (var i = 0; i < header.dwNumberOfItems; i++)
            {
                var itemPointer = IntPtr.Add(listPointer, ListHeaderSize + (i * itemSize));
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(itemPointer);

                string description;
                unsafe
                {
                    // 'info' is a local copy, so the fixed size buffer is already pinned and its
                    // member access yields a pointer without an extra fixed statement.
                    var descriptionPointer = info.strInterfaceDescription;
                    description = descriptionPointer is null
                        ? string.Empty
                        : new string(descriptionPointer).Trim();
                }

                discovered.Add(new WifiAdapterInfo
                {
                    InterfaceGuid = info.InterfaceGuid,
                    Description = description,
                    State = WifiMapping.MapInterfaceState(info.isState),
                });
            }

            lock (_gate)
            {
                var previous = _adapters
                    .Select(a => a.InterfaceGuid)
                    .ToHashSet();

                _adapters.Clear();
                _adapters.AddRange(discovered);

                foreach (var adapter in discovered.Where(a => !previous.Contains(a.InterfaceGuid)))
                {
                    _logger.LogInformation("WLAN interface discovered: {Description} ({Guid})",
                        adapter.Description, adapter.InterfaceGuid);
                }

                foreach (var gone in previous.Where(g => discovered.All(a => a.InterfaceGuid != g)))
                {
                    _logger.LogInformation("WLAN interface removed: {Guid}", gone);
                    _scanStates.TryRemove(gone, out _);
                }
            }

            return discovered.Count;
        }
        finally
        {
            if (listPointer != IntPtr.Zero)
            {
                WlanFreeMemory(listPointer);
            }
        }
    }

    public IReadOnlyList<WifiAdapterInfo> GetAdapters()
    {
        lock (_gate)
        {
            return _adapters.ToList();
        }
    }

    public bool IsScanInProgress(Guid interfaceGuid) =>
        _scanStates.TryGetValue(interfaceGuid, out var state) && state.Waiter is not null;

    public AdapterScanSnapshot? GetLastScan(Guid interfaceGuid) =>
        _scanStates.TryGetValue(interfaceGuid, out var state) ? state.LastSnapshot : null;

    public async Task<AdapterScanSnapshot> RequestScanAsync(
        Guid interfaceGuid,
        bool force,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var state = _scanStates.GetOrAdd(interfaceGuid, _ => new ScanState());
        var now = DateTimeOffset.UtcNow;
        var started = now;

        // Exactly one in-flight scan per adapter: the WLAN service rejects overlapping requests.
        var acquired = await state.Gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            var inFlight = state.LastSnapshot ?? AdapterScanSnapshot.Empty(interfaceGuid, now);
            _logger.LogWarning("Scan on {Adapter} skipped: another scan is still running", interfaceGuid);
            return inFlight with { FailureReason = "another scan was already in progress" };
        }

        try
        {
            var handle = CurrentHandle();
            if (handle == IntPtr.Zero)
            {
                return Fail(interfaceGuid, started, "WLAN client handle is not open");
            }

            if (!force && state.LastSnapshot is { Completed: true } cached &&
                now - cached.CompletedAtUtc!.Value < TimeSpan.FromSeconds(10))
            {
                return cached;
            }

            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Waiter = waiter;
            state.LastScanStartedUtc = started;

            var result = WlanScan(handle, interfaceGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (result != ERROR_SUCCESS)
            {
                state.Waiter = null;
                return HandleScanFailure(interfaceGuid, started, result, "WlanScan");
            }

            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(waiter.Task, timeoutTask).ConfigureAwait(false);

            state.Waiter = null;

            if (completed == timeoutTask)
            {
                // The driver never signalled completion. Results may still be partially updated,
                // but we must not hang: report a failure and let the caller retry with backoff.
                _logger.LogWarning("Scan on {Adapter} timed out after {Timeout}s", interfaceGuid, timeout.TotalSeconds);
                var partial = ReadScan(interfaceGuid, started, failed: true,
                    failure: $"scan completion notification not received within {timeout.TotalSeconds:F0}s");
                return partial;
            }

            var succeeded = await waiter.Task.ConfigureAwait(false);
            var snapshot = ReadScan(interfaceGuid, started, failed: !succeeded,
                failure: succeeded ? null : "driver reported scan failure");

            state.LastSnapshot = snapshot;

            if (succeeded)
            {
                _logger.LogInformation("Scan on {Adapter} finished in {Elapsed:F1}s with {Count} network(s)",
                    interfaceGuid, (DateTimeOffset.UtcNow - started).TotalSeconds, snapshot.Networks.Count);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    foreach (var network in snapshot.Networks)
                    {
                        _logger.LogDebug("  {Ssid} {Quality}% rssi={Rssi} {Band} bssid={Bssid} profile={Profile}",
                            network.Ssid, network.SignalQuality, network.Rssi, network.Band,
                            network.BssEntries.Count > 0 ? network.BssEntries[0].Bssid : "-",
                            network.ProfileName ?? "-");
                    }
                }
            }

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            return Fail(interfaceGuid, started, "scan cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure while scanning {Adapter}", interfaceGuid);
            return Fail(interfaceGuid, started, ex.Message);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private AdapterScanSnapshot Fail(Guid interfaceGuid, DateTimeOffset started, string reason) => new()
    {
        InterfaceGuid = interfaceGuid,
        StartedAtUtc = started,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        Completed = false,
        Failed = true,
        FailureReason = reason,
    };

    private AdapterScanSnapshot HandleScanFailure(Guid interfaceGuid, DateTimeOffset started, uint result, string api)
    {
        var code = (int)result;
        string reason;

        if (Win32Error.IsAccessDenied(code))
        {
            reason = $"{api} was denied (ERROR_ACCESS_DENIED). Windows 11 blocks Wi-Fi scanning for " +
                     "desktop apps that do not have Location permission.";
            MarkLocationBlocked(api);
        }
        else
        {
            reason = Win32Error.Build(api, code, $"adapter {interfaceGuid:N}");
        }

        _logger.LogWarning("{Reason}", reason);
        return Fail(interfaceGuid, started, reason);
    }

    private void MarkLocationBlocked(string operation)
    {
        lock (_gate)
        {
            _locationPermission = _locationPermission with
            {
                ScanBlockedByPolicy = true,
                BlockedOperation = operation,
                Detail = $"{operation} 返回 ERROR_ACCESS_DENIED：Windows 11 要求桌面应用具有位置权限" +
                         "（设置 > 隐私和安全性 > 位置）。",
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    private AdapterScanSnapshot ReadScan(Guid interfaceGuid, DateTimeOffset started, bool failed, string? failure)
    {
        var now = DateTimeOffset.UtcNow;
        var available = ReadAvailableNetworks(interfaceGuid, out var availableError);
        var bssEntries = ReadBssList(interfaceGuid, out var bssError);

        var hasProfileList = TryGetProfileNames(interfaceGuid, out var profiles);
        var profileSet = new HashSet<string>(profiles, StringComparer.OrdinalIgnoreCase);
        var connection = GetConnection(interfaceGuid);

        var grouped = new Dictionary<string, List<WifiBssEntry>>(StringComparer.Ordinal);
        foreach (var bss in bssEntries)
        {
            if (string.IsNullOrEmpty(bss.Ssid))
            {
                continue;
            }

            if (!grouped.TryGetValue(bss.Ssid, out var list))
            {
                list = new List<WifiBssEntry>();
                grouped[bss.Ssid] = list;
            }

            list.Add(bss);
        }

        var networks = new List<ScannedNetwork>();
        foreach (var network in available)
        {
            if (string.IsNullOrEmpty(network.Ssid))
            {
                continue;
            }

            grouped.TryGetValue(network.Ssid, out var perSsid);
            var best = perSsid?.OrderByDescending(b => b.SignalQuality).FirstOrDefault();
            var profileName = !string.IsNullOrWhiteSpace(network.ProfileName)
                ? network.ProfileName
                : profileSet.Contains(network.Ssid) ? network.Ssid : null;

            networks.Add(new ScannedNetwork
            {
                InterfaceGuid = interfaceGuid,
                Ssid = network.Ssid,
                SignalQuality = network.SignalQuality,
                Rssi = best?.Rssi ?? WifiMapping.EstimateRssiFromQuality(network.SignalQuality),
                FrequencyKhz = best?.FrequencyKhz ?? 0,
                Channel = best?.Channel ?? 0,
                Band = best?.Band ?? NetworkBand.Unknown,
                Security = network.Security,
                BssType = network.BssType,
                ProfileName = profileName,
                HasProfile = profileName is not null && profileSet.Contains(profileName),
                Connectable = network.Connectable,
                IsCurrentConnection = connection?.IsConnected == true &&
                                     string.Equals(connection.Ssid, network.Ssid, StringComparison.Ordinal),
                ObservedAtUtc = now,
                BssEntries = perSsid ?? new List<WifiBssEntry>(),
            });
        }

        var reason = failure;
        if (availableError is not null)
        {
            reason = string.IsNullOrEmpty(reason) ? availableError : $"{reason}; {availableError}";
        }

        if (bssError is not null)
        {
            reason = string.IsNullOrEmpty(reason) ? bssError : $"{reason}; {bssError}";
        }

        return new AdapterScanSnapshot
        {
            InterfaceGuid = interfaceGuid,
            StartedAtUtc = started,
            CompletedAtUtc = now,
            Completed = !failed,
            Failed = failed,
            FailureReason = reason,
            Networks = networks
                .OrderByDescending(n => n.SignalQuality)
                .ToList(),
        };
    }

    private sealed record AvailableNetwork
    {
        public required string Ssid { get; init; }

        public string? ProfileName { get; init; }

        public int SignalQuality { get; init; }

        public WifiSecurity Security { get; init; }

        public WifiBssType BssType { get; init; }

        public bool Connectable { get; init; }

        public bool HasProfile { get; init; }
    }

    private List<AvailableNetwork> ReadAvailableNetworks(Guid interfaceGuid, out string? error)
    {
        error = null;
        var results = new List<AvailableNetwork>();
        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            error = "WLAN client handle is not open";
            return results;
        }

        IntPtr listPointer = IntPtr.Zero;
        try
        {
            var result = WlanGetAvailableNetworkList(handle, interfaceGuid, 0, IntPtr.Zero, out listPointer);
            if (result != ERROR_SUCCESS)
            {
                error = Win32Error.Build("WlanGetAvailableNetworkList", (int)result, $"adapter {interfaceGuid:N}");
                if (Win32Error.IsAccessDenied((int)result))
                {
                    MarkLocationBlocked("WlanGetAvailableNetworkList");
                }

                return results;
            }

            var header = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK_LIST_HEADER>(listPointer);
            var itemSize = Marshal.SizeOf<WLAN_AVAILABLE_NETWORK>();

            for (var i = 0; i < header.dwNumberOfItems; i++)
            {
                var itemPointer = IntPtr.Add(listPointer, ListHeaderSize + (i * itemSize));
                var item = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK>(itemPointer);

                string ssid;
                string profileName;
                unsafe
                {
                    ssid = NativeStringHelper.ReadSsid(item.dot11Ssid.ucSSID, item.dot11Ssid.uSSIDLength);
                }

                // Read from the native buffer, not from the marshalled copy of the struct.
                profileName = NativeStringHelper.ReadFixedString(itemPointer, 0, 256);

                results.Add(new AvailableNetwork
                {
                    Ssid = ssid,
                    ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName,
                    SignalQuality = (int)Math.Min(item.wlanSignalQuality, 100u),
                    Security = WifiMapping.MapSecurity(
                        item.bSecurityEnabled != 0,
                        item.dot11DefaultAuthAlgorithm,
                        item.dot11DefaultCipherAlgorithm),
                    BssType = WifiMapping.MapBssType(item.dot11BssType),
                    Connectable = item.bNetworkConnectable != 0 && item.wlanNotConnectableReason == 0,
                    HasProfile = (item.dwFlags & WlanAvailableNetworkHasProfile) != 0,
                });
            }
        }
        catch (Exception ex)
        {
            error = $"WlanGetAvailableNetworkList threw {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (listPointer != IntPtr.Zero)
            {
                WlanFreeMemory(listPointer);
            }
        }

        return results;
    }

    private List<WifiBssEntry> ReadBssList(Guid interfaceGuid, out string? error)
    {
        error = null;
        var results = new List<WifiBssEntry>();
        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            error = "WLAN client handle is not open";
            return results;
        }

        IntPtr listPointer = IntPtr.Zero;
        try
        {
            // DOT11_BSS_TYPE_ANY = 3, bSecurityEnabled = FALSE (do not filter by security).
            var result = WlanGetNetworkBssList(handle, interfaceGuid, IntPtr.Zero, Dot11BssTypeAny, 0, IntPtr.Zero, out listPointer);
            if (result != ERROR_SUCCESS)
            {
                var code = (int)result;
                error = Win32Error.Build("WlanGetNetworkBssList", code, $"adapter {interfaceGuid:N}");

                if (Win32Error.IsAccessDenied(code))
                {
                    // Documented Windows 11 behaviour: BSS details (BSSID, RSSI, channel) require the
                    // Location privacy permission for desktop apps. Callers keep working with the
                    // available-network list, just without per-BSS detail.
                    MarkLocationBlocked("WlanGetNetworkBssList");
                    _logger.LogWarning(
                        "WlanGetNetworkBssList denied; continuing without BSSID/RSSI details. " +
                        "Grant Location permission to desktop apps to restore them.");
                }
                else
                {
                    _logger.LogWarning("{Error}", error);
                }

                return results;
            }

            var header = Marshal.PtrToStructure<WLAN_BSS_LIST_HEADER>(listPointer);
            var itemSize = Marshal.SizeOf<WLAN_BSS_ENTRY>();

            for (var i = 0; i < header.dwNumberOfItems; i++)
            {
                var itemPointer = IntPtr.Add(listPointer, ListHeaderSize + (i * itemSize));
                var item = Marshal.PtrToStructure<WLAN_BSS_ENTRY>(itemPointer);

                string ssid;
                string bssid;
                unsafe
                {
                    ssid = NativeStringHelper.ReadSsid(item.dot11Ssid.ucSSID, item.dot11Ssid.uSSIDLength);
                    bssid = NativeStringHelper.FormatMac(item.dot11Bssid);
                }

                var frequency = (int)item.ulChCenterFrequency;
                var rssi = item.lRssi != 0
                    ? item.lRssi
                    : WifiMapping.EstimateRssiFromQuality((int)item.uLinkQuality);

                results.Add(new WifiBssEntry
                {
                    Ssid = ssid,
                    Bssid = bssid,
                    SignalQuality = (int)Math.Min(item.uLinkQuality, 100u),
                    Rssi = rssi,
                    FrequencyKhz = frequency,
                    Channel = WifiMapping.FrequencyToChannel(frequency),
                    Band = WifiMapping.FrequencyToBand(frequency),
                    BssType = WifiMapping.MapBssType(item.dot11BssType),
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                });
            }
        }
        catch (Exception ex)
        {
            error = $"WlanGetNetworkBssList threw {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (listPointer != IntPtr.Zero)
            {
                WlanFreeMemory(listPointer);
            }
        }

        return results;
    }

    public IReadOnlyList<string> GetProfileNames(Guid interfaceGuid)
    {
        TryGetProfileNames(interfaceGuid, out var names);
        return names;
    }

    private bool TryGetProfileNames(Guid interfaceGuid, out List<string> names)
    {
        names = new List<string>();
        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        IntPtr listPointer = IntPtr.Zero;
        try
        {
            var result = WlanGetProfileList(handle, interfaceGuid, IntPtr.Zero, out listPointer);
            if (result != ERROR_SUCCESS)
            {
                if (Win32Error.IsAccessDenied((int)result))
                {
                    _logger.LogWarning("WlanGetProfileList denied for {Adapter}: {Error}",
                        interfaceGuid, Win32Error.Describe((int)result));
                }
                else
                {
                    _logger.LogDebug("WlanGetProfileList failed for {Adapter}: {Error}",
                        interfaceGuid, Win32Error.Describe((int)result));
                }

                return false;
            }

            var header = Marshal.PtrToStructure<WLAN_PROFILE_INFO_LIST_HEADER>(listPointer);
            var itemSize = Marshal.SizeOf<WLAN_PROFILE_INFO>();

            for (var i = 0; i < header.dwNumberOfItems; i++)
            {
                var itemPointer = IntPtr.Add(listPointer, ListHeaderSize + (i * itemSize));

                // The name is read straight out of the native buffer: reading it from a marshalled copy
                // of the struct (new string(fixed char*)) returned only the first character.
                var name = NativeStringHelper.ReadFixedString(itemPointer, 0, 256);
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WlanGetProfileList threw for {Adapter}", interfaceGuid);
            return false;
        }
        finally
        {
            if (listPointer != IntPtr.Zero)
            {
                WlanFreeMemory(listPointer);
            }
        }
    }

    public WifiConnectionInfo? GetConnection(Guid interfaceGuid)
    {
        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        IntPtr dataPointer = IntPtr.Zero;
        try
        {
            var result = WlanQueryInterface(
                handle,
                interfaceGuid,
                WlanIntfOpcodeCurrentConnection,
                IntPtr.Zero,
                out var dataSize,
                out dataPointer,
                out _);

            if (result != ERROR_SUCCESS)
            {
                if (result is not ERROR_INVALID_STATE)
                {
                    _logger.LogDebug("WlanQueryInterface(current_connection) on {Adapter}: {Error}",
                        interfaceGuid, Win32Error.Describe((int)result));
                }

                // Not associated: report an explicit disconnected state so callers can act on it.
                return new WifiConnectionInfo
                {
                    InterfaceGuid = interfaceGuid,
                    State = WifiConnectionState.Disconnected,
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                };
            }

            if (dataSize < (uint)Marshal.SizeOf<WLAN_CONNECTION_ATTRIBUTES>() || dataPointer == IntPtr.Zero)
            {
                return null;
            }

            var attributes = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPointer);
            var state = WifiMapping.MapInterfaceState(attributes.isState);

            string profileName;
            string ssid;
            string bssid;
            unsafe
            {
                ssid = NativeStringHelper.ReadSsid(
                    attributes.wlanAssociationAttributes.dot11Ssid.ucSSID,
                    attributes.wlanAssociationAttributes.dot11Ssid.uSSIDLength);
                bssid = NativeStringHelper.FormatMac(attributes.wlanAssociationAttributes.dot11Bssid);
            }

            // strProfileName lives at offset 8 in WLAN_CONNECTION_ATTRIBUTES; read it from the native
            // buffer instead of the marshalled copy.
            profileName = NativeStringHelper.ReadFixedString(dataPointer, 8, 256);

            if (state != WifiConnectionState.Connected)
            {
                return new WifiConnectionInfo
                {
                    InterfaceGuid = interfaceGuid,
                    State = state,
                    Ssid = ssid,
                    ProfileName = profileName,
                    Bssid = bssid,
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                };
            }

            var frequency = QueryUInt32(interfaceGuid, WlanIntfOpcodeChannelNumber);
            var rssi = QueryUInt32(interfaceGuid, WlanIntfOpcodeRssi);
            var quality = (int)attributes.wlanAssociationAttributes.wlanSignalQuality;

            return new WifiConnectionInfo
            {
                InterfaceGuid = interfaceGuid,
                State = state,
                Ssid = ssid,
                ProfileName = profileName,
                Bssid = bssid,
                SignalQuality = quality,
                Rssi = rssi is { } value && value != 0 ? (int)value : WifiMapping.EstimateRssiFromQuality(quality),
                FrequencyKhz = 0,
                Channel = 0,
                Band = NetworkBand.Unknown,
                Security = WifiMapping.MapSecurity(
                    attributes.wlanSecurityAttributes.bSecurityEnabled != 0,
                    attributes.wlanSecurityAttributes.dot11AuthAlgorithm,
                    attributes.wlanSecurityAttributes.dot11CipherAlgorithm),
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WlanQueryInterface(current_connection) threw for {Adapter}", interfaceGuid);
            return null;
        }
        finally
        {
            if (dataPointer != IntPtr.Zero)
            {
                WlanFreeMemory(dataPointer);
            }
        }
    }

    /// <summary>Fills in band/channel for an already associated interface using the BSS list.</summary>
    public WifiConnectionInfo EnrichWithBssDetails(Guid interfaceGuid, WifiConnectionInfo connection)
    {
        if (!connection.IsConnected || string.IsNullOrEmpty(connection.Bssid))
        {
            return connection;
        }

        var entries = ReadBssList(interfaceGuid, out _);
        var match = entries.FirstOrDefault(e =>
            string.Equals(e.Bssid, connection.Bssid, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return connection;
        }

        return connection with
        {
            FrequencyKhz = match.FrequencyKhz,
            Channel = match.Channel,
            Band = match.Band,
            Rssi = connection.Rssi != 0 ? connection.Rssi : match.Rssi,
        };
    }

    private uint? QueryUInt32(Guid interfaceGuid, uint opcode)
    {
        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        IntPtr dataPointer = IntPtr.Zero;
        try
        {
            var result = WlanQueryInterface(handle, interfaceGuid, opcode, IntPtr.Zero, out var size, out dataPointer, out _);
            if (result != ERROR_SUCCESS || dataPointer == IntPtr.Zero || size < sizeof(uint))
            {
                return null;
            }

            return unchecked((uint)Marshal.ReadInt32(dataPointer));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (dataPointer != IntPtr.Zero)
            {
                WlanFreeMemory(dataPointer);
            }
        }
    }

    public async Task<WlanOperationResult> ConnectAsync(
        Guid interfaceGuid,
        string profileName,
        string? bssid,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return WlanOperationResult.Fail("profile name is empty");
        }

        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            return WlanOperationResult.Fail("WLAN client handle is not open");
        }

        var dispatcher = new List<IntPtr>();
        string? ssid = null;
        try
        {
            var adapter = GetAdapters().FirstOrDefault(a => a.InterfaceGuid == interfaceGuid);
            var cached = GetLastScan(interfaceGuid);
            ssid = cached?.Networks.FirstOrDefault(n =>
                string.Equals(n.ProfileName, profileName, StringComparison.OrdinalIgnoreCase))?.Ssid;

            _ = adapter;

            var dot11Ssid = IntPtr.Zero;
            if (!string.IsNullOrEmpty(ssid))
            {
                dot11Ssid = Marshal.AllocHGlobal(36);
                dispatcher.Add(dot11Ssid);
                unsafe
                {
                    var result = WlanStringToSsid(ssid, (byte*)dot11Ssid);
                    if (result != ERROR_SUCCESS)
                    {
                        Marshal.FreeHGlobal(dot11Ssid);
                        dispatcher.Remove(dot11Ssid);
                        dot11Ssid = IntPtr.Zero;
                    }
                }
            }

            var parameters = new WLAN_CONNECTION_PARAMETERS
            {
                wlanConnectionMode = WlanConnectionModeProfile,
                strProfile = profileName,
                pDot11Ssid = dot11Ssid,
                pDesiredBssidList = IntPtr.Zero,
                dot11BssType = Dot11BssTypeInfrastructure,
                dwFlags = 0,
            };

            _logger.LogInformation("WlanConnect: adapter={Adapter} profile={Profile} ssid={Ssid} bssid={Bssid}",
                interfaceGuid, profileName, ssid ?? "-", bssid ?? "any");

            var connectResult = WlanConnect(handle, interfaceGuid, in parameters, IntPtr.Zero);
            if (connectResult != ERROR_SUCCESS)
            {
                var code = (int)connectResult;
                var accessDenied = Win32Error.IsAccessDenied(code);
                if (accessDenied)
                {
                    MarkLocationBlocked("WlanConnect");
                }

                return WlanOperationResult.Fail(
                    Win32Error.Build("WlanConnect", code, $"adapter {interfaceGuid:N} profile {profileName}"),
                    code,
                    accessDenied,
                    accessDenied);
            }

            var outcome = await WaitForStateAsync(
                interfaceGuid,
                WifiConnectionState.Connected,
                DefaultConnectTimeout,
                cancellationToken).ConfigureAwait(false);

            if (outcome.Success)
            {
                return WlanOperationResult.Ok();
            }

            return WlanOperationResult.Fail(
                outcome.Failure ?? $"adapter did not reach the connected state within {DefaultConnectTimeout.TotalSeconds:F0}s");
        }
        catch (OperationCanceledException)
        {
            return WlanOperationResult.Fail("connect cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WlanConnect threw for {Adapter}", interfaceGuid);
            return WlanOperationResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (var pointer in dispatcher)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    public async Task<WlanOperationResult> DisconnectAsync(Guid interfaceGuid, CancellationToken cancellationToken)
    {
        var handle = CurrentHandle();
        if (handle == IntPtr.Zero)
        {
            return WlanOperationResult.Fail("WLAN client handle is not open");
        }

        try
        {
            var result = WlanDisconnect(handle, interfaceGuid, IntPtr.Zero);
            if (result != ERROR_SUCCESS)
            {
                var code = (int)result;
                if (result == ERROR_INVALID_STATE)
                {
                    // Already disconnected: not an error for our purposes.
                    return WlanOperationResult.Ok();
                }

                var accessDenied = Win32Error.IsAccessDenied(code);
                if (accessDenied)
                {
                    MarkLocationBlocked("WlanDisconnect");
                }

                return WlanOperationResult.Fail(
                    Win32Error.Build("WlanDisconnect", code, $"adapter {interfaceGuid:N}"),
                    code,
                    accessDenied,
                    accessDenied);
            }

            _logger.LogInformation("WlanDisconnect requested on {Adapter}", interfaceGuid);

            var outcome = await WaitForStateAsync(
                interfaceGuid,
                WifiConnectionState.Disconnected,
                DefaultDisconnectTimeout,
                cancellationToken).ConfigureAwait(false);

            return outcome.Success
                ? WlanOperationResult.Ok()
                : WlanOperationResult.Fail(outcome.Failure ?? "adapter did not disconnect in time");
        }
        catch (OperationCanceledException)
        {
            return WlanOperationResult.Fail("disconnect cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WlanDisconnect threw for {Adapter}", interfaceGuid);
            return WlanOperationResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<(bool Success, string? Failure)> WaitForStateAsync(
        Guid interfaceGuid,
        WifiConnectionState desired,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        string? lastFailure = null;

        while (deadline.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var connection = GetConnection(interfaceGuid);
            if (connection is not null && connection.State == desired)
            {
                return (true, null);
            }

            if (desired == WifiConnectionState.Connected && connection is not null)
            {
                if (connection.State == WifiConnectionState.Disconnected && deadline.Elapsed > TimeSpan.FromSeconds(3))
                {
                    lastFailure = "adapter returned to the disconnected state during the connect attempt";
                    return (false, lastFailure);
                }

                if (_lastAttemptFailure is { } attemptFailure &&
                    DateTimeOffset.UtcNow - attemptFailure.AtUtc < TimeSpan.FromSeconds(5))
                {
                    lastFailure = $"connection attempt failed: {attemptFailure.Description}";
                    return (false, lastFailure);
                }
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        return (false, lastFailure ?? $"timed out after {timeout.TotalSeconds:F0}s waiting for {desired}");
    }

    private (DateTimeOffset AtUtc, string Description)? _lastAttemptFailure;

    /// <summary>Waits for a notification signal, or the timeout, whichever happens first.</summary>
    public async Task<bool> WaitForNotificationAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await _notificationSignal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pumps queued notifications to <see cref="NotificationReceived"/> on the caller's thread.</summary>
    public int DrainNotifications()
    {
        var drained = 0;
        while (_notifications.TryDequeue(out var notification))
        {
            drained++;
            try
            {
                NotificationReceived?.Invoke(this, notification);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification handler threw for {Notification}", notification);
            }
        }

        return drained;
    }

    private void OnNotification(IntPtr notificationData, IntPtr context)
    {
        // Runs on an arbitrary thread owned by the WLAN service. Keep it allocation-light, never
        // throw, never block, and never call back into the WLAN API from here.
        try
        {
            if (notificationData == IntPtr.Zero)
            {
                return;
            }

            var data = Marshal.PtrToStructure<WLAN_NOTIFICATION_DATA>(notificationData);
            var guid = data.InterfaceGuid == Guid.Empty ? (Guid?)null : data.InterfaceGuid;
            var description = Describe(data.NotificationSource, data.NotificationCode);
            var notification = new WlanNotificationEvent
            {
                InterfaceGuid = guid,
                Code = description.Code,
                Description = description.Text,
                TimestampUtc = DateTimeOffset.UtcNow,
            };

            if (data.NotificationSource == WLAN_NOTIFICATION_SOURCE_ACM &&
                data.NotificationCode is WlanNotificationAcmScanComplete or WlanNotificationAcmScanFail)
            {
                var succeeded = data.NotificationCode == WlanNotificationAcmScanComplete;
                if (guid is { } scanGuid &&
                    _scanStates.TryGetValue(scanGuid, out var scanState) &&
                    scanState.Waiter is { } waiter)
                {
                    waiter.TrySetResult(succeeded);
                }
            }

            if (data.NotificationSource == WLAN_NOTIFICATION_SOURCE_ACM &&
                data.NotificationCode == WlanNotificationAcmConnectionAttemptFail)
            {
                _lastAttemptFailure = (DateTimeOffset.UtcNow, description.Text);
            }

            if (data.NotificationSource == WLAN_NOTIFICATION_SOURCE_MSM &&
                data.NotificationCode is WlanNotificationMsmConnected or WlanNotificationMsmDisconnected)
            {
                _lastAttemptFailure = null;
            }

            _notifications.Enqueue(notification);

            try
            {
                _notificationSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Consumers will drain the queue anyway; dropping the signal is harmless.
            }
        }
        catch (Exception)
        {
            // A callback that throws across the native boundary can take the process down.
        }
    }

    private static (string Code, string Text) Describe(uint source, uint code)
    {
        if (source == WLAN_NOTIFICATION_SOURCE_ACM)
        {
            return code switch
            {
                WlanNotificationAcmAutoconfEnabled => ("acm.autoconf_enabled", "WLAN autoconfiguration enabled"),
                WlanNotificationAcmAutoconfDisabled => ("acm.autoconf_disabled", "WLAN autoconfiguration disabled"),
                WlanNotificationAcmScanComplete => ("acm.scan_complete", "scan completed"),
                WlanNotificationAcmScanFail => ("acm.scan_fail", "scan failed"),
                WlanNotificationAcmScanListRefresh => ("acm.scan_list_refresh", "scan list refreshed"),
                WlanNotificationAcmConnectionStart => ("acm.connection_start", "connection started"),
                WlanNotificationAcmConnectionComplete => ("acm.connection_complete", "connection completed"),
                WlanNotificationAcmConnectionAttemptFail => ("acm.connection_attempt_fail", "connection attempt failed"),
                WlanNotificationAcmInterfaceArrival => ("acm.interface_arrival", "WLAN interface arrived"),
                WlanNotificationAcmInterfaceRemoval => ("acm.interface_removal", "WLAN interface removed"),
                WlanNotificationAcmProfileChange => ("acm.profile_change", "profile changed"),
                WlanNotificationAcmProfileNameChange => ("acm.profile_name_change", "profile name changed"),
                WlanNotificationAcmProfilesExhausted => ("acm.profiles_exhausted", "all profiles attempted without success"),
                WlanNotificationAcmNetworkNotAvailable => ("acm.network_not_available", "network no longer available"),
                WlanNotificationAcmNetworkAvailable => ("acm.network_available", "network became available"),
                WlanNotificationAcmDisconnecting => ("acm.disconnecting", "disconnecting"),
                WlanNotificationAcmDisconnected => ("acm.disconnected", "disconnected"),
                WlanNotificationAcmAdhocNetworkStateChange => ("acm.adhoc_state_change", "ad-hoc network state changed"),
                WlanNotificationAcmProfileUnblocked => ("acm.profile_unblocked", "profile unblocked"),
                WlanNotificationAcmProfileBlocked => ("acm.profile_blocked", "profile blocked"),
                WlanNotificationAcmScreenPowerChange => ("acm.screen_power_change", "screen power state changed"),
                WlanNotificationAcmOperationalStateChange => ("acm.operational_state_change", "operational state changed"),
                WlanNotificationAcmBssTypeChange => ("acm.bss_type_change", "BSS type changed"),
                WlanNotificationAcmPowerSettingChange => ("acm.power_setting_change", "power setting changed"),
                _ => ($"acm.0x{code:X}", $"unmapped ACM notification {code}"),
            };
        }

        if (source == WLAN_NOTIFICATION_SOURCE_MSM)
        {
            return code switch
            {
                WlanNotificationMsmAssociating => ("msm.associating", "associating"),
                WlanNotificationMsmAssociated => ("msm.associated", "associated"),
                WlanNotificationMsmAuthenticating => ("msm.authenticating", "authenticating"),
                WlanNotificationMsmConnected => ("msm.connected", "connected"),
                WlanNotificationMsmRoamingStart => ("msm.roaming_start", "roaming started"),
                WlanNotificationMsmRoamingEnd => ("msm.roaming_end", "roaming ended"),
                WlanNotificationMsmRadioStateChange => ("msm.radio_state_change", "radio state changed"),
                WlanNotificationMsmSignalQualityChange => ("msm.signal_quality_change", "signal quality changed"),
                WlanNotificationMsmDisassociating => ("msm.disassociating", "disassociating"),
                WlanNotificationMsmDisconnected => ("msm.disconnected", "disconnected"),
                WlanNotificationMsmPeerJoin => ("msm.peer_join", "peer joined"),
                WlanNotificationMsmPeerLeave => ("msm.peer_leave", "peer left"),
                WlanNotificationMsmAdapterRemoval => ("msm.adapter_removal", "adapter removed"),
                WlanNotificationMsmAdapterOperationModeChange => ("msm.operation_mode_change", "operation mode changed"),
                WlanNotificationMsmLinkDegraded => ("msm.link_degraded", "link degraded"),
                WlanNotificationMsmLinkImproved => ("msm.link_improved", "link improved"),
                _ => ($"msm.0x{code:X}", $"unmapped MSM notification {code}"),
            };
        }

        return ($"src.0x{source:X}.0x{code:X}", $"unmapped notification source {source} code {code}");
    }

    private IntPtr CurrentHandle()
    {
        lock (_gate)
        {
            if (_client is null || _client.IsInvalid || _client.IsClosed)
            {
                return IntPtr.Zero;
            }

            return _client.DangerousGetHandle();
        }
    }

    /// <summary>Runs the documented struct layout self check; results are logged, never thrown.</summary>
    public IReadOnlyList<string> ValidateStructLayouts()
    {
        var problems = new List<string>();

        void Check<T>(int expected) where T : struct
        {
            var actual = Marshal.SizeOf<T>();
            if (actual != expected)
            {
                problems.Add($"{typeof(T).Name}: managed size {actual}, native size {expected}");
            }
        }

        Check<DOT11_SSID>(36);
        Check<WLAN_INTERFACE_INFO>(532);
        Check<WLAN_AVAILABLE_NETWORK>(628);
        Check<WLAN_BSS_ENTRY>(360);
        Check<WLAN_RATE_SET>(256);
        Check<WLAN_PROFILE_INFO>(516);
        Check<WLAN_CONNECTION_PARAMETERS>(Environment.Is64BitProcess ? 40 : 24);
        Check<WLAN_CONNECTION_ATTRIBUTES>(604);
        Check<WLAN_NOTIFICATION_DATA>(Environment.Is64BitProcess ? 40 : 32);
        Check<WLAN_ASSOCIATION_ATTRIBUTES>(68);
        Check<WLAN_SECURITY_ATTRIBUTES>(16);

        // The radio state struct is written back through WlanSetInterface(radio_state), so a wrong
        // size would silently corrupt it: 4 byte count + 64 PHYs x 12 bytes.
        Check<WLAN_PHY_RADIO_STATE>(12);
        Check<WLAN_RADIO_STATE>(772);

        foreach (var problem in problems)
        {
            _logger.LogError("Native struct layout mismatch: {Problem}", problem);
        }

        if (problems.Count == 0)
        {
            _logger.LogDebug("All Native Wi-Fi structures match the expected native layout");
        }

        return problems;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        WlanClientHandle? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }

        if (client is not null)
        {
            if (_notificationRegistered)
            {
                try
                {
                    var unregisterResult = WlanRegisterNotification(
                        client.DangerousGetHandle(),
                        WLAN_NOTIFICATION_SOURCE_NONE,
                        1,
                        null,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        out _);

                    if (unregisterResult == ERROR_SUCCESS)
                    {
                        _logger.LogInformation("Unregistered WLAN notifications");
                    }
                    else
                    {
                        _logger.LogWarning("Unregistering WLAN notifications returned {Error}",
                            Win32Error.Describe((int)unregisterResult));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to unregister WLAN notifications");
                }

                _notificationRegistered = false;
            }

            client.Dispose();
            _logger.LogInformation("WLAN client handle closed");
        }

        foreach (var state in _scanStates.Values)
        {
            state.Gate.Dispose();
        }

        _scanStates.Clear();
        _notificationSignal.Dispose();

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        GC.KeepAlive(_callback);
    }

    private sealed class ScanState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public TaskCompletionSource<bool>? Waiter { get; set; }

        public DateTimeOffset LastScanStartedUtc { get; set; }

        public AdapterScanSnapshot? LastSnapshot { get; set; }
    }
}
