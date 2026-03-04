using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstherLink.Core.Configuration;
using EstherLink.Core.Status;
using EstherLink.UI.Models;
using EstherLink.UI.Services;
using EstherLink.UI.Views.Dialogs;
using System.ComponentModel;
using System.Text;
using System.Windows;

namespace EstherLink.UI.ViewModels;

public partial class ServiceStatusViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    private readonly IServiceControlService _serviceControl;
    private readonly IGatewayBundleResolverService _bundleResolver;
    private readonly IGatewayDeploymentService _gatewayDeployment;
    private readonly IGatewayHealthService _gatewayHealth;
    private readonly IDeploymentProgressAggregator _progressAggregator;
    private readonly ISudoSessionSecretCache _sudoCache;

    private GatewayBundleDescriptor? _bundle;
    private readonly StringBuilder _operationLogBuilder = new();

    public ServiceStatusViewModel(
        GatewayOrchestratorService orchestrator,
        GatewayStateStore state,
        IServiceControlService serviceControl,
        IGatewayBundleResolverService bundleResolver,
        IGatewayDeploymentService gatewayDeployment,
        IGatewayHealthService gatewayHealth,
        IDeploymentProgressAggregator progressAggregator,
        ISudoSessionSecretCache sudoCache)
    {
        _orchestrator = orchestrator;
        _state = state;
        _serviceControl = serviceControl;
        _bundleResolver = bundleResolver;
        _gatewayDeployment = gatewayDeployment;
        _gatewayHealth = gatewayHealth;
        _progressAggregator = progressAggregator;
        _sudoCache = sudoCache;

        _state.PropertyChanged += OnStateChanged;
        RefreshView();
        RefreshBundleDescriptor();
    }

    [ObservableProperty]
    private string serviceState = "Unknown";

    [ObservableProperty]
    private GatewayStatus? status;

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string gatewayServiceState = "Unknown";

    [ObservableProperty]
    private GatewayHealthReport? gatewayHealthReport;

    [ObservableProperty]
    private string gatewayHealthSummary = "Not checked";

    [ObservableProperty]
    private string bundleVersion = "Unavailable";

    [ObservableProperty]
    private int overallProgressPercent;

    [ObservableProperty]
    private string overallProgressMessage = "Idle";

    [ObservableProperty]
    private string operationLog = string.Empty;

    [ObservableProperty]
    private bool hasCachedSudoPassword;

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        InstallStartServiceCommand.NotifyCanExecuteChanged();
        StopServiceCommand.NotifyCanExecuteChanged();
        UninstallRelayServiceCommand.NotifyCanExecuteChanged();
        InstallGatewayCommand.NotifyCanExecuteChanged();
        StartGatewayCommand.NotifyCanExecuteChanged();
        StopGatewayCommand.NotifyCanExecuteChanged();
        UninstallGatewayCommand.NotifyCanExecuteChanged();
        HealthCheckGatewayCommand.NotifyCanExecuteChanged();
        InstallStartAllCommand.NotifyCanExecuteChanged();
        ClearCachedSudoPasswordCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.RefreshStatusAsync();
            Feedback = result.Message;
            RefreshView();

            var sudo = _sudoCache.Get();
            HasCachedSudoPassword = !string.IsNullOrWhiteSpace(sudo);
            if (!string.IsNullOrWhiteSpace(sudo))
            {
                await RefreshGatewayStatusAsync(sudo!);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallStartServiceAsync()
    {
        await RunBusyAsync(async () =>
        {
            ReportProgress(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.Relay,
                Percent = 10,
                Message = "Installing/starting Relay service"
            }, useOverallAggregator: false);

            var result = await _orchestrator.InstallStartServiceAsync();
            Feedback = result.Message;
            AppendLog(result.Message);

            ReportProgress(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.Relay,
                Percent = result.Success ? 100 : 0,
                Message = result.Success ? "Relay service ready" : "Relay service failed"
            }, useOverallAggregator: false);

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
            AppendLog(result.Message);
            await _orchestrator.RefreshStatusAsync();
            RefreshView();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallRelayServiceAsync()
    {
        await RunBusyAsync(async () =>
        {
            var stopped = await _orchestrator.StopServiceAsync();
            AppendLog(stopped.Message);

            var uninstalled = await _serviceControl.UninstallWindowsServiceAsync();
            Feedback = uninstalled
                ? "Relay service uninstall requested."
                : "Relay service uninstall canceled or failed.";
            AppendLog(Feedback);

            await _orchestrator.RefreshStatusAsync();
            RefreshView();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallGatewayAsync()
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = "Gateway install canceled. Sudo password is required.";
                AppendLog(Feedback);
                return;
            }

            RefreshBundleDescriptor();
            var request = BuildGatewayRequest();

            var op = await _gatewayDeployment.InstallGatewayAsync(
                request,
                sudo,
                new Progress<DeploymentProgressSnapshot>(x => ReportProgress(x, useOverallAggregator: false)));

            Feedback = op.Message;
            AppendLog(op.Message);
            await RefreshGatewayStatusAsync(sudo);
            await RefreshGatewayHealthAsync(request, sudo);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartGatewayAsync()
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = "Gateway start canceled. Sudo password is required.";
                return;
            }

            var request = BuildGatewayRequest();
            var op = await _gatewayDeployment.StartGatewayAsync(
                request,
                sudo,
                new Progress<DeploymentProgressSnapshot>(x => ReportProgress(x, useOverallAggregator: false)));

            Feedback = op.Message;
            AppendLog(op.Message);
            await RefreshGatewayStatusAsync(sudo);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StopGatewayAsync()
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = "Gateway stop canceled. Sudo password is required.";
                return;
            }

            var request = BuildGatewayRequest();
            var op = await _gatewayDeployment.StopGatewayAsync(
                request,
                sudo,
                new Progress<DeploymentProgressSnapshot>(x => ReportProgress(x, useOverallAggregator: false)));

            Feedback = op.Message;
            AppendLog(op.Message);
            await RefreshGatewayStatusAsync(sudo);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallGatewayAsync()
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = "Gateway uninstall canceled. Sudo password is required.";
                return;
            }

            var request = BuildGatewayRequest();
            var op = await _gatewayDeployment.UninstallGatewayAsync(
                request,
                sudo,
                new Progress<DeploymentProgressSnapshot>(x => ReportProgress(x, useOverallAggregator: false)));

            Feedback = op.Message;
            AppendLog(op.Message);
            await RefreshGatewayStatusAsync(sudo);
            GatewayHealthReport = null;
            GatewayHealthSummary = "Not checked";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task HealthCheckGatewayAsync()
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = "Gateway health check canceled. Sudo password is required.";
                return;
            }

            var request = BuildGatewayRequest();
            await RefreshGatewayHealthAsync(request, sudo);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallStartAllAsync()
    {
        await RunBusyAsync(async () =>
        {
            try
            {
                OperationLog = string.Empty;
                _operationLogBuilder.Clear();

                ReportProgress(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.Relay,
                    Percent = 10,
                    Message = "Starting combined deployment"
                }, useOverallAggregator: true);

                AppendLog("[Combined] Installing/starting Relay service...");
                var relay = await _orchestrator.InstallStartServiceAsync();
                AppendLog(relay.Message);
                if (!relay.Success)
                {
                    Feedback = $"Combined operation failed at Relay step: {relay.Message}";
                    return;
                }

                await _orchestrator.RefreshStatusAsync();
                RefreshView();

                ReportProgress(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.Relay,
                    Percent = 100,
                    Message = "Relay ready"
                }, useOverallAggregator: true);

                var sudo = EnsureSudoPassword();
                if (sudo is null)
                {
                    Feedback = "Combined operation canceled. Sudo password is required for Gateway steps.";
                    AppendLog(Feedback);
                    return;
                }

                RefreshBundleDescriptor();
                var request = BuildGatewayRequest();

                AppendLog("[Combined] Deploying Gateway service to VPS...");
                var gatewayInstall = await _gatewayDeployment.InstallGatewayAsync(
                    request,
                    sudo,
                    new Progress<DeploymentProgressSnapshot>(x => ReportProgress(x, useOverallAggregator: true)));

                AppendLog(gatewayInstall.Message);
                if (!gatewayInstall.Success)
                {
                    Feedback = $"Combined operation failed at Gateway install: {gatewayInstall.Message}";
                    return;
                }

                AppendLog("[Combined] Running Gateway health-check...");
                await RefreshGatewayHealthAsync(request, sudo, useOverallAggregator: true);

                await RefreshGatewayStatusAsync(sudo);
                ReportProgress(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayHealth,
                    Percent = 100,
                    Message = "Combined deployment complete"
                }, useOverallAggregator: true);

                Feedback = "Relay and Gateway services are installed/started successfully.";
                AppendLog(Feedback);
            }
            catch (Exception ex)
            {
                Feedback = $"Combined operation failed: {ex.Message}";
                AppendLog(Feedback);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ClearCachedSudoPassword()
    {
        _sudoCache.Clear();
        HasCachedSudoPassword = false;
        Feedback = "Cached sudo password cleared for this session.";
        AppendLog(Feedback);
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
        HasCachedSudoPassword = !string.IsNullOrWhiteSpace(_sudoCache.Get());
    }

    private void RefreshBundleDescriptor()
    {
        try
        {
            _bundle = _bundleResolver.Resolve();
            BundleVersion = _bundle.BundleVersion;
        }
        catch (Exception ex)
        {
            _bundle = null;
            BundleVersion = "Unavailable";
            AppendLog($"Gateway bundle unavailable: {ex.Message}");
        }
    }

    private async Task RefreshGatewayStatusAsync(string sudoPassword)
    {
        try
        {
            var request = BuildGatewayRequest();
            var status = await _gatewayDeployment.GetStatusAsync(request, sudoPassword);
            GatewayServiceState = $"x-ui={status.XuiState}, sshd={status.SshState}, fail2ban={status.Fail2BanState}";
        }
        catch (Exception ex)
        {
            GatewayServiceState = "Unavailable";
            AppendLog($"Gateway status read failed: {ex.Message}");
        }
    }

    private async Task RefreshGatewayHealthAsync(GatewayDeploymentRequest request, string sudoPassword, bool useOverallAggregator = false)
    {
        var report = await _gatewayHealth.GetHealthAsync(
            request,
            sudoPassword,
            new Progress<DeploymentProgressSnapshot>(x => ReportProgress(x, useOverallAggregator)));

        GatewayHealthReport = report;
        GatewayHealthSummary = report.Healthy
            ? $"Healthy (checked {report.CheckedAtUtc:yyyy-MM-dd HH:mm:ss} UTC)"
            : $"Unhealthy (checked {report.CheckedAtUtc:yyyy-MM-dd HH:mm:ss} UTC)";

        Feedback = GatewayHealthSummary;
        AppendLog($"Gateway health: {GatewayHealthSummary}");
    }

    private GatewayDeploymentRequest BuildGatewayRequest()
    {
        if (_bundle is null)
        {
            throw new InvalidOperationException("Gateway bundle is not available. Build the bundle and restart UI.");
        }

        var config = BuildServiceConfig();

        if (!int.TryParse(_state.GatewayPublicPortText.Trim(), out var publicPort) || publicPort <= 0)
        {
            throw new InvalidOperationException("Gateway public port must be a positive integer.");
        }

        if (!int.TryParse(_state.GatewayPanelPortText.Trim(), out var panelPort) || panelPort <= 0)
        {
            throw new InvalidOperationException("Gateway panel port must be a positive integer.");
        }

        return new GatewayDeploymentRequest
        {
            Config = config,
            BundleLocalPath = _bundle.BundleFilePath,
            BundleSha256 = _bundle.BundleSha256,
            GatewayPublicPort = publicPort,
            GatewayPanelPort = panelPort
        };
    }

    private ServiceConfig BuildServiceConfig()
    {
        if (!int.TryParse(_state.ProxyPortText.Trim(), out var proxyPort) || proxyPort <= 0)
        {
            throw new InvalidOperationException("Proxy listen port must be a positive integer.");
        }

        if (!int.TryParse(_state.TunnelSshPortText.Trim(), out var tunnelSshPort) || tunnelSshPort <= 0)
        {
            throw new InvalidOperationException("Tunnel SSH port must be a positive integer.");
        }

        if (!int.TryParse(_state.TunnelRemotePortText.Trim(), out var tunnelRemotePort) || tunnelRemotePort <= 0)
        {
            throw new InvalidOperationException("Tunnel remote port must be a positive integer.");
        }

        var authMethod = TunnelAuthMethods.Normalize(_state.TunnelAuthMethod);
        if (authMethod == TunnelAuthMethods.Password && string.IsNullOrWhiteSpace(_state.TunnelPassword))
        {
            throw new InvalidOperationException("Tunnel password is required when password authentication is selected.");
        }

        if (authMethod == TunnelAuthMethods.HostKey && string.IsNullOrWhiteSpace(_state.TunnelKeyPath))
        {
            throw new InvalidOperationException("Tunnel host key file path is required when host-key authentication is selected.");
        }

        return new ServiceConfig
        {
            LocalProxyListenPort = proxyPort,
            WhitelistAdapterIfIndex = _state.VpsAdapter?.IfIndex ?? -1,
            DefaultAdapterIfIndex = _state.OutgoingAdapter?.IfIndex ?? -1,
            TunnelHost = _state.TunnelHost.Trim(),
            TunnelSshPort = tunnelSshPort,
            TunnelRemotePort = tunnelRemotePort,
            TunnelUser = _state.TunnelUser.Trim(),
            TunnelAuthMethod = authMethod,
            TunnelPrivateKeyPath = _state.TunnelKeyPath.Trim(),
            TunnelPrivateKeyPassphrase = _state.TunnelKeyPassphrase,
            TunnelPassword = _state.TunnelPassword,
            LicenseKey = _state.LicenseKey.Trim()
        };
    }

    private string? EnsureSudoPassword()
    {
        var cached = _sudoCache.Get();
        if (!string.IsNullOrWhiteSpace(cached))
        {
            HasCachedSudoPassword = true;
            return cached;
        }

        var dialog = new SecretInputDialog(
            "Gateway Sudo Password",
            "Enter the VPS sudo password for gateway install/start/stop operations. The value is kept only for this app session.",
            string.Empty)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SecretValue))
        {
            return null;
        }

        _sudoCache.Set(dialog.SecretValue);
        HasCachedSudoPassword = true;
        return dialog.SecretValue;
    }

    private void ReportProgress(DeploymentProgressSnapshot snapshot, bool useOverallAggregator)
    {
        if (useOverallAggregator)
        {
            OverallProgressPercent = _progressAggregator.ToOverallPercent(snapshot);
        }
        else
        {
            OverallProgressPercent = Math.Clamp(snapshot.Percent, 0, 100);
        }

        OverallProgressMessage = snapshot.Message;
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:HH:mm:ss} {text.Trim()}";
        _operationLogBuilder.AppendLine(line);
        OperationLog = _operationLogBuilder.ToString();
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
