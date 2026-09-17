using Microsoft.Extensions.Logging;
using NetworkGuardian.Infrastructure.Logging;

namespace NetworkGuardian.Portable.Host;

/// <summary>
/// Minimal <see cref="ILoggerFactory"/> around the rolling file logger.
/// </summary>
/// <remarks>
/// The WinUI version used <c>LoggerFactory.Create(...)</c> from Microsoft.Extensions.Logging. That
/// builder is built on dependency injection, which the Native AOT analyzer flags as reflection based,
/// so the portable app wires the single provider directly instead. The abstraction (and therefore
/// every <c>ILogger&lt;T&gt;</c> call site in the shared projects) is unchanged.
/// </remarks>
internal sealed class SimpleLoggerFactory : ILoggerFactory
{
    private readonly RollingFileLoggerProvider _provider;

    public SimpleLoggerFactory(RollingFileLoggerProvider provider) => _provider = provider;

    public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
        // Only the rolling file logger is used; providers cannot be added dynamically.
    }

    public void Dispose()
    {
        // The provider is owned by the application host and disposed there, after the last log write.
    }
}
