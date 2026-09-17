using System.Runtime.InteropServices;
using Windows.Storage.Pickers;

namespace NetworkGuardian.App.Services;

/// <summary>
/// File pickers for unpackaged desktop apps. <c>WinRT.Interop.InitializeWithWindow</c> is applied
/// through the raw COM interface so the behaviour does not depend on projection helpers.
/// </summary>
public static class StorageFilePicker
{
    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    public static async Task<string?> PickExecutableAsync(IntPtr ownerWindow)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };

            ((IInitializeWithWindow)(object)picker).Initialize(ownerWindow);

            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".cmd");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add(".ps1");
            picker.FileTypeFilter.Add("*");

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task<string?> PickFolderAsync(IntPtr ownerWindow)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };

            ((IInitializeWithWindow)(object)picker).Initialize(ownerWindow);

            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
