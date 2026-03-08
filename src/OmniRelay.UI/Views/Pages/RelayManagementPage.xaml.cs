using System.Windows.Controls;
using OmniRelay.UI.ViewModels;

namespace OmniRelay.UI.Views.Pages;

public partial class RelayManagementPage : Page
{
    public RelayManagementPage(RelayManagementViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
