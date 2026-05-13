using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using OmniRelay.UI.Services;
using OmniRelay.UI.ViewModels;

namespace OmniRelay.UI;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly INavigationService _navigationService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ITrayIconService _trayIconService;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        IServiceProvider serviceProvider,
        ITrayIconService trayIconService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _navigationService = navigationService;
        _serviceProvider = serviceProvider;
        _trayIconService = trayIconService;

        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        _navigationService.Initialize(ContentFrame, _serviceProvider);
        await _viewModel.InitializeAsync();
    }

    private void FooterLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch
        {
            // Ignore link launch failures.
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_trayIconService.AllowWindowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
