using System.Windows.Controls;
using OmniRelay.UI.ViewModels;

namespace OmniRelay.UI.Views.Pages;

public partial class WhitelistPage : Page
{
    public WhitelistPage(WhitelistViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
