using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Portable.Host;
using NetworkGuardian.Portable.Interop;
using NetworkGuardian.Portable.Ui.Pages;
using NetworkGuardian.Portable.Ui;
using static NetworkGuardian.Portable.Interop.NativeMethods;

namespace NetworkGuardian.Portable;

/// <summary>Builds the page list once; the window keeps the instances for its lifetime.</summary>
internal static class Pages
{
    public static IReadOnlyList<IPage> Build() => new List<IPage>
    {
        new DashboardPage(),
        new WirelessPage(),
        new CredentialsPage(),
        new EthernetPage(),
        new SettingsPage(),
        new LogsPage(),
    };
}

/// <summary>
/// Entry point of the portable build: single instance guard, application host and the message loop.
/// </summary>
/// <remarks>
/// QA switches used by the verification scripts:
/// <list type="bullet">
/// <item><c>--minimized</c>: start hidden in the tray.</item>
/// <item><c>--page &lt;tag&gt;</c>: dashboard | wireless | credentials | ethernet | settings | logs.</item>
/// <item><c>--visible</c>: force the window on screen (used together with the screenshot script).</item>
/// </list>
/// The configuration root can be redirected with <c>NETWORKGUARDIAN_CONFIG_ROOT</c> so a smoke test
/// never touches the real profile.
/// </remarks>
internal static class Program
{
    /// <summary>Window class polled by the screenshot and shutdown scripts.</summary>
    public const string WindowClassName = MainWindow.ClassName;

    /// <summary>
    /// Single instance name. <c>NETWORKGUARDIAN_INSTANCE_SUFFIX</c> appends a suffix so verification
    /// runs can start their own instance next to the one the user is running.
    /// </summary>
    private const string MutexName = @"Local\NetworkGuardian.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // The privileged half ships inside this executable: --helper turns this process into the
        // on-demand helper. It has to be handled before anything else - above all before the single
        // instance mutex, which the running user instance already owns (an elevated helper that
        // exited because "an instance is running" would silently answer nothing).
        if (args.Any(a => string.Equals(a, "--helper", StringComparison.OrdinalIgnoreCase)))
        {
            return NetworkGuardian.Windows.Helper.HelperEntry.Run(args);
        }

        try
        {
            var suffix = Environment.GetEnvironmentVariable("NETWORKGUARDIAN_INSTANCE_SUFFIX");
            using var mutex = new Mutex(initiallyOwned: true, MutexName + (suffix ?? string.Empty), out var isFirstInstance);
            if (!isFirstInstance)
            {
                // Surface the already running instance instead of starting a second monitor loop.
                var existing = FindWindowW(MainWindow.ClassName, null);
                if (existing != IntPtr.Zero)
                {
                    PostMessageW(existing, (uint)WM_APP_SHOW_WINDOW, IntPtr.Zero, IntPtr.Zero);
                }

                AppendStartupNote(
                    "another NetworkGuardian instance owns the single instance mutex; this process exits " +
                    $"(existing window found: {existing != IntPtr.Zero})");
                return 0;
            }

            GuardianPaths.EnsureCreated();
            return PortableApp.Run(args);
        }
        catch (Exception ex)
        {
            // Startup failures have to leave a trace: an elevated console is not available here.
            AppendStartupNote($"startup failed: {ex}");
            return 1;
        }
    }

    private static void AppendStartupNote(string message)
    {
        try
        {
            GuardianPaths.EnsureCreated();
            var path = Path.Combine(GuardianPaths.Root, "startup-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else can be done here.
        }
    }
}
