using System.Text.Json;
using NetworkGuardian.Core.Serialization;

namespace NetworkGuardian.Core.Helper;

/// <summary>
/// Wire contract between the unprivileged UI process and the on-demand elevated helper.
/// The helper re-validates every request (device exists, is a network class node, is genuinely
/// physical) so that the file-based protocol cannot be abused to touch arbitrary devices.
/// </summary>
public sealed record HelperRequest
{
    public int ProtocolVersion { get; init; } = HelperProtocol.Version;

    /// <summary>One of <see cref="HelperOperations"/>.</summary>
    public string Operation { get; init; } = HelperOperations.QueryStatus;

    public string DeviceInstanceId { get; init; } = string.Empty;

    /// <summary>Unpredictable value echoed by the helper to bind a response to this request.</summary>
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>When true the helper refuses to act unless the device is classified as physical.</summary>
    public bool RequirePhysicalDevice { get; init; } = true;

    /// <summary>Process that asked for the operation, recorded for audit logging.</summary>
    public string? RequestedBy { get; init; }

    public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record HelperResponse
{
    public int ProtocolVersion { get; init; } = HelperProtocol.Version;

    public bool Success { get; init; }

    public string Operation { get; init; } = string.Empty;

    public string DeviceInstanceId { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public string Outcome { get; init; } = string.Empty;

    public bool StartedAfter { get; init; }

    public uint ProblemCodeAfter { get; init; }

    public int? NativeErrorCode { get; init; }

    public string? NativeErrorName { get; init; }

    public string? Message { get; init; }

    public string? Detail { get; init; }

    public bool HelperElevated { get; init; }

    public string HelperVersion { get; init; } = "0.0.0";

    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Summary =>
        $"{Operation} {DeviceInstanceId}: {Outcome}" +
        (NativeErrorCode is null ? string.Empty : $" (error {NativeErrorCode} {NativeErrorName})");
}

public static class HelperOperations
{
    public const string QueryStatus = "query-status";
    public const string Enable = "enable";
    public const string Disable = "disable";
    public const string Restart = "restart";
}

public static class HelperProtocol
{
    public const int Version = 2;

    /// <summary>Same source generated policy as the configuration document.</summary>
    public static JsonSerializerOptions Json => NetworkGuardianJson.Options;

    public static string SerializeRequest(HelperRequest request) => NetworkGuardianJson.Serialize(request);

    public static string SerializeResponse(HelperResponse response) => NetworkGuardianJson.Serialize(response);

    public static HelperRequest? DeserializeRequest(string json) => NetworkGuardianJson.Deserialize<HelperRequest>(json);

    public static HelperResponse? DeserializeResponse(string json) => NetworkGuardianJson.Deserialize<HelperResponse>(json);
}
