using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstherLink.Core.Configuration;
using EstherLink.UI.Services;
using Microsoft.Win32;
using System.ComponentModel;

namespace EstherLink.UI.ViewModels;

public partial class NetworkConfigViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    public GatewayStateStore State => _state;

    public NetworkConfigViewModel(GatewayOrchestratorService orchestrator, GatewayStateStore state)
    {
        _orchestrator = orchestrator;
        _state = state;
        _state.PropertyChanged += OnStatePropertyChanged;
    }

    public bool IsHostKeyAuthSelected =>
        string.Equals(TunnelAuthMethods.Normalize(State.TunnelAuthMethod), TunnelAuthMethods.HostKey, StringComparison.Ordinal);

    public bool IsPasswordAuthSelected =>
        string.Equals(TunnelAuthMethods.Normalize(State.TunnelAuthMethod), TunnelAuthMethods.Password, StringComparison.Ordinal);

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    private bool CanApplyConfig() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        ApplyConfigCommand.NotifyCanExecuteChanged();
        TestTunnelConnectionCommand.NotifyCanExecuteChanged();
        BrowseTunnelKeyCommand.NotifyCanExecuteChanged();
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

    [RelayCommand(CanExecute = nameof(CanApplyConfig))]
    private async Task TestTunnelConnectionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _orchestrator.TestTunnelConnectionAsync();
            Feedback = result.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyConfig))]
    private void BrowseTunnelKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select SSH private key file",
            Filter = "SSH Key files|id_*;*.pem;*.ppk;*.*|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            State.TunnelKeyPath = dialog.FileName;
        }
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GatewayStateStore.TunnelAuthMethod))
        {
            OnPropertyChanged(nameof(IsHostKeyAuthSelected));
            OnPropertyChanged(nameof(IsPasswordAuthSelected));
        }
    }
}
