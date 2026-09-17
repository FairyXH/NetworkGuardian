using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetworkGuardian.App.ViewModels;

namespace NetworkGuardian.App.Views;

public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        ViewModel = App.MainViewModel
                     ?? throw new InvalidOperationException("The main view model is not initialised yet.");

        InitializeComponent();

        ViewModel.Logs.EntriesReloaded += OnEntriesReloaded;
        Unloaded += (_, _) => ViewModel.Logs.EntriesReloaded -= OnEntriesReloaded;
    }

    public MainViewModel ViewModel { get; }

    private void OnEntriesReloaded(object? sender, EventArgs e)
    {
        if (!ViewModel.Logs.AutoScroll || LogList.Items.Count == 0)
        {
            return;
        }

        try
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
        catch (Exception)
        {
            // Scrolling is cosmetic; ignore transient failures while the list is rebuilding.
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => ViewModel.Logs.Reload();

    private void OnClearClicked(object sender, RoutedEventArgs e) => ViewModel.Logs.Clear();

    private void OnOpenLogsClicked(object sender, RoutedEventArgs e) => ViewModel.Host.OpenLogFolder();
}
