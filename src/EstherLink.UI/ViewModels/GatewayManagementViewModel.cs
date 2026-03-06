using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstherLink.Core.Configuration;
using EstherLink.UI.Models;
using EstherLink.UI.Services;
using EstherLink.UI.Views.Dialogs;
using Microsoft.Win32;
using System.ComponentModel;
using System.Text;
using System.Windows;

namespace EstherLink.UI.ViewModels;

public partial class GatewayManagementViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    private readonly IGatewayDeploymentService _gatewayDeployment;
    private readonly IGatewayHealthService _gatewayHealth;
    private readonly ISudoSessionSecretCache _sudoCache;

    private readonly StringBuilder _operationLogBuilder = new();

    public GatewayManagementViewModel(
        GatewayOrchestratorService orchestrator,
        GatewayStateStore state,
        IGatewayDeploymentService gatewayDeployment,
        IGatewayHealthService gatewayHealth,
        ISudoSessionSecretCache sudoCache)
    {
        _orchestrator = orchestrator;
        _state = state;
        _gatewayDeployment = gatewayDeployment;
        _gatewayHealth = gatewayHealth;
        _sudoCache = sudoCache;

        _state.PropertyChanged += OnStateChanged;
    }

    public GatewayStateStore State => _state;

    public bool IsHostKeyAuthSelected =>
        string.Equals(TunnelAuthMethods.Normalize(State.TunnelAuthMethod), TunnelAuthMethods.HostKey, StringComparison.Ordinal);

    public bool IsPasswordAuthSelected =>
        string.Equals(TunnelAuthMethods.Normalize(State.TunnelAuthMethod), TunnelAuthMethods.Password, StringComparison.Ordinal);

    public string KeyPassphraseState =>
        string.IsNullOrWhiteSpace(State.TunnelKeyPassphrase) ? "Not set" : "Configured";

    public string PasswordState =>
        string.IsNullOrWhiteSpace(State.TunnelPassword) ? "Not set" : "Configured";

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string gatewayServiceState = "Unknown";

    [ObservableProperty]
    private string gatewayBootstrapState = "Not checked";

    [ObservableProperty]
    private GatewayHealthReport? gatewayHealthReport;

    [ObservableProperty]
    private string gatewayHealthSummary = "Not checked";

    [ObservableProperty]
    private string gatewayDnsSummary = "Not checked";

    [ObservableProperty]
    private string operationLog = string.Empty;

    [ObservableProperty]
    private bool hasCachedSudoPassword;

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyGatewayConfigCommand.NotifyCanExecuteChanged();
        TestTunnelConnectionCommand.NotifyCanExecuteChanged();
        BrowseTunnelKeyCommand.NotifyCanExecuteChanged();
        SetKeyPassphraseCommand.NotifyCanExecuteChanged();
        ClearKeyPassphraseCommand.NotifyCanExecuteChanged();
        SetTunnelPasswordCommand.NotifyCanExecuteChanged();
        ClearTunnelPasswordCommand.NotifyCanExecuteChanged();

        GatewayBootstrapCheckCommand.NotifyCanExecuteChanged();
        InstallGatewayCommand.NotifyCanExecuteChanged();
        StartGatewayCommand.NotifyCanExecuteChanged();
        StopGatewayCommand.NotifyCanExecuteChanged();
        UninstallGatewayCommand.NotifyCanExecuteChanged();
        HealthCheckGatewayCommand.NotifyCanExecuteChanged();
        ApplyGatewayDnsCommand.NotifyCanExecuteChanged();
        CheckGatewayDnsCommand.NotifyCanExecuteChanged();
        RepairGatewayDnsCommand.NotifyCanExecuteChanged();
        ClearCachedSudoPasswordCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            var refresh = await _orchestrator.RefreshStatusAsync();
            Feedback = refresh.Message;
            HasCachedSudoPassword = !string.IsNullOrWhiteSpace(_sudoCache.Get());

            var sudo = _sudoCache.Get();
            if (!string.IsNullOrWhiteSpace(sudo))
            {
                await RefreshGatewayStatusAsync(sudo!);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyGatewayConfigAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.ApplyGatewayConfigAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TestTunnelConnectionAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.TestTunnelConnectionAsync();
            Feedback = result.Message;
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
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

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void SetKeyPassphrase()
    {
        var dialog = new SecretInputDialog(
            "Key Passphrase",
            "Enter the SSH private key passphrase used for tunnel authentication.",
            State.TunnelKeyPassphrase)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            State.TunnelKeyPassphrase = dialog.SecretValue;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ClearKeyPassphrase()
    {
        State.TunnelKeyPassphrase = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void SetTunnelPassword()
    {
        var dialog = new SecretInputDialog(
            "Tunnel Password",
            "Enter the SSH account password used for tunnel authentication.",
            State.TunnelPassword)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            State.TunnelPassword = dialog.SecretValue;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ClearTunnelPassword()
    {
        State.TunnelPassword = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task GatewayBootstrapCheckAsync()
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = "Gateway bootstrap check canceled. Sudo password is required.";
                AppendLog(Feedback);
                return;
            }

            var request = BuildGatewayRequest();
            var progress = CreateGatewayProgressReporter();
            var op = await _gatewayDeployment.CheckGatewayBootstrapAsync(request, sudo, progress);
            GatewayBootstrapState = op.Success ? "Passed" : "Failed";
            Feedback = op.Message;
            AppendLog(op.Message);
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
                return;
            }

            var request = BuildGatewayRequest();
            var progress = CreateGatewayProgressReporter();
            var op = await _gatewayDeployment.InstallGatewayAsync(request, sudo, progress);
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
            var progress = CreateGatewayProgressReporter();
            var op = await _gatewayDeployment.StartGatewayAsync(request, sudo, progress);
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
            var progress = CreateGatewayProgressReporter();
            var op = await _gatewayDeployment.StopGatewayAsync(request, sudo, progress);
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
            var progress = CreateGatewayProgressReporter();
            var op = await _gatewayDeployment.UninstallGatewayAsync(request, sudo, progress);
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
    private async Task ApplyGatewayDnsAsync()
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = "DNS apply canceled. Sudo password is required.";
                return;
            }

            var request = BuildGatewayRequest();
            var progress = CreateGatewayProgressReporter();
            var op = await _gatewayDeployment.ApplyGatewayDnsAsync(request, sudo, progress);
            Feedback = op.Message;
            AppendLog(op.Message);
            await RefreshGatewayHealthAsync(request, sudo);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckGatewayDnsAsync()
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = "DNS check canceled. Sudo password is required.";
                return;
            }

            var request = BuildGatewayRequest();
            var progress = CreateGatewayProgressReporter();
            var op = await _gatewayDeployment.CheckGatewayDnsAsync(request, sudo, progress);
            Feedback = op.Message;
            AppendLog(op.Message);
            await RefreshGatewayHealthAsync(request, sudo);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RepairGatewayDnsAsync()
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = "DNS repair canceled. Sudo password is required.";
                return;
            }

            var request = BuildGatewayRequest();
            var progress = CreateGatewayProgressReporter();
            var op = await _gatewayDeployment.RepairGatewayDnsAsync(request, sudo, progress);
            Feedback = op.Message;
            AppendLog(op.Message);
            await RefreshGatewayHealthAsync(request, sudo);
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

    private async Task RefreshGatewayStatusAsync(string sudoPassword)
    {
        try
        {
            var request = BuildGatewayRequest();
            var status = await _gatewayDeployment.GetStatusAsync(request, sudoPassword);
            GatewayServiceState = $"x-ui={status.XuiState}, sshd={status.SshState}";
        }
        catch (Exception ex)
        {
            GatewayServiceState = "Unavailable";
            AppendLog($"Gateway status read failed: {BuildGatewayConnectivityHint(ex.Message)}");
        }
    }

    private async Task RefreshGatewayHealthAsync(GatewayDeploymentRequest request, string sudoPassword)
    {
        try
        {
            var report = await _gatewayHealth.GetHealthAsync(request, sudoPassword);
            GatewayHealthReport = report;
            GatewayHealthSummary = report.Healthy
                ? $"Healthy (checked {report.CheckedAtUtc:yyyy-MM-dd HH:mm:ss} UTC)"
                : $"Unhealthy (checked {report.CheckedAtUtc:yyyy-MM-dd HH:mm:ss} UTC)";
            GatewayDnsSummary = $"DNS path={report.DnsPathHealthy}, config={report.DnsConfigPresent}, rules={report.DnsRuleActive}, doh={report.DohReachableViaTunnel}, udp53={report.Udp53PathReady}";
            Feedback = GatewayHealthSummary;
            AppendLog($"Gateway health: {GatewayHealthSummary}");
            AppendLog($"Gateway DNS: {GatewayDnsSummary}");
        }
        catch (Exception ex)
        {
            GatewayHealthSummary = $"Health check failed: {BuildGatewayConnectivityHint(ex.Message)}";
            GatewayDnsSummary = "DNS status unavailable";
            Feedback = GatewayHealthSummary;
            AppendLog(GatewayHealthSummary);
        }
    }

    private GatewayDeploymentRequest BuildGatewayRequest()
    {
        var config = BuildServiceConfig();

        if (!int.TryParse(_state.GatewayPublicPortText.Trim(), out var publicPort) || publicPort <= 0)
        {
            throw new InvalidOperationException("Gateway public port must be a positive integer.");
        }

        if (!int.TryParse(_state.GatewayPanelPortText.Trim(), out var panelPort) || panelPort <= 0)
        {
            throw new InvalidOperationException("Gateway panel port must be a positive integer.");
        }

        var dnsMode = _state.GatewayDnsMode.Trim().ToLowerInvariant();
        if (dnsMode is not ("hybrid" or "doh" or "udp"))
        {
            throw new InvalidOperationException("Gateway DNS mode must be hybrid, doh, or udp.");
        }

        if (string.IsNullOrWhiteSpace(_state.GatewayDohEndpointsText))
        {
            throw new InvalidOperationException("Gateway DoH endpoints are required.");
        }

        return new GatewayDeploymentRequest
        {
            Config = config,
            GatewayPublicPort = publicPort,
            GatewayPanelPort = panelPort,
            GatewayDnsMode = dnsMode,
            GatewayDohEndpoints = _state.GatewayDohEndpointsText.Trim(),
            GatewayDnsUdpOnly = _state.GatewayDnsUdpOnly
        };
    }

    private ServiceConfig BuildServiceConfig()
    {
        if (!int.TryParse(_state.ProxyPortText.Trim(), out var proxyPort) || proxyPort <= 0)
        {
            throw new InvalidOperationException("Proxy listen port must be a positive integer.");
        }

        if (!int.TryParse(_state.BootstrapSocksLocalPortText.Trim(), out var bootstrapSocksLocalPort) || bootstrapSocksLocalPort <= 0)
        {
            throw new InvalidOperationException("Bootstrap SOCKS local port must be a positive integer.");
        }

        if (!int.TryParse(_state.BootstrapSocksRemotePortText.Trim(), out var bootstrapSocksRemotePort) || bootstrapSocksRemotePort <= 0)
        {
            throw new InvalidOperationException("Bootstrap SOCKS remote port must be a positive integer.");
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
            BootstrapSocksLocalPort = bootstrapSocksLocalPort,
            BootstrapSocksRemotePort = bootstrapSocksRemotePort,
            GatewayOnlineInstallEnabled = true,
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

    private IProgress<DeploymentProgressSnapshot> CreateGatewayProgressReporter()
    {
        return new Progress<DeploymentProgressSnapshot>(snapshot =>
        {
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Message))
            {
                return;
            }

            AppendLog(snapshot.Message);
        });
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

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GatewayStateStore.TunnelAuthMethod))
        {
            OnPropertyChanged(nameof(IsHostKeyAuthSelected));
            OnPropertyChanged(nameof(IsPasswordAuthSelected));
        }

        if (e.PropertyName is nameof(GatewayStateStore.TunnelKeyPassphrase) or nameof(GatewayStateStore.TunnelPassword))
        {
            OnPropertyChanged(nameof(KeyPassphraseState));
            OnPropertyChanged(nameof(PasswordState));
        }
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
            AppendLog(Feedback);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string BuildGatewayConnectivityHint(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "unknown gateway connectivity error";
        }

        var msg = error.Trim();
        if (msg.Contains("Connection failed to establish within", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return $"{msg}. Check VPS SSH reachability on {_state.TunnelHost}:{_state.TunnelSshPortText} (firewall + sshd Port).";
        }

        return msg;
    }
}
