using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetworkGuardian.App.Services;
using NetworkGuardian.App.ViewModels;

namespace NetworkGuardian.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.MainViewModel
                     ?? throw new InvalidOperationException("The main view model is not initialised yet.");

        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    private static IntPtr OwnerHandle => App.Instance?.WindowHandle ?? IntPtr.Zero;

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.Settings.ReportStatus($"保存异常：{ex.Message}");
        }
    }

    private void OnReloadClicked(object sender, RoutedEventArgs e) => ViewModel.ReloadSettings();

    private void OnOpenLogsClicked(object sender, RoutedEventArgs e) => ViewModel.Host.OpenLogFolder();

    private async void OnBrowseCampusAuthClicked(object sender, RoutedEventArgs e)
    {
        var path = await StorageFilePicker.PickExecutableAsync(OwnerHandle);
        if (!string.IsNullOrEmpty(path))
        {
            ViewModel.Settings.CampusAuthPath = path;
        }
    }

    private async void OnBrowseCampusAuthWorkDirClicked(object sender, RoutedEventArgs e)
    {
        var path = await StorageFilePicker.PickFolderAsync(OwnerHandle);
        if (!string.IsNullOrEmpty(path))
        {
            ViewModel.Settings.CampusAuthWorkingDirectory = path;
        }
    }

    private void OnAddCommandClicked(object sender, RoutedEventArgs e) => ViewModel.Settings.AddCommand();

    private void OnRemoveCommandClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommandDefinitionViewModel command)
        {
            ViewModel.Settings.RemoveCommand(command);
        }
    }

    private async void OnBrowseCommandClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommandDefinitionViewModel command)
        {
            return;
        }

        var path = await StorageFilePicker.PickExecutableAsync(OwnerHandle);
        if (!string.IsNullOrEmpty(path))
        {
            command.ExecutablePath = path;
        }
    }

    private async void OnBrowseCommandWorkDirClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommandDefinitionViewModel command)
        {
            return;
        }

        var path = await StorageFilePicker.PickFolderAsync(OwnerHandle);
        if (!string.IsNullOrEmpty(path))
        {
            command.WorkingDirectory = path;
        }
    }
}
