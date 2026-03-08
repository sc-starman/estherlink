using System.Windows.Controls;
using OmniRelay.UI.ViewModels;

namespace OmniRelay.UI.Views.Pages;

public partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
