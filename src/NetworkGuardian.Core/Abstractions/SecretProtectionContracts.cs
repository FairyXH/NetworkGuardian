namespace NetworkGuardian.Core.Abstractions;

/// <summary>
/// Protects a secret so it can be stored on disk without keeping the clear text there.
/// Implemented with DPAPI (current user) on Windows; tests use an in-memory stand-in.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Name of the protection mechanism, for logs and the UI.</summary>
    string Name { get; }

    /// <summary>Protects <paramref name="clearText"/> and returns a storable blob (base64).</summary>
    string Protect(string clearText);

    /// <summary>
    /// Reverses <see cref="Protect"/>. Null when the blob cannot be unprotected (another user or
    /// another machine), which is a recoverable condition: the entry stays but cannot authenticate.
    /// </summary>
    string? Unprotect(string protectedBlob);
}
