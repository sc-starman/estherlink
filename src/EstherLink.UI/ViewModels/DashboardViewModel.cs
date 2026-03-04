using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstherLink.Core.Status;
using EstherLink.UI.Services;
using System.ComponentModel;

namespace EstherLink.UI.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;

    public DashboardViewModel(GatewayOrchestratorService orchestrator, GatewayStateStore state)
    {
        _orchestrator = orchestrator;
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        RefreshFromState();
    }

    [ObservableProperty]
    private string serviceState = "Unknown";

    [ObservableProperty]
    private string proxyState = "Unknown";

    [ObservableProperty]
    private string licenseState = "Unknown";

    [ObservableProperty]
    private string tunnelState = "Unknown";

    [ObservableProperty]
    private int whitelistCount;

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    private bool CanRunCommands() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        VerifyLicenseCommand.NotifyCanExecuteChanged();
        StartProxyCommand.NotifyCanExecuteChanged();
        StopProxyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRunCommands))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.RefreshStatusAsync();
            Feedback = result.Message;
            RefreshFromState();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunCommands))]
    private async Task VerifyLicenseAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.VerifyLicenseAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshFromState();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunCommands))]
    private async Task StartProxyAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.StartProxyAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshFromState();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunCommands))]
    private async Task StopProxyAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.StopProxyAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshFromState();
        });
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayStateStore.Status) or nameof(GatewayStateStore.ServiceState))
        {
            RefreshFromState();
        }
    }

    private void RefreshFromState()
    {
        var status = _state.Status;
        ServiceState = _state.ServiceState;
        ProxyState = status is null ? "Unavailable" : (status.ProxyRunning ? "Running" : "Stopped");
        LicenseState = status is null ? "Unavailable" : (status.LicenseValid ? "Valid" : "Invalid");
        TunnelState = status is null ? "Unavailable" : (status.TunnelConnected ? "Connected" : "Disconnected");
        WhitelistCount = status?.WhitelistCount ?? 0;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
