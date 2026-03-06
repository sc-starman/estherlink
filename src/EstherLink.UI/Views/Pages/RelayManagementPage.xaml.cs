using System.Windows.Controls;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Views.Pages;

public partial class RelayManagementPage : Page
{
    public RelayManagementPage(RelayManagementViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
