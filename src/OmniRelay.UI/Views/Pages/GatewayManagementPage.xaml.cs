using System.Windows.Controls;
using OmniRelay.UI.ViewModels;

namespace OmniRelay.UI.Views.Pages;

public partial class GatewayManagementPage : Page
{
    public GatewayManagementPage(GatewayManagementViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
