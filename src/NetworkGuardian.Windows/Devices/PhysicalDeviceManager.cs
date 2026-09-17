using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Helper;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;

namespace NetworkGuardian.Windows.Devices;

/// <summary>
/// Public device manager used by the guardian host. Enumeration and classification are unprivileged;
/// enabling or disabling a device goes through the on-demand elevated helper.
/// </summary>
public sealed class PhysicalDeviceManager : IDeviceManager
{
    private readonly ILogger<PhysicalDeviceManager> _logger;
    private readonly NetworkDeviceClassifier _classifier;
    private readonly PnpDeviceInventory _inventory;
    private readonly HelperClient _helper;
    private readonly Func<IReadOnlyCollection<Guid>> _wlanGuidProvider;

    public PhysicalDeviceManager(
        NetworkDeviceClassifier classifier,
        Func<IReadOnlyCollection<Guid>>? wlanGuidProvider = null,
        ILogger<PhysicalDeviceManager>? logger = null,
        HelperClient? helperClient = null)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _logger = logger ?? NullLogger<PhysicalDeviceManager>.Instance;
        _inventory = new PnpDeviceInventory();
        _helper = helperClient ?? new HelperClient();
        _wlanGuidProvider = wlanGuidProvider ?? (() => Array.Empty<Guid>());
    }

    /// <summary>Wildcard deny patterns applied on top of the physical device rules.</summary>
    public IReadOnlyList<string> DenyList { get; set; } = Array.Empty<string>();

    /// <summary>Cached snapshot of the last enumeration, used by the settings and device views.</summary>
    public IReadOnlyList<ManagedDevice> LastEnumeration { get; private set; } = Array.Empty<ManagedDevice>();

    public Task<IReadOnlyList<ManagedDevice>> EnumerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var guids = _wlanGuidProvider();
        var devices = new List<ManagedDevice>();

        foreach (var record in _inventory.Enumerate())
        {
            var classification = _classifier.Classify(record, guids, DenyList);
            devices.Add(new ManagedDevice { Record = record, Classification = classification });
        }

        LastEnumeration = devices;

        var physical = devices.Count(d => d.Classification.IsPhysical);
        var rejected = devices.Count(d => !d.Classification.IsPhysical);
        _logger.LogDebug("PnP enumeration: {Total} network class node(s), {Physical} physical, {Rejected} filtered out",
            devices.Count, physical, rejected);

        foreach (var device in devices)
        {
            _logger.LogDebug("  {InstanceId} => {Category} physical={Physical} rule={Rule}",
                device.Record.DeviceInstanceId,
                device.Classification.Category,
                device.Classification.IsPhysical,
                device.Classification.Rule);
        }

        return Task.FromResult<IReadOnlyList<ManagedDevice>>(devices);
    }

    public async Task<DeviceOperationResult> EnableAsync(string deviceInstanceId, CancellationToken cancellationToken)
    {
        var verification = Verify(deviceInstanceId, out var record);
        if (verification is not null)
        {
            return verification;
        }

        if (record!.IsStarted && record.ProblemCode == 0)
        {
            return new DeviceOperationResult
            {
                DeviceInstanceId = deviceInstanceId,
                Operation = "enable",
                Outcome = DeviceOperationOutcome.AlreadyInDesiredState,
                StartedAfter = true,
                ProblemCodeAfter = 0,
                Detail = "The device is already enabled.",
                Elevated = HelperClient.IsProcessElevated(),
            };
        }

        if (!string.IsNullOrWhiteSpace(record.ProblemCode.ToString()) && record.ProblemCode != 22 && record.ProblemCode != 21)
        {
            // Problem codes other than "disabled" (22) indicate a driver fault; enabling will not help
            // and repeated attempts create noise, so report the real reason instead.
            _logger.LogWarning("Skipping enable of {Device}: problem code {Problem} is not 'disabled'",
                deviceInstanceId, record.ProblemCode);
        }

        if (HelperClient.IsProcessElevated())
        {
            _logger.LogInformation("Process is elevated; enabling {Device} directly", deviceInstanceId);
            return DeviceNodeOperations.Enable(deviceInstanceId, requirePhysical: true, _classifier, _inventory, _logger);
        }

        var request = new HelperRequest
        {
            Operation = HelperOperations.Enable,
            DeviceInstanceId = deviceInstanceId,
            RequirePhysicalDevice = true,
            RequestedBy = $"{Environment.ProcessPath} ({Environment.ProcessId})",
        };

        var response = await _helper.SendAsync(request, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        return Map(response, "enable");
    }

    public async Task<DeviceOperationResult> DisableAsync(string deviceInstanceId, CancellationToken cancellationToken)
    {
        var verification = Verify(deviceInstanceId, out _);
        if (verification is not null)
        {
            return verification;
        }

        if (HelperClient.IsProcessElevated())
        {
            return DeviceNodeOperations.Disable(deviceInstanceId, requirePhysical: true, _classifier, _inventory, _logger);
        }

        var request = new HelperRequest
        {
            Operation = HelperOperations.Disable,
            DeviceInstanceId = deviceInstanceId,
            RequirePhysicalDevice = true,
            RequestedBy = $"{Environment.ProcessPath} ({Environment.ProcessId})",
        };

        var response = await _helper.SendAsync(request, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        return Map(response, "disable");
    }

    /// <summary>Returns a non-null result when the request must be refused before doing any work.</summary>
    private DeviceOperationResult? Verify(string deviceInstanceId, out PnpDeviceRecord? record)
    {
        var guids = _wlanGuidProvider();
        record = _inventory.Enumerate()
            .FirstOrDefault(r => string.Equals(r.DeviceInstanceId, deviceInstanceId, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            return new DeviceOperationResult
            {
                DeviceInstanceId = deviceInstanceId,
                Operation = "verify",
                Outcome = DeviceOperationOutcome.DeviceNotFound,
                Detail = "Device instance id not found in the network setup class.",
            };
        }

        var classification = _classifier.Classify(record, guids, DenyList);
        if (!classification.IsPhysical)
        {
            _logger.LogWarning("Refusing to change state of non-physical device {Device}: {Reason}",
                deviceInstanceId, classification.Reason);
            return new DeviceOperationResult
            {
                DeviceInstanceId = deviceInstanceId,
                Operation = "verify",
                Outcome = DeviceOperationOutcome.NotPhysicalDevice,
                Detail = $"Refused for safety: {classification.Reason}",
            };
        }

        if (!record.IsPresent)
        {
            return new DeviceOperationResult
            {
                DeviceInstanceId = deviceInstanceId,
                Operation = "verify",
                Outcome = DeviceOperationOutcome.DeviceNotFound,
                Detail = "The device is not present.",
            };
        }

        return null;
    }

    private static DeviceOperationResult Map(HelperResponse response, string operation)
    {
        var outcome = Enum.TryParse<DeviceOperationOutcome>(response.Outcome, ignoreCase: true, out var parsed)
            ? parsed
            : DeviceOperationOutcome.Failed;

        return new DeviceOperationResult
        {
            DeviceInstanceId = response.DeviceInstanceId,
            Operation = operation,
            Outcome = outcome,
            StartedAfter = response.StartedAfter,
            ProblemCodeAfter = response.ProblemCodeAfter,
            NativeErrorCode = response.NativeErrorCode,
            NativeErrorName = response.NativeErrorName,
            Win32Message = response.Message,
            Detail = response.Detail,
            Elevated = response.HelperElevated,
        };
    }
}
