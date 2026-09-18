using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Helper;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Windows.Devices;

/// <summary>Launches the elevated helper process and exchanges a single request/response pair.</summary>
public sealed class HelperClient
{
    private readonly ILogger<HelperClient> _logger;

    public HelperClient(ILogger<HelperClient>? logger = null)
    {
        _logger = logger ?? NullLogger<HelperClient>.Instance;
    }

    /// <summary>True when the current process already has administrator rights.</summary>
    public static bool IsProcessElevated()
        => NetworkGuardian.Windows.Privileges.ProcessElevation.IsElevated();

    /// <summary>How the privileged helper is started: file, argument prefix and working directory.</summary>
    public sealed record HelperInvocation(string FileName, string Prefix, string WorkingDirectory);

    /// <summary>
    /// Resolves what performs the privileged operation, in this order: an explicit override
    /// (<c>NETWORKGUARDIAN_HELPER_PATH</c>, used by tests), the application's own executable with
    /// <c>--helper</c> (the portable single-file package contains the helper inside the main exe),
    /// and finally a separate <c>NetworkGuardian.Helper.exe</c> next to it.
    /// </summary>
    public static HelperInvocation? ResolveInvocation()
    {
        var overridden = Environment.GetEnvironmentVariable("NETWORKGUARDIAN_HELPER_PATH");
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return new HelperInvocation(overridden, string.Empty, Path.GetDirectoryName(overridden) ?? AppContext.BaseDirectory);
        }

        var self = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(self) && File.Exists(self))
        {
            return new HelperInvocation(self, "--helper", Path.GetDirectoryName(self) ?? AppContext.BaseDirectory);
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "helper", "NetworkGuardian.Helper.exe"),
            Path.Combine(AppContext.BaseDirectory, "NetworkGuardian.Helper.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "NetworkGuardian.Helper.exe"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return new HelperInvocation(full, string.Empty, Path.GetDirectoryName(full) ?? AppContext.BaseDirectory);
            }
        }

        return null;
    }

    public async Task<HelperResponse> SendAsync(HelperRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var helper = ResolveInvocation();
        if (helper is null)
        {
            return new HelperResponse
            {
                Success = false,
                Operation = request.Operation,
                DeviceInstanceId = request.DeviceInstanceId,
                Outcome = nameof(DeviceOperationOutcome.NotSupported),
                Message = "The privileged helper could not be located.",
                Detail = "The portable package runs it from the application executable with --helper; " +
                         "for a split layout, place NetworkGuardian.Helper.exe next to the application " +
                         "or point NETWORKGUARDIAN_HELPER_PATH at it.",
            };
        }

        GuardianPaths.EnsureCreated();
        var requestPath = GuardianPaths.CreateHelperRequestPath();
        var responsePath = GuardianPaths.CreateHelperResponsePath();

        try
        {
            await File.WriteAllTextAsync(requestPath, HelperProtocol.SerializeRequest(request), Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            var arguments = string.IsNullOrEmpty(helper.Prefix)
                ? $"--request \"{requestPath}\" --response \"{responsePath}\""
                : $"{helper.Prefix} --request \"{requestPath}\" --response \"{responsePath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = helper.FileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = helper.WorkingDirectory,
            };

            _logger.LogInformation("Requesting elevation for {Operation} on {Device}",
                request.Operation, request.DeviceInstanceId);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Failure(request, "The helper process could not be started.", null);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return Failure(request, $"The helper did not finish within {timeout.TotalSeconds:F0}s.", null);
            }

            if (!File.Exists(responsePath))
            {
                return Failure(request,
                    $"The helper exited with code {process.ExitCode} without writing a response.",
                    process.ExitCode);
            }

            var json = await File.ReadAllTextAsync(responsePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var response = HelperProtocol.DeserializeResponse(json);
            if (response is null)
            {
                return Failure(request, "The helper response could not be parsed.", null);
            }

            _logger.LogInformation("Helper response: {Summary}", response.Summary);
            return response;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt. Not an application error.
            _logger.LogWarning("Elevation was cancelled by the user for {Operation} on {Device}",
                request.Operation, request.DeviceInstanceId);
            return Failure(request, "The elevation request was cancelled by the user.", 1223);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Helper invocation failed for {Operation} on {Device}",
                request.Operation, request.DeviceInstanceId);
            return Failure(request, $"{ex.GetType().Name}: {ex.Message}", null);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    private static HelperResponse Failure(HelperRequest request, string message, int? exitCode) => new()
    {
        Success = false,
        Operation = request.Operation,
        DeviceInstanceId = request.DeviceInstanceId,
        Outcome = exitCode == 1223
            ? nameof(DeviceOperationOutcome.AccessDenied)
            : nameof(DeviceOperationOutcome.Failed),
        Message = message,
        NativeErrorCode = exitCode,
    };

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process exited between the check and the kill.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Leftover temp files are harmless; the helper directory is pruned on startup.
        }
    }

    /// <summary>Removes stale helper exchange files left behind by a crash or a killed helper.</summary>
    public static void PruneStaleExchangeFiles(TimeSpan olderThan)
    {
        try
        {
            var directory = GuardianPaths.HelperDirectory;
            if (!Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }
}
