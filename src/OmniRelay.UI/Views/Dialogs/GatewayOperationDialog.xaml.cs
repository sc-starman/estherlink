using System.ComponentModel;
using System.Reflection;
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
        var runningProp = DataContext?.GetType().GetProperty("IsGatewayOperationRunning", BindingFlags.Public | BindingFlags.Instance);
        if (runningProp?.PropertyType == typeof(bool) &&
            runningProp.GetValue(DataContext) is bool running &&
            running)
        {
            e.Cancel = true;
        }
    }
}
