using NetworkGuardian.Windows.Helper;

namespace NetworkGuardian.Helper;

/// <summary>
/// Standalone entry point of the privileged helper.
/// </summary>
/// <remarks>
/// The implementation lives in <see cref="HelperEntry"/> (NetworkGuardian.Windows) so the portable
/// single-file application can host the same code behind its <c>--helper</c> switch. This executable
/// is kept for deployments that prefer a visibly separate elevated binary and for the verification
/// script, which exercises the helper without elevation.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args) => HelperEntry.Run(args);
}
