using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.Core.Status;
using OmniRelay.UI.Services;
using System.ComponentModel;

namespace OmniRelay.UI.ViewModels;

public partial class RelayManagementViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    private readonly IServiceControlService _serviceControl;

    public RelayManagementViewModel(
        GatewayOrchestratorService orchestrator,
        GatewayStateStore state,
        IServiceControlService serviceControl)
    {
        _orchestrator = orchestrator;
        _state = state;
        _serviceControl = serviceControl;
        _state.PropertyChanged += OnStateChanged;
        RefreshView();
    }

    public GatewayStateStore State => _state;

    [ObservableProperty]
    private string serviceState = "Unknown";

    [ObservableProperty]
    private GatewayStatus? status;

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyRelayConfigCommand.NotifyCanExecuteChanged();
        InstallStartRelayCommand.NotifyCanExecuteChanged();
        StopRelayCommand.NotifyCanExecuteChanged();
        UninstallRelayCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.RefreshStatusAsync();
            Feedback = result.Message;
            RefreshView();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyRelayConfigAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.ApplyRelayConfigAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshView();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallStartRelayAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.InstallStartServiceAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshView();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StopRelayAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.StopServiceAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshView();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallRelayAsync()
    {
        await RunBusyAsync(async () =>
        {
            var stopped = await _orchestrator.StopServiceAsync();
            var uninstalled = await _serviceControl.UninstallWindowsServiceAsync();
            Feedback = uninstalled
                ? "Relay service uninstall requested."
                : $"Relay service uninstall failed: {stopped.Message}";

            await _orchestrator.RefreshStatusAsync();
            RefreshView();
        });
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayStateStore.Status) or nameof(GatewayStateStore.ServiceState))
        {
            RefreshView();
        }
    }

    private void RefreshView()
    {
        ServiceState = _state.ServiceState;
        Status = _state.Status;
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
        catch (Exception ex)
        {
            Feedback = $"Operation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
