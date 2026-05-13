using System.Windows;
using OmniRelay.Core.Configuration;

namespace OmniRelay.UI.Views.Dialogs;

public partial class RelayTypeDialog : Window
{
    public RelayTypeDialog()
    {
        InitializeComponent();
    }

    public string SelectedGatewayType { get; private set; } = GatewayTypes.Remote;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SelectedGatewayType = GatewayTypeCombo.SelectedIndex == 1 ? GatewayTypes.Local : GatewayTypes.Remote;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
