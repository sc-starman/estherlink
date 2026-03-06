using System.Windows.Controls;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Views.Pages;

public partial class LicensePage : Page
{
    private readonly LicenseViewModel _viewModel;

    public LicensePage(LicenseViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += LicensePage_Loaded;
    }

    private async void LicensePage_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= LicensePage_Loaded;
        await _viewModel.InitializeAsync();
    }
}
