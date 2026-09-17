using System.Diagnostics;
using System.Reflection;
using System.Text;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Helper;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using NetworkGuardian.Windows.Devices;
using NetworkGuardian.Windows.Privileges;

namespace NetworkGuardian.Helper;

/// <summary>
/// On-demand elevated helper. It performs exactly one privileged PnP operation per invocation and
/// always re-validates that the target is a physical device before touching it.
/// </summary>
internal static class Program
{
    private static readonly string HelperVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private static int Main(string[] args)
    {
        GuardianPaths.EnsureCreated();

        var requestPath = GetArgument(args, "--request");
        var responsePath = GetArgument(args, "--response");

        Log($"helper start pid={Environment.ProcessId} elevated={ProcessElevation.IsElevated()} " +
            $"request={(requestPath ?? "<none>")}");

        if (string.IsNullOrWhiteSpace(requestPath) || string.IsNullOrWhiteSpace(responsePath))
        {
            Log("missing --request or --response argument");
            return 2;
        }

        HelperRequest? request = null;
        try
        {
            if (!File.Exists(requestPath))
            {
                WriteResponse(responsePath, new HelperResponse
                {
                    Success = false,
                    Outcome = nameof(DeviceOperationOutcome.Failed),
                    Message = "The request file does not exist.",
                    Detail = requestPath,
                });
                return 3;
            }

            var json = File.ReadAllText(requestPath, Encoding.UTF8);
            request = HelperProtocol.Deserialize<HelperRequest>(json);
        }
        catch (Exception ex)
        {
            Log($"failed to read the request: {ex}");
            WriteResponse(responsePath, new HelperResponse
            {
                Success = false,
                Outcome = nameof(DeviceOperationOutcome.Failed),
                Message = $"The request could not be read: {ex.Message}",
            });
            return 3;
        }

        if (request is null)
        {
            WriteResponse(responsePath, new HelperResponse
            {
                Success = false,
                Outcome = nameof(DeviceOperationOutcome.Failed),
                Message = "The request document was empty.",
            });
            return 3;
        }

        if (request.ProtocolVersion != HelperProtocol.Version)
        {
            WriteResponse(responsePath, new HelperResponse
            {
                Success = false,
                Operation = request.Operation,
                DeviceInstanceId = request.DeviceInstanceId,
                Outcome = nameof(DeviceOperationOutcome.NotSupported),
                Message = $"Protocol version {request.ProtocolVersion} is not supported by this helper " +
                          $"(expected {HelperProtocol.Version}).",
            });
            return 4;
        }

        var response = Execute(request);
        WriteResponse(responsePath, response);
        Log($"helper done {response.Summary}");
        return response.Success ? 0 : 1;
    }

    private static HelperResponse Execute(HelperRequest request)
    {
        var classifier = new NetworkDeviceClassifier();
        var inventory = new PnpDeviceInventory();
        var elevated = ProcessElevation.IsElevated();

        if (!elevated)
        {
            return new HelperResponse
            {
                Success = false,
                Operation = request.Operation,
                DeviceInstanceId = request.DeviceInstanceId,
                Outcome = nameof(DeviceOperationOutcome.AccessDenied),
                HelperElevated = false,
                Message = "The helper is not running elevated; the operation was refused.",
                HelperVersion = HelperVersion,
            };
        }

        DeviceOperationResult result;

        switch (request.Operation)
        {
            case HelperOperations.QueryStatus:
                result = DeviceNodeOperations.Verify(
                    request.DeviceInstanceId, request.RequirePhysicalDevice, classifier, inventory);
                break;
            case HelperOperations.Enable:
                result = DeviceNodeOperations.Enable(
                    request.DeviceInstanceId, request.RequirePhysicalDevice, classifier, inventory);
                break;
            case HelperOperations.Disable:
                result = DeviceNodeOperations.Disable(
                    request.DeviceInstanceId, request.RequirePhysicalDevice, classifier, inventory);
                break;
            case HelperOperations.Restart:
                result = DeviceNodeOperations.Restart(
                    request.DeviceInstanceId, request.RequirePhysicalDevice, classifier, inventory);
                break;
            default:
                return new HelperResponse
                {
                    Success = false,
                    Operation = request.Operation,
                    DeviceInstanceId = request.DeviceInstanceId,
                    Outcome = nameof(DeviceOperationOutcome.NotSupported),
                    HelperElevated = elevated,
                    Message = $"Unsupported operation '{request.Operation}'.",
                    HelperVersion = HelperVersion,
                };
        }

        return new HelperResponse
        {
            Success = result.Success,
            Operation = result.Operation,
            DeviceInstanceId = result.DeviceInstanceId,
            Outcome = result.Outcome.ToString(),
            StartedAfter = result.StartedAfter ?? false,
            ProblemCodeAfter = result.ProblemCodeAfter ?? 0,
            NativeErrorCode = result.NativeErrorCode,
            NativeErrorName = result.NativeErrorName,
            Message = result.Win32Message,
            Detail = result.Detail,
            HelperElevated = elevated,
            HelperVersion = HelperVersion,
        };
    }

    private static void WriteResponse(string path, HelperResponse response)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, HelperProtocol.Serialize(response), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log($"failed to write the response: {ex}");
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Appends to a helper log file; used because an elevated WinExe has no console.</summary>
    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(GuardianPaths.HelperDirectory, "helper.log");
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception)
        {
            // Nothing useful to do; the helper must never fail because logging failed.
        }
    }

}
