using OmniRelay.UI.ViewModels;
using System.ComponentModel;
using System.Windows;

namespace OmniRelay.UI.Views.Dialogs;

public partial class GatewayOperationDialog : Window
{
    public GatewayOperationDialog()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is GatewayManagementViewModel vm && vm.IsGatewayOperationRunning)
        {
            e.Cancel = true;
        }
    }
}
