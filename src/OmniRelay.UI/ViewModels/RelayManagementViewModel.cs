using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.Core.Status;
using OmniRelay.UI.Services;
using System.ComponentModel;
using System.Windows.Threading;

namespace OmniRelay.UI.ViewModels;

public partial class RelayManagementViewModel : ObservableObject
{
    private static readonly TimeSpan StatusStaleThreshold = TimeSpan.FromSeconds(45);
    private const string TunnelModuleMissingReasonCode = "remote_probe_module_missing";
    private const string RemoteForwardPortInUseReasonCode = "remote_forward_port_in_use";
    private const string TunnelModulePath = "/usr/local/sbin/omnirelay-tunnelctl";

    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    private readonly IServiceControlService _serviceControl;
    private readonly DispatcherTimer _staleTimer;

    public RelayManagementViewModel(
        GatewayOrchestratorService orchestrator,
        GatewayStateStore state,
        IServiceControlService serviceControl)
    {
        _orchestrator = orchestrator;
        _state = state;
        _serviceControl = serviceControl;
        _state.PropertyChanged += OnStateChanged;
        _staleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _staleTimer.Tick += (_, _) => RefreshDerivedStatus();
        _staleTimer.Start();
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

    [ObservableProperty]
    private string effectiveTunnelState = "Unknown";

    [ObservableProperty]
    private string healthStateDisplay = "Unknown";

    [ObservableProperty]
    private string healthReasonDisplay = string.Empty;

    [ObservableProperty]
    private bool statusStale;

    [ObservableProperty]
    private bool showTunnelModuleNote;

    [ObservableProperty]
    private string tunnelModuleNote = string.Empty;

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
        RefreshDerivedStatus();
    }

    private void RefreshDerivedStatus()
    {
        var snapshot = Status;
        if (snapshot is null)
        {
            StatusStale = true;
            EffectiveTunnelState = "Unavailable";
            HealthStateDisplay = "Unavailable";
            HealthReasonDisplay = "status_unavailable";
            ShowTunnelModuleNote = false;
            TunnelModuleNote = string.Empty;
            return;
        }

        var stale = !snapshot.LastStatusUpdateUtc.HasValue ||
                    DateTimeOffset.UtcNow - snapshot.LastStatusUpdateUtc.Value > StatusStaleThreshold;
        StatusStale = stale;
        if (stale)
        {
            EffectiveTunnelState = "Disconnected (stale)";
            HealthStateDisplay = "Disconnected (stale)";
            HealthReasonDisplay = string.IsNullOrWhiteSpace(snapshot.HealthReasonCode)
                ? "status_stale"
                : snapshot.HealthReasonCode!;
            UpdateTunnelModuleNote(snapshot, HealthReasonDisplay);
            return;
        }

        EffectiveTunnelState = !string.IsNullOrWhiteSpace(snapshot.TunnelState)
            ? snapshot.TunnelState
            : snapshot.TunnelConnected ? "Healthy" : "Disconnected";
        HealthStateDisplay = !string.IsNullOrWhiteSpace(snapshot.HealthState)
            ? snapshot.HealthState
            : snapshot.TunnelConnected ? "Healthy" : "Disconnected";
        HealthReasonDisplay = string.IsNullOrWhiteSpace(snapshot.HealthReasonCode)
            ? snapshot.TunnelLastError ?? string.Empty
            : snapshot.HealthReasonCode;
        UpdateTunnelModuleNote(snapshot, HealthReasonDisplay);
    }

    private void UpdateTunnelModuleNote(GatewayStatus snapshot, string? resolvedHealthReason)
    {
        var reason = resolvedHealthReason ?? string.Empty;
        var lastError = snapshot.TunnelLastError ?? string.Empty;
        var recoveryAction = snapshot.RecoveryAction ?? string.Empty;
        var moduleMissing =
            reason.Contains(TunnelModuleMissingReasonCode, StringComparison.OrdinalIgnoreCase) ||
            lastError.Contains("omnirelay-tunnelctl", StringComparison.OrdinalIgnoreCase) ||
            lastError.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
            recoveryAction.Contains("module_missing", StringComparison.OrdinalIgnoreCase);

        var remoteForwardPortInUse =
            reason.Contains(RemoteForwardPortInUseReasonCode, StringComparison.OrdinalIgnoreCase) ||
            lastError.Contains("remote-forward port", StringComparison.OrdinalIgnoreCase) ||
            lastError.Contains("remote port forwarding failed for listen port", StringComparison.OrdinalIgnoreCase);

        ShowTunnelModuleNote = moduleMissing || remoteForwardPortInUse;
        if (moduleMissing)
        {
            TunnelModuleNote =
                $"Remote tunnel module is not installed on the gateway yet ({TunnelModulePath}). " +
                "Tier 3 remote remediation is skipped until a gateway install deploys tunnelctl.";
            return;
        }

        TunnelModuleNote = remoteForwardPortInUse
            ? "Gateway remote-forward port is already owned by another relay/session. " +
              "Stop the other relay session or change Tunnel Remote Port."
            : string.Empty;
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
