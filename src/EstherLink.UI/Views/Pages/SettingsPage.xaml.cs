using System.Windows.Controls;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}