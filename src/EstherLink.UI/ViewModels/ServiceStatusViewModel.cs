using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstherLink.Core.Status;
using EstherLink.UI.Services;
using System.ComponentModel;

namespace EstherLink.UI.ViewModels;

public partial class ServiceStatusViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;

    public ServiceStatusViewModel(GatewayOrchestratorService orchestrator, GatewayStateStore state)
    {
        _orchestrator = orchestrator;
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        RefreshView();
    }

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
        InstallStartServiceCommand.NotifyCanExecuteChanged();
        StopServiceCommand.NotifyCanExecuteChanged();
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
    private async Task InstallStartServiceAsync()
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
    private async Task StopServiceAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.StopServiceAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshView();
        });
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayStateStore.ServiceState) or nameof(GatewayStateStore.Status))
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
        finally
        {
            IsBusy = false;
        }
    }
}
