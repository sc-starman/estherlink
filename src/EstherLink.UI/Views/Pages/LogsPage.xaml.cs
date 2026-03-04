using System.Windows.Controls;
using System.Windows.Threading;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Views.Pages;

public partial class LogsPage : Page
{
    private readonly LogsViewModel _viewModel;
    private readonly DispatcherTimer _tailTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public LogsPage(LogsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += LogsPage_Loaded;
        Unloaded += LogsPage_Unloaded;
        _tailTimer.Tick += TailTimer_Tick;
    }

    private async void LogsPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        _tailTimer.Start();
    }

    private void LogsPage_Unloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _tailTimer.Stop();
    }

    private async void TailTimer_Tick(object? sender, EventArgs e)
    {
        if (_viewModel.TailMode)
        {
            await _viewModel.RefreshCommand.ExecuteAsync(null);
        }
    }
}
