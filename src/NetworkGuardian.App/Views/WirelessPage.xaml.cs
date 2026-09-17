using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetworkGuardian.App.ViewModels;

namespace NetworkGuardian.App.Views;

public sealed partial class WirelessPage : Page
{
    public WirelessPage()
    {
        ViewModel = App.MainViewModel
                     ?? throw new InvalidOperationException("The main view model is not initialised yet.");

        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    private async void OnScanAdapterClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WifiAdapterViewModel adapter)
        {
            await GuardAsync(() => ViewModel.Wireless.ScanAsync(adapter), "扫描失败");
        }
    }

    private async void OnRescanAllClicked(object sender, RoutedEventArgs e)
    {
        await GuardAsync(ViewModel.RescanWifiAsync, "重新扫描失败");
    }

    private async void OnDisconnectAdapterClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WifiAdapterViewModel adapter)
        {
            await GuardAsync(() => ViewModel.Wireless.DisconnectAsync(adapter), "断开失败");
        }
    }

    private async void OnConnectNetworkClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ScannedNetworkViewModel network ||
            network.Owner is null)
        {
            return;
        }

        await GuardAsync(
            () => ViewModel.Wireless.ConnectAsync(network.Owner!, network.ProfileName),
            "连接失败");
    }

    private async void OnEnableDeviceClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceRowViewModel device)
        {
            await GuardAsync(() => ViewModel.Wireless.EnableDeviceAsync(device), "启用设备失败");
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await GuardAsync(ViewModel.Wireless.RefreshAsync, "刷新失败");
    }

    /// <summary>
    /// An exception escaping an async void handler terminates the process, so every handler funnels
    /// through here and reports the failure in the UI instead.
    /// </summary>
    private async Task GuardAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError($"{what}：{ex.Message}");
        }
    }
}
