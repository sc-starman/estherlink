using System.Windows.Controls;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Views.Pages;

public partial class GatewayManagementPage : Page
{
    public GatewayManagementPage(GatewayManagementViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
