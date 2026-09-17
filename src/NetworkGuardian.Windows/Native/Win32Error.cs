using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NetworkGuardian.Windows.Native;

/// <summary>Helpers for turning Win32 / WLAN error codes into actionable messages.</summary>
public static class Win32Error
{
    private const int FORMAT_MESSAGE_ALLOCATE_BUFFER = 0x00000100;
    private const int FORMAT_MESSAGE_IGNORE_INSERTS = 0x00000200;
    private const int FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;

    public const int ErrorSuccess = 0;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorNotSupported = 50;
    public const int ErrorInvalidState = 5023;
    public const int ErrorServiceNotActive = 1062;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorDeviceNotConnected = 1167;
    public const int ErrorFileNotFound = 2;

    /// <summary>FormatMessage-backed description; falls back to the numeric value.</summary>
    public static string Describe(int code)
    {
        if (code == ErrorSuccess)
        {
            return "ERROR_SUCCESS";
        }

        var buffer = IntPtr.Zero;
        try
        {
            var length = FormatMessage(
                FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                IntPtr.Zero,
                code,
                0,
                ref buffer,
                0,
                IntPtr.Zero);

            if (length > 0 && buffer != IntPtr.Zero)
            {
                var message = Marshal.PtrToStringUni(buffer)?.Trim() ?? string.Empty;
                return $"{Name(code)}: {message}";
            }
        }
        catch (Exception)
        {
            // Fall through to the numeric form: error formatting must never throw.
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                LocalFree(buffer);
            }
        }

        return $"{Name(code)} (0x{code:X8})";
    }

    public static string Name(int code) => code switch
    {
        ErrorSuccess => "ERROR_SUCCESS",
        ErrorAccessDenied => "ERROR_ACCESS_DENIED",
        ErrorInvalidParameter => "ERROR_INVALID_PARAMETER",
        ErrorNotSupported => "ERROR_NOT_SUPPORTED",
        ErrorInvalidState => "ERROR_INVALID_STATE",
        ErrorServiceNotActive => "ERROR_SERVICE_NOT_ACTIVE",
        ErrorNoMoreItems => "ERROR_NO_MORE_ITEMS",
        ErrorInsufficientBuffer => "ERROR_INSUFFICIENT_BUFFER",
        ErrorDeviceNotConnected => "ERROR_DEVICE_NOT_CONNECTED",
        ErrorFileNotFound => "ERROR_FILE_NOT_FOUND",
        _ => $"ERROR_{code}",
    };

    private const int HResultAccessDenied = unchecked((int)0x80070005);

    public static bool IsAccessDenied(int code) => code == ErrorAccessDenied || code == HResultAccessDenied;

    /// <summary>Standard message shape used across the project.</summary>
    public static string Build(string api, int code, string? context = null)
    {
        var builder = new StringBuilder();
        builder.Append(api).Append(" failed: ").Append(Describe(code));
        if (!string.IsNullOrWhiteSpace(context))
        {
            builder.Append(" [").Append(context).Append(']');
        }

        return builder.ToString();
    }

    public static Win32Exception ToException(string api, int code, string? context = null)
    {
        var formatted = Build(api, code, context);
        try
        {
            return new Win32Exception(code, formatted);
        }
        catch (Exception)
        {
            return new Win32Exception(formatted);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "FormatMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int FormatMessage(
        int dwFlags,
        IntPtr lpSource,
        int dwMessageId,
        int dwLanguageId,
        ref IntPtr lpBuffer,
        int nSize,
        IntPtr arguments);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
