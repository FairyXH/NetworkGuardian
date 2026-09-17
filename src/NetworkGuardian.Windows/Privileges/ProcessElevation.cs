using System.Security.Principal;

namespace NetworkGuardian.Windows.Privileges;

/// <summary>
/// Elevation detection for the Windows layer. Kept separate from the Infrastructure helper so the
/// Windows project has no dependency on Infrastructure.
/// </summary>
public static class ProcessElevation
{
    private static readonly Lazy<bool> Elevated = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsElevated() => Elevated.Value;

    private static bool Detect()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity is null)
            {
                return false;
            }

            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
