using System.Windows.Controls;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Views.Pages;

public partial class ServiceStatusPage : Page
{
    public ServiceStatusPage(ServiceStatusViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
