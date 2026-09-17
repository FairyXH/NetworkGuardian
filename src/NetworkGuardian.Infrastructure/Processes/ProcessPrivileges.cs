using System.Security.Principal;

namespace NetworkGuardian.Infrastructure.Processes;

/// <summary>Answers whether the current process already runs with administrator rights.</summary>
public static class ProcessPrivileges
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
