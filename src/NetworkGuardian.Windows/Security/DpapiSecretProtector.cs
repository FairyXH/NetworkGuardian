using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;

namespace NetworkGuardian.Windows.Security;

/// <summary>
/// DPAPI based secret protection (<c>CryptProtectData</c> / <c>CryptUnprotectData</c>) scoped to the
/// current user, with a fixed application entropy.
/// </summary>
/// <remarks>
/// The .NET wrapper package is avoided on purpose: the portable build is Native AOT, and a direct
/// P/Invoke to crypt32 keeps the deployment a single file with no extra dependency. User scope is the
/// right level here - the library belongs to the signed-in account, and machine scope would let any
/// other account on the machine decrypt the campus account.
/// </remarks>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>Extra entropy so another application's DPAPI blob cannot be dropped in unnoticed.</summary>
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("NetworkGuardian.WifiLibrary.v1");

    private const int CryptProtectUiForbidden = 0x1;

    private readonly ILogger<DpapiSecretProtector> _logger;

    public DpapiSecretProtector(ILogger<DpapiSecretProtector>? logger = null) =>
        _logger = logger ?? NullLogger<DpapiSecretProtector>.Instance;

    public string Name => "DPAPI (当前用户)";

    public string Protect(string clearText)
    {
        ArgumentNullException.ThrowIfNull(clearText);

        var plainBytes = Encoding.UTF8.GetBytes(clearText);
        var protectedBytes = Transform(plainBytes, protect: true)
            ?? throw new InvalidOperationException("CryptProtectData 失败，无法保存密码");

        return Convert.ToBase64String(protectedBytes);
    }

    public string? Unprotect(string protectedBlob)
    {
        if (string.IsNullOrWhiteSpace(protectedBlob))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(protectedBlob);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Stored Wi-Fi password blob is not valid base64");
            return null;
        }

        var plainBytes = Transform(bytes, protect: false);
        return plainBytes is null ? null : Encoding.UTF8.GetString(plainBytes);
    }

    private byte[]? Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();
        var entropyBlob = new DataBlob();

        try
        {
            inputBlob.Data = Marshal.AllocHGlobal(input.Length);
            inputBlob.Size = input.Length;
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);

            entropyBlob.Data = Marshal.AllocHGlobal(Entropy.Length);
            entropyBlob.Size = Entropy.Length;
            Marshal.Copy(Entropy, 0, entropyBlob.Data, Entropy.Length);

            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref outputBlob);

            if (!ok)
            {
                _logger.LogWarning("{Operation} failed with Win32 error {Error}",
                    protect ? "CryptProtectData" : "CryptUnprotectData", Marshal.GetLastWin32Error());
                return null;
            }

            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} threw", protect ? "CryptProtectData" : "CryptUnprotectData");
            return null;
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputBlob.Data);
            }

            if (entropyBlob.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyBlob.Data);
            }

            if (outputBlob.Data != IntPtr.Zero)
            {
                // The API allocates this buffer with LocalAlloc.
                LocalFree(outputBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
