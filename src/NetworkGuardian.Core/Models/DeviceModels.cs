namespace NetworkGuardian.Core.Models;

/// <summary>
/// Raw, transport agnostic description of a PnP device node plus the network-specific registry
/// properties that the Windows layer could read. Keeping this a plain data record means the
/// physical/virtual classification rules live in Core and are unit testable.
/// </summary>
public sealed record PnpDeviceRecord
{
    public required string DeviceInstanceId { get; init; }

    public string? ParentInstanceId { get; init; }

    public string? FriendlyName { get; init; }

    public string? DeviceDescription { get; init; }

    public string? BusReportedDeviceDescription { get; init; }

    /// <summary>PnP enumerator, e.g. <c>PCI</c>, <c>USB</c>, <c>ROOT</c>, <c>SWD</c>, <c>VMBUS</c>.</summary>
    public string? EnumeratorName { get; init; }

    public string? BusTypeName { get; init; }

    public Guid? BusTypeGuid { get; init; }

    public string? ClassName { get; init; }

    public Guid? ClassGuid { get; init; }

    public string? DriverKey { get; init; }

    public string? Service { get; init; }

    public string? LocationInfo { get; init; }

    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CompatibleIds { get; init; } = Array.Empty<string>();

    public bool IsPresent { get; init; }

    public bool IsStarted { get; init; }

    /// <summary>PnP problem code (0 = working, 22 = disabled, 21 = removed, ...).</summary>
    public uint ProblemCode { get; init; }

    /// <summary>netcfg instance GUID (<c>NetCfgInstanceId</c>) when the driver key could be read.</summary>
    public string? NetCfgInstanceId { get; init; }

    /// <summary>NDIS <c>*PhysicalMediaType</c>: 1/9 = wireless LAN, 14 = 802.3 Ethernet.</summary>
    public int? PhysicalMediaType { get; init; }

    /// <summary>NDIS <c>*IfType</c> when present (71 = IEEE80211, 6 = Ethernet CSMA/CD).</summary>
    public int? IfType { get; init; }

    public string? MacAddress { get; init; }

    public string? DriverVersion { get; init; }

    public string? ManufacturerName { get; init; }

    public override string ToString() => $"{DeviceInstanceId} ({FriendlyName})";
}

/// <summary>Classification verdict for a <see cref="PnpDeviceRecord"/>.</summary>
public sealed record DeviceClassification
{
    public required DeviceCategory Category { get; init; }

    public required bool IsPhysical { get; init; }

    /// <summary>Machine readable rule identifier that produced the verdict.</summary>
    public required string Rule { get; init; }

    /// <summary>Human readable explanation shown in the UI and written to logs.</summary>
    public required string Reason { get; init; }
}

/// <summary>PnP device plus classification, ready for the UI and device manager.</summary>
public sealed record ManagedDevice
{
    public required PnpDeviceRecord Record { get; init; }

    public required DeviceClassification Classification { get; init; }

    public bool IsEnabled => Record.IsPresent && Record.IsStarted && Record.ProblemCode == 0;

    public bool IsDisabled => Record.IsPresent && !Record.IsStarted
                              && (Record.ProblemCode == 22 || Record.ProblemCode == 21);

    public override string ToString() => $"{Record.DeviceInstanceId} => {Classification.Category}";
}

/// <summary>Outcome of a PnP enable/disable/restart request executed by the privileged helper.</summary>
public sealed record DeviceOperationResult
{
    public required string DeviceInstanceId { get; init; }

    public required string Operation { get; init; }

    public required DeviceOperationOutcome Outcome { get; init; }

    /// <summary>State observed after the operation, when known.</summary>
    public bool? StartedAfter { get; init; }

    public uint? ProblemCodeAfter { get; init; }

    public int? NativeErrorCode { get; init; }

    public string? NativeErrorName { get; init; }

    public string? Win32Message { get; init; }

    public string? Detail { get; init; }

    public bool Elevated { get; init; }

    public bool Success => Outcome is DeviceOperationOutcome.Succeeded
                                   or DeviceOperationOutcome.AlreadyInDesiredState
                                   or DeviceOperationOutcome.RebootRequired;

    public override string ToString() =>
        $"{Operation} {DeviceInstanceId}: {Outcome} (err={NativeErrorCode} {NativeErrorName}) {Detail}";
}
