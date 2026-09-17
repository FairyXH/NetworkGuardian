using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;

namespace NetworkGuardian.Infrastructure.Processes;

/// <summary>
/// Runs the configured external programs (campus authenticator and user defined offline commands).
/// </summary>
/// <remarks>
/// This runner never supplies credentials, never reads a saved Wi-Fi key and never inspects the
/// arguments it was given. Command lines are deliberately not written to the log, only the
/// executable name and the argument count, so a user who embeds a token in the arguments cannot
/// leak it through NetworkGuardian's own logs.
/// </remarks>
public sealed class ExternalCommandRunner : ICommandRunner
{
    private readonly ILogger<ExternalCommandRunner> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Process> _running = new(StringComparer.OrdinalIgnoreCase);

    public ExternalCommandRunner(ILogger<ExternalCommandRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<ExternalCommandRunner>.Instance;
    }

    public IReadOnlyList<string> RunningInstances
    {
        get
        {
            lock (_gate)
            {
                PruneDead();
                return _running.Values
                    .Select(p => SafeProcessName(p) ?? "unknown")
                    .ToList();
            }
        }
    }

    public bool IsRunning(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        lock (_gate)
        {
            PruneDead();
            return _running.ContainsKey(Normalize(executablePath)) ||
                   IsRunningByName(Path.GetFileNameWithoutExtension(executablePath));
        }
    }

