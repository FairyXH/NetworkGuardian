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
            await ViewModel.Wireless.ScanAsync(adapter);
        }
    }

    private async void OnRescanAllClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.RescanWifiAsync();
    }

    private async void OnDisconnectAdapterClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WifiAdapterViewModel adapter)
        {
            await ViewModel.Wireless.DisconnectAsync(adapter);
        }
    }

    private async void OnConnectNetworkClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ScannedNetworkViewModel network ||
            network.Owner is null)
        {
            return;
        }

        await ViewModel.Wireless.ConnectAsync(network.Owner, network.ProfileName);
    }

    private async void OnEnableDeviceClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceRowViewModel device)
        {
            await ViewModel.Wireless.EnableDeviceAsync(device);
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.Wireless.RefreshAsync();
    }
}
