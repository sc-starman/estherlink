using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.UI.Services;

namespace OmniRelay.UI.ViewModels;

public partial class WhitelistViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    public GatewayStateStore State { get; }

    public WhitelistViewModel(GatewayOrchestratorService orchestrator, GatewayStateStore state)
    {
        _orchestrator = orchestrator;
        State = state;
    }

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private string validationSummary = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        ValidateCommand.NotifyCanExecuteChanged();
        UpdateWhitelistCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Validate()
    {
        var errors = _orchestrator.ValidateWhitelistLines();
        ValidationSummary = errors.Count == 0
            ? "Whitelist syntax looks valid."
            : string.Join(Environment.NewLine, errors);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateWhitelistAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var errors = _orchestrator.ValidateWhitelistLines();
        if (errors.Count > 0)
        {
            ValidationSummary = string.Join(Environment.NewLine, errors);
            Feedback = "Whitelist contains invalid entries.";
            IsBusy = false;
            return;
        }

        try
        {
            var result = await _orchestrator.UpdateWhitelistAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