    private static bool IsRunningByName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<CommandExecutionResult> RunAsync(
        CommandDefinition definition,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stopwatch = Stopwatch.StartNew();
        var display = Describe(definition);

        if (string.IsNullOrWhiteSpace(definition.ExecutablePath) && definition.Kind == CommandKind.Executable)
        {
            return new CommandExecutionResult
            {
                Started = false,
                Failure = "no executable path is configured",
            };
        }

        if (definition.SkipIfAlreadyRunning && IsRunning(definition.ExecutablePath))
        {
            _logger.LogInformation("Skipping {Command}: an instance is already running ({Reason})", display, reason);
            return new CommandExecutionResult
            {
                Started = false,
                AlreadyRunning = true,
                ExecutablePath = definition.ExecutablePath,
                Failure = "an instance of this program is already running",
            };
        }

        var elevated = definition.RunAsAdministrator;
        if (elevated && ProcessPrivileges.IsElevated())
        {
            // Already elevated: no need to spawn a second UAC prompt.
            elevated = false;
        }

        Process? process = null;
        try
        {
            var startInfo = BuildStartInfo(definition, elevated);

            _logger.LogInformation(
                "Starting {Command} ({Reason}) [elevated={Elevated}, kind={Kind}, argCount={ArgCount}]",
                display, reason, definition.RunAsAdministrator, definition.Kind, CountArguments(definition.Arguments));

            process = Process.Start(startInfo);
            if (process is null)
            {
                return new CommandExecutionResult
                {
                    Started = false,
                    ExecutablePath = definition.ExecutablePath,
                    Failure = "Process.Start returned null",
                    Elevated = definition.RunAsAdministrator,
                };
            }

            lock (_gate)
            {
                _running[Normalize(definition.ExecutablePath)] = process;
            }

            if (!definition.WaitForExit)
            {
                stopwatch.Stop();
                return new CommandExecutionResult
                {
                    Started = true,
                    ExecutablePath = definition.ExecutablePath,
                    Elevated = definition.RunAsAdministrator,
                    Duration = stopwatch.Elapsed,
                };
            }

            var timeout = definition.ExecutionTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(definition.ExecutionTimeoutSeconds)
                : Timeout.InfiniteTimeSpan;

            var exited = await WaitForExitAsync(process, timeout, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!exited)
            {
                var killed = false;
                if (definition.KillOnTimeout)
                {
                    killed = TryKill(process);
                }

                _logger.LogWarning("{Command} did not exit within {Timeout}s (killed={Killed})",
                    display, definition.ExecutionTimeoutSeconds, killed);

                return new CommandExecutionResult
                {
                    Started = true,
                    TimedOut = true,
                    Killed = killed,
                    ExecutablePath = definition.ExecutablePath,
                    Elevated = definition.RunAsAdministrator,
                    Duration = stopwatch.Elapsed,
                    Failure = $"the process did not exit within {definition.ExecutionTimeoutSeconds}s",
                };
            }

            var exitCode = TryGetExitCode(process);
            _logger.LogInformation("{Command} exited with code {ExitCode} after {Elapsed:F1}s",
                display, exitCode, stopwatch.Elapsed.TotalSeconds);

            return new CommandExecutionResult
            {
                Started = true,
                ExitCode = exitCode,
                ExecutablePath = definition.ExecutablePath,
                Elevated = definition.RunAsAdministrator,
                Duration = stopwatch.Elapsed,
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            stopwatch.Stop();
            _logger.LogWarning("Elevation for {Command} was cancelled by the user", display);
            return new CommandExecutionResult
            {
                Started = false,
                ExecutablePath = definition.ExecutablePath,
                Elevated = true,
                Duration = stopwatch.Elapsed,
                Failure = "the elevation request was cancelled",
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed to start {Command}", display);
            return new CommandExecutionResult
            {
                Started = false,
                ExecutablePath = definition.ExecutablePath,
                Elevated = definition.RunAsAdministrator,
                Duration = stopwatch.Elapsed,
                Failure = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
        finally
        {
            if (process is not null && definition.WaitForExit)
            {
                lock (_gate)
                {
                    _running.Remove(Normalize(definition.ExecutablePath));
                }

                process.Dispose();
            }
        }
    }

    private static ProcessStartInfo BuildStartInfo(CommandDefinition definition, bool elevated)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = elevated,
            CreateNoWindow = definition.WindowStyle == CommandWindowStyle.Hidden,
            WindowStyle = definition.WindowStyle switch
            {
                CommandWindowStyle.Hidden => ProcessWindowStyle.Hidden,
                CommandWindowStyle.Minimized => ProcessWindowStyle.Minimized,
                _ => ProcessWindowStyle.Normal,
            },
        };

        if (elevated)
        {
            startInfo.Verb = "runas";
        }

        if (definition.Kind == CommandKind.Shell)
        {
            // A shell command is executed through cmd.exe so users can paste an arbitrary command
            // line ("curl http://...", "python auth.py --user x", ...).
            startInfo.FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var commandLine = string.Join(' ',
                new[] { definition.ExecutablePath, definition.Arguments }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            startInfo.Arguments = "/d /c " + commandLine;
        }
        else
        {
            startInfo.FileName = definition.ExecutablePath;
            startInfo.Arguments = definition.Arguments ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(definition.WorkingDirectory) &&
            Directory.Exists(definition.WorkingDirectory))
        {
            startInfo.WorkingDirectory = definition.WorkingDirectory;
        }
        else if (definition.Kind == CommandKind.Executable && !string.IsNullOrWhiteSpace(definition.ExecutablePath))
        {
            var directory = Path.GetDirectoryName(definition.ExecutablePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                startInfo.WorkingDirectory = directory;
            }
        }

        return startInfo;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (timeout != Timeout.InfiniteTimeSpan)
        {
            cts.CancelAfter(timeout);
        }

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void PruneDead()
    {
        foreach (var key in _running.Keys.ToList())
        {
            try
            {
                if (_running[key].HasExited)
                {
                    _running[key].Dispose();
                    _running.Remove(key);
                }
            }
            catch (Exception)
            {
                _running.Remove(key);
            }
        }
    }

    private static string Normalize(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().ToLowerInvariant();

    private static int CountArguments(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments)
            ? 0
            : arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Describe(CommandDefinition definition) =>
        $"{definition.Name} ({(definition.Kind == CommandKind.Shell ? "shell" : "exe")}:" +
        $"{Path.GetFileName(definition.ExecutablePath)})";
}
