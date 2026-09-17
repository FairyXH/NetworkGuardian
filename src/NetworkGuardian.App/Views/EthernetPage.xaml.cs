using Microsoft.UI.Xaml.Controls;
using NetworkGuardian.App.ViewModels;

namespace NetworkGuardian.App.Views;

public sealed partial class EthernetPage : Page
{
    public EthernetPage()
    {
        ViewModel = App.MainViewModel
                     ?? throw new InvalidOperationException("The main view model is not initialised yet.");

        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }
}
