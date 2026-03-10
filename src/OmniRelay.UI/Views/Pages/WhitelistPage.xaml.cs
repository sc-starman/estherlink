using System.Windows.Controls;
using OmniRelay.UI.ViewModels;

namespace OmniRelay.UI.Views.Pages;

public partial class WhitelistPage : Page
{
    private readonly WhitelistViewModel _viewModel;

    public WhitelistPage(WhitelistViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_viewModel.RefreshCommand.CanExecute(null))
        {
            await _viewModel.RefreshCommand.ExecuteAsync(null);
        }
    }
}
