using System.Windows.Controls;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Views.Pages;

public partial class WhitelistPage : Page
{
    public WhitelistPage(WhitelistViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
