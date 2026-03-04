using System.Windows.Controls;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Views.Pages;

public partial class NetworkConfigPage : Page
{
    public NetworkConfigPage(NetworkConfigViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
