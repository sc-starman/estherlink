using System.Windows.Controls;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Views.Pages;

public partial class LicensePage : Page
{
    public LicensePage(LicenseViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
