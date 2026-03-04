using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstherLink.UI.Services;

namespace EstherLink.UI.ViewModels;

public partial class NetworkConfigViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    public GatewayStateStore State { get; }

    public NetworkConfigViewModel(GatewayOrchestratorService orchestrator, GatewayStateStore state)
    {
        _orchestrator = orchestrator;
        State = state;
    }

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    private bool CanApplyConfig() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        ApplyConfigCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApplyConfig))]
    private async Task ApplyConfigAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _orchestrator.ApplyConfigAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
