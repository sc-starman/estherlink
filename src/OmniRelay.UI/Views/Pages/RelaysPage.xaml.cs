using System.Windows.Controls;
using System.Windows.Input;
using OmniRelay.UI.ViewModels;

namespace OmniRelay.UI.Views.Pages;

public partial class RelaysPage : Page
{
    private readonly RelaysViewModel _viewModel;

    public RelaysPage(RelaysViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void RelaysGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.EditRelayCommand.CanExecute(null))
        {
            _viewModel.EditRelayCommand.Execute(null);
        }
    }
}
