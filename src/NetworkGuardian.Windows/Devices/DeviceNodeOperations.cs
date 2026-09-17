using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Windows.Native;
using static NetworkGuardian.Windows.Native.CfgMgr32Native;

namespace NetworkGuardian.Windows.Devices;

/// <summary>
/// Direct (in-process) device node operations. Used by the elevated helper and by the UI process
/// when it already happens to be elevated. Always re-validates that the target is a physical device.
/// </summary>
public static class DeviceNodeOperations
{
    internal const uint CM_REENUMERATE_NORMAL = 0x00000000;

    public static DeviceOperationResult Verify(
        string deviceInstanceId,
        bool requirePhysical,
        NetworkDeviceClassifier classifier,
        PnpDeviceInventory inventory)
    {
        var record = inventory.Enumerate()
            .FirstOrDefault(r => string.Equals(r.DeviceInstanceId, deviceInstanceId, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            return Result(deviceInstanceId, "verify", DeviceOperationOutcome.DeviceNotFound,
                detail: "The device instance id is not present in the network setup class.");
        }

        var classification = classifier.Classify(record);
        if (requirePhysical && !classification.IsPhysical)
        {
            return Result(deviceInstanceId, "verify", DeviceOperationOutcome.NotPhysicalDevice,
                detail: $"Refused: {classification.Reason}");
        }

        return Result(deviceInstanceId, "verify", DeviceOperationOutcome.Succeeded,
            startedAfter: record.IsStarted, problemCodeAfter: record.ProblemCode,
            detail: $"category={classification.Category} rule={classification.Rule}");
    }

    public static DeviceOperationResult Enable(
        string deviceInstanceId,
        bool requirePhysical,
        NetworkDeviceClassifier classifier,
        PnpDeviceInventory inventory,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var located = Locate(deviceInstanceId, inventory, classifier, requirePhysical, out var refusal);
        if (located is null)
        {
            return refusal!;
        }

        var (record, devInst) = located.Value;

        if (record.IsStarted && record.ProblemCode == 0)
        {
            return Result(deviceInstanceId, "enable", DeviceOperationOutcome.AlreadyInDesiredState,
                startedAfter: true, problemCodeAfter: 0,
                detail: "The device is already enabled.");
        }

        var status = CM_Enable_DevNode(devInst, 0);
        logger.LogInformation("CM_Enable_DevNode({Device}) returned {Status}", deviceInstanceId, Describe(status));

        return Translate(deviceInstanceId, "enable", status, devInst, inventory);
    }

    public static DeviceOperationResult Disable(
        string deviceInstanceId,
        bool requirePhysical,
        NetworkDeviceClassifier classifier,
        PnpDeviceInventory inventory,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var located = Locate(deviceInstanceId, inventory, classifier, requirePhysical, out var refusal);
        if (located is null)
        {
            return refusal!;
        }

        var (record, devInst) = located.Value;

        if (!record.IsStarted)
        {
            return Result(deviceInstanceId, "disable", DeviceOperationOutcome.AlreadyInDesiredState,
                startedAfter: false, problemCodeAfter: record.ProblemCode,
                detail: "The device is already disabled.");
        }

        var status = CM_Disable_DevNode(devInst, 0);
        logger.LogInformation("CM_Disable_DevNode({Device}) returned {Status}", deviceInstanceId, Describe(status));

        return Translate(deviceInstanceId, "disable", status, devInst, inventory);
    }

    public static DeviceOperationResult Restart(
        string deviceInstanceId,
        bool requirePhysical,
        NetworkDeviceClassifier classifier,
        PnpDeviceInventory inventory,
        ILogger? logger = null)
    {
        var disable = Disable(deviceInstanceId, requirePhysical, classifier, inventory, logger);
        if (!disable.Success)
        {
            return disable;
        }

        Thread.Sleep(1500);
        return Enable(deviceInstanceId, requirePhysical, classifier, inventory, logger) with { Operation = "restart" };
    }

    private static (PnpDeviceRecord Record, uint DevInst)? Locate(
        string deviceInstanceId,
        PnpDeviceInventory inventory,
        NetworkDeviceClassifier classifier,
        bool requirePhysical,
        out DeviceOperationResult? refusal)
    {
        refusal = null;

        var record = inventory.Enumerate()
            .FirstOrDefault(r => string.Equals(r.DeviceInstanceId, deviceInstanceId, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            refusal = Result(deviceInstanceId, "locate", DeviceOperationOutcome.DeviceNotFound,
                detail: "The device instance id was not found in the network class.");
            return null;
        }

        if (!record.IsPresent)
        {
            refusal = Result(deviceInstanceId, "locate", DeviceOperationOutcome.DeviceNotFound,
                detail: "The device is not currently present (ghost node).");
            return null;
        }

        var classification = classifier.Classify(record);
        if (requirePhysical && !classification.IsPhysical)
        {
            refusal = Result(deviceInstanceId, "locate", DeviceOperationOutcome.NotPhysicalDevice,
                detail: $"Refused for safety: {classification.Reason}");
            return null;
        }

        var status = CM_Locate_DevNodeW(out var devInst, deviceInstanceId, CM_LOCATE_DEVNODE_NORMAL);
        if (status != CR_SUCCESS)
        {
            refusal = Result(deviceInstanceId, "locate", DeviceOperationOutcome.Failed,
                nativeErrorCode: status, nativeErrorName: Describe(status),
                detail: "CM_Locate_DevNode failed.");
            return null;
        }

        return (record, devInst);
    }

    private static DeviceOperationResult Translate(
        string deviceInstanceId,
        string operation,
        int status,
        uint devInst,
        PnpDeviceInventory inventory)
    {
        var startedAfter = false;
        uint problemAfter = 0;
        if (CM_Get_DevNode_Status(out var devStatus, out problemAfter, devInst, 0) == CR_SUCCESS)
        {
            startedAfter = (devStatus & DN_STARTED) != 0;
        }

        if (status == CR_SUCCESS)
        {
            return Result(deviceInstanceId, operation, DeviceOperationOutcome.Succeeded,
                startedAfter: startedAfter, problemCodeAfter: problemAfter,
                detail: startedAfter ? "Device started." : "Request accepted; the device is starting.");
        }

        var outcome = status switch
        {
            CR_ACCESS_DENIED => DeviceOperationOutcome.AccessDenied,
            CR_NO_SUCH_VALUE => DeviceOperationOutcome.NotSupported,
            CR_REMOVE_VETOED => DeviceOperationOutcome.NotSupported,
            CR_INVALID_DEVINST => DeviceOperationOutcome.DeviceNotFound,
            _ => DeviceOperationOutcome.Failed,
        };

        var needsReboot = problemAfter == 14; // CM_PROB_NEED_RESTART
        var detail = status switch
        {
            CR_ACCESS_DENIED => "Administrator rights are required to change this device's state.",
            _ => $"Configuration Manager reported {Describe(status)}.",
        };

        _ = inventory;

        return Result(deviceInstanceId, operation,
            needsReboot ? DeviceOperationOutcome.RebootRequired : outcome,
            startedAfter: startedAfter,
            problemCodeAfter: problemAfter,
            nativeErrorCode: status,
            nativeErrorName: Describe(status),
            detail: detail);
    }

    private static DeviceOperationResult Result(
        string deviceInstanceId,
        string operation,
        DeviceOperationOutcome outcome,
        bool? startedAfter = null,
        uint? problemCodeAfter = null,
        int? nativeErrorCode = null,
        string? nativeErrorName = null,
        string? detail = null) => new()
    {
        DeviceInstanceId = deviceInstanceId,
        Operation = operation,
        Outcome = outcome,
        StartedAfter = startedAfter,
        ProblemCodeAfter = problemCodeAfter,
        NativeErrorCode = nativeErrorCode,
        NativeErrorName = nativeErrorName,
        Win32Message = nativeErrorCode is null ? null : Win32Error.Describe(nativeErrorCode.Value),
        Detail = detail,
        Elevated = HelperClient.IsProcessElevated(),
    };
}
