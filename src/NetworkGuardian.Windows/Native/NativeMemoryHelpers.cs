using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetworkGuardian.Windows.Native;

/// <summary>
/// Owns the WLAN client handle returned by <c>WlanOpenHandle</c>. Released exactly once through
/// <c>WlanCloseHandle</c>.
/// </summary>
internal sealed class WlanClientHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public WlanClientHandle()
        : base(true)
    {
    }

    public WlanClientHandle(IntPtr handle)
        : base(true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => WlanApiNative.WlanCloseHandle(handle, IntPtr.Zero) == 0;
}

/// <summary>Copies a string out of a native wide char array and frees the WLAN buffer afterwards.</summary>
internal static unsafe class NativeStringHelper
{
    /// <summary>Reads a null terminated wide string starting at <paramref name="basePtr"/> + offset.</summary>
    public static string ReadFixedString(IntPtr basePtr, int offset, int maxChars = 256)
    {
        if (basePtr == IntPtr.Zero)
        {
            return string.Empty;
        }

        var value = Marshal.PtrToStringUni(IntPtr.Add(basePtr, offset), maxChars) ?? string.Empty;
        var terminator = value.IndexOf('\0');
        if (terminator >= 0)
        {
            value = value[..terminator];
        }

        return value.Trim();
    }

    /// <summary>
    /// Decodes an SSID byte array. SSIDs are raw bytes and are usually UTF-8, but some enterprise
    /// gear emits legacy code pages, so a strict UTF-8 decode is attempted first.
    /// </summary>
    public static string ReadSsid(byte* data, uint length)
    {
        if (data == null || length == 0)
        {
            return string.Empty;
        }

        var count = (int)Math.Min(length, 32u);
        var bytes = new byte[count];
        Marshal.Copy((IntPtr)data, bytes, 0, count);

        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.GetEncoding(28591).GetString(bytes);
        }
    }

    public static string FormatMac(byte* data, int length = 6)
    {
        if (data == null || length <= 0)
        {
            return string.Empty;
        }

        var bytes = new byte[length];
        Marshal.Copy((IntPtr)data, bytes, 0, length);
        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    public static string FormatMac(ReadOnlySpan<byte> data) =>
        string.Join(':', data.ToArray().Select(b => b.ToString("X2")));
}
