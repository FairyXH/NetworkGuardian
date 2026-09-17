using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetworkGuardian.App.ViewModels;
using NetworkGuardian.App.Views;

namespace NetworkGuardian.App;

/// <summary>Shell window: header, navigation and page host.</summary>
public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _toastTimer;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        Title = "NetworkGuardian - Windows 网络保活";
        _toastTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(4);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };

        TryApplyBackdrop();
        ConfigureWindow();
    }

    public MainViewModel ViewModel { get; private set; } = null!;

    public void AttachViewModel(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewModel.ToastRequested += (_, message) => ShowToast(message);

        ContentFrame.Navigate(typeof(DashboardPage));

        NavView.SelectedItem = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => (item.Tag as string) == "dashboard");
    }

    private void TryApplyBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch (Exception)
        {
            // Mica is unavailable on some configurations (or with transparency disabled).
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            var appWindow = AppWindow;
            appWindow.Title = "NetworkGuardian - Windows 网络保活";

            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "NetworkGuardian.ico");
            if (File.Exists(icon))
            {
                appWindow.SetIcon(icon);
            }

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 900;
                presenter.PreferredMinimumHeight = 600;
            }

            appWindow.Resize(new global::Windows.Graphics.SizeInt32(1180, 800));

            appWindow.Closing += (_, args) =>
            {
                if (_allowClose)
                {
                    return;
                }

                // Decide between "hide to tray" and a real shutdown.
                if (!HandleCloseRequest(out var cancel))
                {
                    return;
                }

                args.Cancel = cancel;
            };

            appWindow.Changed += (_, _) =>
            {
                if (_allowClose)
                {
                    return;
                }

                var minimizeToTray = App.Host?.Config.Startup.MinimizeToTray ?? false;
                if (!minimizeToTray)
                {
                    return;
                }

                if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                {
                    App.Instance?.HideMainWindow();
                }
            };
        }
        catch (Exception)
        {
            // Window cosmetics must never prevent startup.
        }
    }

    private bool HandleCloseRequest(out bool cancel)
    {
        cancel = false;

        var app = App.Instance;
        if (app is null)
        {
            _allowClose = true;
            return false;
        }

        var closeToTray = App.Host?.Config.Startup.CloseToTray ?? false;
        if (closeToTray)
        {
            app.HideMainWindow();
            cancel = true;
            return true;
        }

        // Real shutdown: run cleanup first, then close for real.
        cancel = true;
        _ = app.ShutdownAsync().ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _allowClose = true;
                Close();
            });
        });

        return true;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "dashboard" => typeof(DashboardPage),
            "wireless" => typeof(WirelessPage),
            "ethernet" => typeof(EthernetPage),
            "settings" => typeof(SettingsPage),
            "logs" => typeof(LogsPage),
            _ => typeof(DashboardPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e) => ViewModel?.TogglePause();

    private void OnConnectivityTestClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            _ = ViewModel.RunConnectivityTestAsync();
        }
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
