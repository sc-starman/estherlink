using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.Core.Configuration;
using OmniRelay.UI.Models;
using OmniRelay.UI.Services;
using OmniRelay.UI.Views.Dialogs;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;

namespace OmniRelay.UI.ViewModels;

public partial class GatewayManagementViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    private readonly IGatewayDeploymentService _gatewayDeployment;
    private readonly IGatewayHealthService _gatewayHealth;
    private readonly IDeploymentProgressAggregator _progressAggregator;
    private readonly ISudoSessionSecretCache _sudoCache;

    private readonly StringBuilder _operationLogBuilder = new();
    private GatewayOperationDialog? _gatewayOperationDialog;
    private CancellationTokenSource? _gatewayOperationCts;

    public GatewayManagementViewModel(
        GatewayOrchestratorService orchestrator,
        GatewayStateStore state,
        IGatewayDeploymentService gatewayDeployment,
        IGatewayHealthService gatewayHealth,
        IDeploymentProgressAggregator progressAggregator,
        ISudoSessionSecretCache sudoCache)
    {
        _orchestrator = orchestrator;
        _state = state;
        _gatewayDeployment = gatewayDeployment;
        _gatewayHealth = gatewayHealth;
        _progressAggregator = progressAggregator;
        _sudoCache = sudoCache;

        _state.PropertyChanged += OnStateChanged;
    }

    public GatewayStateStore State => _state;

    public bool IsHostKeyAuthSelected =>
        string.Equals(TunnelAuthMethods.Normalize(State.TunnelAuthMethod), TunnelAuthMethods.HostKey, StringComparison.Ordinal);

    public bool IsPasswordAuthSelected =>
        string.Equals(TunnelAuthMethods.Normalize(State.TunnelAuthMethod), TunnelAuthMethods.Password, StringComparison.Ordinal);

    public int TunnelAuthMethodIndex
    {
        get => IsPasswordAuthSelected ? 1 : 0;
        set
        {
            var mapped = value == 1 ? TunnelAuthMethods.Password : TunnelAuthMethods.HostKey;
            if (string.Equals(TunnelAuthMethods.Normalize(State.TunnelAuthMethod), mapped, StringComparison.Ordinal))
            {
                return;
            }

            State.TunnelAuthMethod = mapped;
        }
    }

    public string KeyPassphraseState =>
        string.IsNullOrWhiteSpace(State.TunnelKeyPassphrase) ? "Not set" : "Configured";

    public string PasswordState =>
        string.IsNullOrWhiteSpace(State.TunnelPassword) ? "Not set" : "Configured";

    public string HostKeyFileState =>
        string.IsNullOrWhiteSpace(State.TunnelKeyPath) ? "Not set" : "Configured";

    public string AuthenticationDetailLabel =>
        IsHostKeyAuthSelected ? "Host Key File" : "Password";

    public IReadOnlyList<(string Value, string Label)> AvailableGatewayProtocols => GatewayProtocols.All;

    public int SelectedGatewayProtocolIndex
    {
        get
        {
            var selected = GatewayProtocols.Normalize(State.SelectedGatewayProtocol);
            for (var i = 0; i < AvailableGatewayProtocols.Count; i++)
            {
                if (string.Equals(AvailableGatewayProtocols[i].Value, selected, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 0;
        }
        set
        {
            var normalizedIndex = Math.Clamp(value, 0, AvailableGatewayProtocols.Count - 1);
            var protocol = AvailableGatewayProtocols[normalizedIndex].Value;
            if (string.Equals(GatewayProtocols.Normalize(State.SelectedGatewayProtocol), protocol, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            State.SelectedGatewayProtocol = protocol;
            Feedback = $"Protocol switched to {GatewayProtocols.ToLabel(protocol)}.";
        }
    }

    public bool IsVlessProtocolSelected =>
        string.Equals(GatewayProtocols.Normalize(State.SelectedGatewayProtocol), GatewayProtocols.VlessReality3xui, StringComparison.OrdinalIgnoreCase);

    public bool IsVlessPlain3xuiProtocolSelected =>
        string.Equals(GatewayProtocols.Normalize(State.SelectedGatewayProtocol), GatewayProtocols.VlessPlain3xui, StringComparison.OrdinalIgnoreCase);

    public bool IsShadowTlsProtocolSelected =>
        string.Equals(GatewayProtocols.Normalize(State.SelectedGatewayProtocol), GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase);

    public bool IsShadowsocks3xuiProtocolSelected =>
        string.Equals(GatewayProtocols.Normalize(State.SelectedGatewayProtocol), GatewayProtocols.Shadowsocks3xui, StringComparison.OrdinalIgnoreCase);

    public bool IsOpenVpnProtocolSelected =>
        string.Equals(GatewayProtocols.Normalize(State.SelectedGatewayProtocol), GatewayProtocols.OpenVpnTcpRelay, StringComparison.OrdinalIgnoreCase);

    public bool IsIpsecL2tpProtocolSelected =>
        string.Equals(GatewayProtocols.Normalize(State.SelectedGatewayProtocol), GatewayProtocols.IpsecL2tpHwdsl2, StringComparison.OrdinalIgnoreCase);

    public bool ShowEditableProtocolPort => !IsIpsecL2tpProtocolSelected;

    public string FixedProtocolPortsText => "UDP 500, UDP 4500, UDP 1701 (fixed)";

    public string ProtocolPortLabel =>
        IsVlessProtocolSelected
            ? "VLESS Reality Port"
            : IsVlessPlain3xuiProtocolSelected
                ? "VLESS (no TLS) Port"
            : IsShadowsocks3xuiProtocolSelected
                ? "Shadowsocks Port"
            : IsShadowTlsProtocolSelected
                ? "ShadowTLS Port"
                : IsIpsecL2tpProtocolSelected
                    ? "IPSec/L2TP Ports"
                : "OpenVPN Port";

    public string ProtocolPresetSwitchText =>
        IsVlessProtocolSelected ? "Switch REALITY Pair" : "Switch Camouflage";

    public bool IsGatewayPanelSslEnabled => State.GatewayPanelUseSsl;

    public bool IsGatewayPanelSslUploadMode =>
        IsGatewayPanelSslEnabled &&
        string.Equals(NormalizePanelSslMode(State.GatewayPanelSslMode), "uploaded", StringComparison.OrdinalIgnoreCase);

    public bool IsGatewayPanelSslLetsEncryptMode =>
        IsGatewayPanelSslEnabled &&
        string.Equals(NormalizePanelSslMode(State.GatewayPanelSslMode), "letsencrypt", StringComparison.OrdinalIgnoreCase);

    public int GatewayPanelSslModeIndex
    {
        get => string.Equals(NormalizePanelSslMode(State.GatewayPanelSslMode), "uploaded", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        set
        {
            State.GatewayPanelSslMode = value == 1 ? "uploaded" : "letsencrypt";
            OnPropertyChanged(nameof(IsGatewayPanelSslUploadMode));
            OnPropertyChanged(nameof(IsGatewayPanelSslLetsEncryptMode));
        }
    }

    public string GatewayPanelConfiguredPasswordState =>
        string.IsNullOrWhiteSpace(State.GatewayPanelConfiguredPassword) ? "Not set" : "Configured";

    public string GatewayPanelUploadedCertState =>
        string.IsNullOrWhiteSpace(State.GatewayPanelUploadedCertPath) ? "Not set" : "Configured";

    public string GatewayPanelUploadedKeyState =>
        string.IsNullOrWhiteSpace(State.GatewayPanelUploadedKeyPath) ? "Not set" : "Configured";

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
    private string gatewayOperationTitle = string.Empty;

    [ObservableProperty]
    private bool isGatewayOperationRunning;

    [ObservableProperty]
    private bool isGatewayOperationInstallResultVisible;

    [ObservableProperty]
    private string gatewayOperationPanelUrl = string.Empty;

    [ObservableProperty]
    private string gatewayOperationPanelUsername = string.Empty;

    [ObservableProperty]
    private string gatewayOperationPanelPassword = string.Empty;

    [ObservableProperty]
    private bool isGatewayOperationPanelPasswordVisible;

    [ObservableProperty]
    private bool hasCachedSudoPassword;

    [ObservableProperty]
    private bool isFeedbackVisible;

    private CancellationTokenSource? _feedbackDismissCts;

    [ObservableProperty]
    private bool isGatewayProgressVisible;

    [ObservableProperty]
    private bool isGatewayProgressIndeterminate = true;

    [ObservableProperty]
    private double gatewayProgressPercent;

    [ObservableProperty]
    private string gatewayProgressMessage = string.Empty;

    public string GatewayProgressPercentText =>
        IsGatewayProgressIndeterminate ? string.Empty : $"{Math.Round(GatewayProgressPercent):0}%";

    public string GatewayOperationPanelPasswordDisplay =>
        IsGatewayOperationPanelPasswordVisible
            ? (GatewayOperationPanelPassword ?? string.Empty)
            : MaskSecret(GatewayOperationPanelPassword);

    public string GatewayOperationPanelPasswordToggleText => IsGatewayOperationPanelPasswordVisible ? "Hide" : "Show";
    public string GatewayOperationPanelPasswordToggleIconGlyph => IsGatewayOperationPanelPasswordVisible ? "\uE8F4" : "\uE890";

    public bool CanCancelGatewayOperation => IsGatewayOperationRunning;
    public bool CanCloseGatewayOperationDialog => !IsGatewayOperationRunning && !IsGatewayOperationInstallResultVisible;
    public bool CanAcknowledgeGatewayInstallSecrets => !IsGatewayOperationRunning && IsGatewayOperationInstallResultVisible;

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        SwitchRealityPairCommand.NotifyCanExecuteChanged();
        ApplyGatewayConfigCommand.NotifyCanExecuteChanged();
        TestTunnelConnectionCommand.NotifyCanExecuteChanged();
        BrowseTunnelKeyCommand.NotifyCanExecuteChanged();
        SetKeyPassphraseCommand.NotifyCanExecuteChanged();
        ClearKeyPassphraseCommand.NotifyCanExecuteChanged();
        ClearTunnelKeyPathCommand.NotifyCanExecuteChanged();
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
        SetGatewayPanelPasswordCommand.NotifyCanExecuteChanged();
        ClearGatewayPanelPasswordCommand.NotifyCanExecuteChanged();
        BrowseGatewayPanelCertCommand.NotifyCanExecuteChanged();
        ClearGatewayPanelCertPathCommand.NotifyCanExecuteChanged();
        BrowseGatewayPanelKeyCommand.NotifyCanExecuteChanged();
        ClearGatewayPanelKeyPathCommand.NotifyCanExecuteChanged();
        CancelGatewayOperationCommand.NotifyCanExecuteChanged();
        CloseGatewayOperationDialogCommand.NotifyCanExecuteChanged();
        AcknowledgeGatewayInstallSecretsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsGatewayOperationPanelPasswordVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(GatewayOperationPanelPasswordDisplay));
        OnPropertyChanged(nameof(GatewayOperationPanelPasswordToggleText));
        OnPropertyChanged(nameof(GatewayOperationPanelPasswordToggleIconGlyph));
    }

    partial void OnGatewayProgressPercentChanged(double value)
    {
        OnPropertyChanged(nameof(GatewayProgressPercentText));
    }

    partial void OnIsGatewayOperationRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelGatewayOperation));
        OnPropertyChanged(nameof(CanCloseGatewayOperationDialog));
        OnPropertyChanged(nameof(CanAcknowledgeGatewayInstallSecrets));
        CancelGatewayOperationCommand.NotifyCanExecuteChanged();
        CloseGatewayOperationDialogCommand.NotifyCanExecuteChanged();
        AcknowledgeGatewayInstallSecretsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsGatewayOperationInstallResultVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCloseGatewayOperationDialog));
        OnPropertyChanged(nameof(CanAcknowledgeGatewayInstallSecrets));
        CloseGatewayOperationDialogCommand.NotifyCanExecuteChanged();
        AcknowledgeGatewayInstallSecretsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsGatewayProgressIndeterminateChanged(bool value)
    {
        OnPropertyChanged(nameof(GatewayProgressPercentText));
    }

    partial void OnFeedbackChanged(string value)
    {
        IsFeedbackVisible = !string.IsNullOrWhiteSpace(value);

        _feedbackDismissCts?.Cancel();
        _feedbackDismissCts?.Dispose();
        _feedbackDismissCts = null;

        if (!IsFeedbackVisible)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _feedbackDismissCts = cts;
        _ = AutoDismissFeedbackAsync(value, cts.Token);
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
    private void SwitchRealityPair()
    {
        if (IsVlessProtocolSelected)
        {
            var presets = GatewayRealityTargetCatalog.All;
            var currentTarget = (_state.GatewayTarget ?? string.Empty).Trim();
            var currentSni = (_state.GatewaySni ?? string.Empty).Trim();

            var currentIndex = -1;
            for (var i = 0; i < presets.Count; i++)
            {
                if (string.Equals(presets[i].Target, currentTarget, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(presets[i].Sni, currentSni, StringComparison.OrdinalIgnoreCase))
                {
                    currentIndex = i;
                    break;
                }
            }

            var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % presets.Count;
            var next = presets[nextIndex];

            _state.GatewayTarget = next.Target;
            _state.GatewaySni = next.Sni;
            Feedback = $"Switched REALITY pair to {next.Sni} ({next.Target}).";
            return;
        }

        if (IsOpenVpnProtocolSelected)
        {
            Feedback = "Preset switch is not used for OpenVPN protocol.";
            return;
        }

        if (IsIpsecL2tpProtocolSelected)
        {
            Feedback = "Preset switch is not used for IPSec/L2TP protocol.";
            return;
        }

        var catalog = GatewayCamouflageCatalog.All;
        var current = (_state.ShadowTlsCamouflageServer ?? string.Empty).Trim();
        var currentCatalogIndex = -1;
        for (var i = 0; i < catalog.Count; i++)
        {
            if (string.Equals(catalog[i], current, StringComparison.OrdinalIgnoreCase))
            {
                currentCatalogIndex = i;
                break;
            }
        }

        var nextCamouflageIndex = currentCatalogIndex < 0 ? 0 : (currentCatalogIndex + 1) % catalog.Count;
        _state.ShadowTlsCamouflageServer = catalog[nextCamouflageIndex];
        Feedback = $"Switched camouflage server to {_state.ShadowTlsCamouflageServer}.";
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
    private void ClearTunnelKeyPath()
    {
        State.TunnelKeyPath = string.Empty;
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
    private void SetGatewayPanelPassword()
    {
        var dialog = new SecretInputDialog(
            "OmniPanel Password",
            "Enter optional OmniPanel password. Leave empty to auto-generate at install.",
            State.GatewayPanelConfiguredPassword)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            State.GatewayPanelConfiguredPassword = dialog.SecretValue;
            OnPropertyChanged(nameof(GatewayPanelConfiguredPasswordState));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ClearGatewayPanelPassword()
    {
        State.GatewayPanelConfiguredPassword = string.Empty;
        OnPropertyChanged(nameof(GatewayPanelConfiguredPasswordState));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void BrowseGatewayPanelCert()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select TLS certificate file",
            Filter = "Certificate files|*.crt;*.pem;*.cer|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            State.GatewayPanelUploadedCertPath = dialog.FileName;
            OnPropertyChanged(nameof(GatewayPanelUploadedCertState));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ClearGatewayPanelCertPath()
    {
        State.GatewayPanelUploadedCertPath = string.Empty;
        OnPropertyChanged(nameof(GatewayPanelUploadedCertState));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void BrowseGatewayPanelKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select TLS private key file",
            Filter = "Private key files|*.key;*.pem|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            State.GatewayPanelUploadedKeyPath = dialog.FileName;
            OnPropertyChanged(nameof(GatewayPanelUploadedKeyState));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ClearGatewayPanelKeyPath()
    {
        State.GatewayPanelUploadedKeyPath = string.Empty;
        OnPropertyChanged(nameof(GatewayPanelUploadedKeyState));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task GatewayBootstrapCheckAsync()
    {
        await RunGatewayOperationInDialogAsync(
            "Gateway Bootstrap Check",
            (request, sudo, progress, token) => _gatewayDeployment.CheckGatewayBootstrapAsync(request, sudo, progress, token),
            async (request, sudo, progress, op, token) =>
            {
                GatewayBootstrapState = op.Success ? "Passed" : "Failed";
                await Task.CompletedTask;
            });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallGatewayAsync()
    {
        await RunGatewayOperationInDialogAsync(
            "Gateway Install",
            (request, sudo, progress, token) => _gatewayDeployment.InstallGatewayAsync(request, sudo, progress, token),
            async (request, sudo, progress, op, token) =>
            {
                await RefreshGatewayStatusAsync(sudo);
                await RefreshGatewayHealthAsync(request, sudo, progress);
                PrepareInstallSecretsForOneTimeDisplay(op);
            });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartGatewayAsync()
    {
        await RunGatewayOperationInDialogAsync(
            "Gateway Start",
            (request, sudo, progress, token) => _gatewayDeployment.StartGatewayAsync(request, sudo, progress, token),
            async (request, sudo, progress, op, token) => await RefreshGatewayStatusAsync(sudo));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StopGatewayAsync()
    {
        await RunGatewayOperationInDialogAsync(
            "Gateway Stop",
            (request, sudo, progress, token) => _gatewayDeployment.StopGatewayAsync(request, sudo, progress, token),
            async (request, sudo, progress, op, token) => await RefreshGatewayStatusAsync(sudo));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallGatewayAsync()
    {
        await RunGatewayOperationInDialogAsync(
            "Gateway Uninstall",
            (request, sudo, progress, token) => _gatewayDeployment.UninstallGatewayAsync(request, sudo, progress, token),
            async (request, sudo, progress, op, token) =>
            {
                await RefreshGatewayStatusAsync(sudo);
                GatewayHealthReport = null;
                GatewayHealthSummary = "Not checked";
            });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task HealthCheckGatewayAsync()
    {
        await RunGatewayOperationInDialogAsync(
            "Gateway Health Check",
            async (request, sudo, progress, token) =>
            {
                var ok = await RefreshGatewayHealthAsync(request, sudo, progress);
                return new GatewayOperationResult(ok, GatewayHealthSummary);
            });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyGatewayDnsAsync()
    {
        await RunGatewayOperationInDialogAsync(
            "Apply DNS Profile",
            (request, sudo, progress, token) => _gatewayDeployment.ApplyGatewayDnsAsync(request, sudo, progress, token),
            async (request, sudo, progress, op, token) => await RefreshGatewayHealthAsync(request, sudo, progress));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckGatewayDnsAsync()
    {
        await RunGatewayOperationInDialogAsync(
            "Check DNS Path",
            (request, sudo, progress, token) => _gatewayDeployment.CheckGatewayDnsAsync(request, sudo, progress, token),
            async (request, sudo, progress, op, token) => await RefreshGatewayHealthAsync(request, sudo, progress));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RepairGatewayDnsAsync()
    {
        await RunGatewayOperationInDialogAsync(
            "Repair DNS Path",
            (request, sudo, progress, token) => _gatewayDeployment.RepairGatewayDnsAsync(request, sudo, progress, token),
            async (request, sudo, progress, op, token) => await RefreshGatewayHealthAsync(request, sudo, progress));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ClearCachedSudoPassword()
    {
        _sudoCache.Clear();
        HasCachedSudoPassword = false;
        Feedback = "Cached sudo password cleared for this session.";
        AppendLog(Feedback);
    }

    [RelayCommand(CanExecute = nameof(CanCancelGatewayOperation))]
    private void CancelGatewayOperation()
    {
        if (!IsGatewayOperationRunning)
        {
            return;
        }

        _gatewayOperationCts?.Cancel();
        AppendLog("Cancellation requested by user.");
    }

    [RelayCommand(CanExecute = nameof(CanCloseGatewayOperationDialog))]
    private void CloseGatewayOperationDialog()
    {
        IsGatewayProgressVisible = false;
        _operationLogBuilder.Clear();
        OperationLog = string.Empty;
        ClearGatewayOperationInstallSecrets();
        _gatewayOperationDialog?.Close();
    }

    [RelayCommand(CanExecute = nameof(CanAcknowledgeGatewayInstallSecrets))]
    private void AcknowledgeGatewayInstallSecrets()
    {
        ClearGatewayOperationInstallSecrets();
        IsGatewayProgressVisible = false;
        _operationLogBuilder.Clear();
        OperationLog = string.Empty;
        _gatewayOperationDialog?.Close();
    }

    [RelayCommand]
    private void ToggleGatewayOperationPanelPasswordVisibility()
    {
        IsGatewayOperationPanelPasswordVisible = !IsGatewayOperationPanelPasswordVisible;
    }

    [RelayCommand]
    private void CopyGatewayOperationPanelUsername()
    {
        var username = GatewayOperationPanelUsername?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username))
        {
            Feedback = "Panel username is empty.";
            return;
        }

        Clipboard.SetText(username);
        Feedback = "Panel username copied.";
    }

    [RelayCommand]
    private void CopyGatewayOperationPanelPassword()
    {
        var password = GatewayOperationPanelPassword ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            Feedback = "Panel password is empty.";
            return;
        }

        Clipboard.SetText(password);
        Feedback = "Panel password copied.";
    }

    [RelayCommand]
    private void OpenGatewayOperationPanelUrl()
    {
        var panelUrl = GatewayOperationPanelUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(panelUrl, UriKind.Absolute, out _))
        {
            Feedback = "Panel URL is not valid.";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = panelUrl,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void DismissFeedback()
    {
        Feedback = string.Empty;
    }

    private async Task RunGatewayOperationInDialogAsync(
        string title,
        Func<GatewayDeploymentRequest, string, IProgress<DeploymentProgressSnapshot>, CancellationToken, Task<GatewayOperationResult>> executeAsync,
        Func<GatewayDeploymentRequest, string, IProgress<DeploymentProgressSnapshot>, GatewayOperationResult, CancellationToken, Task>? afterOperationAsync = null)
    {
        await RunBusyAsync(async () =>
        {
            var sudo = EnsureSudoPassword();
            if (sudo is null)
            {
                Feedback = $"{title} canceled. Sudo password is required.";
                AppendLog(Feedback);
                return;
            }

            GatewayDeploymentRequest request;
            try
            {
                request = BuildGatewayRequest();
            }
            catch (Exception ex)
            {
                Feedback = ex.Message;
                AppendLog(Feedback);
                return;
            }

            ResetGatewayOperationDialogState(title);
            _gatewayOperationDialog = new GatewayOperationDialog
            {
                Owner = Application.Current?.MainWindow,
                DataContext = this
            };

            _gatewayOperationDialog.ContentRendered += (_, _) =>
            {
                _ = ExecuteGatewayOperationAsync(title, request, sudo, executeAsync, afterOperationAsync);
            };

            _gatewayOperationDialog.ShowDialog();
            _gatewayOperationDialog = null;
            await Task.CompletedTask;
        });
    }

    private async Task ExecuteGatewayOperationAsync(
        string title,
        GatewayDeploymentRequest request,
        string sudoPassword,
        Func<GatewayDeploymentRequest, string, IProgress<DeploymentProgressSnapshot>, CancellationToken, Task<GatewayOperationResult>> executeAsync,
        Func<GatewayDeploymentRequest, string, IProgress<DeploymentProgressSnapshot>, GatewayOperationResult, CancellationToken, Task>? afterOperationAsync)
    {
        if (IsGatewayOperationRunning)
        {
            return;
        }

        var progress = CreateGatewayProgressReporter();
        BeginGatewayProgress(title);
        IsGatewayOperationRunning = true;
        _gatewayOperationCts?.Cancel();
        _gatewayOperationCts?.Dispose();
        _gatewayOperationCts = new CancellationTokenSource();
        var token = _gatewayOperationCts.Token;

        var success = false;
        var finalMessage = $"{title} did not complete.";

        try
        {
            var operation = await executeAsync(request, sudoPassword, progress, token);
            var safeMessage = BuildSafeGatewayOperationMessage(operation);
            Feedback = safeMessage;
            AppendLog(safeMessage);
            success = operation.Success;
            finalMessage = safeMessage;

            if (afterOperationAsync is not null)
            {
                await afterOperationAsync(request, sudoPassword, progress, operation, token);
            }
        }
        catch (OperationCanceledException)
        {
            finalMessage = $"{title} canceled.";
            Feedback = finalMessage;
            AppendLog(finalMessage);
            success = false;
        }
        catch (Exception ex)
        {
            finalMessage = $"{title} failed: {ex.Message}";
            Feedback = finalMessage;
            AppendLog(finalMessage);
            success = false;
        }
        finally
        {
            IsGatewayOperationRunning = false;
            _gatewayOperationCts?.Dispose();
            _gatewayOperationCts = null;
            EndGatewayProgress(success, finalMessage);
        }
    }

    private static string BuildSafeGatewayOperationMessage(GatewayOperationResult operation)
    {
        if (!operation.Success)
        {
            return operation.Message;
        }

        var hasPanelSecrets =
            !string.IsNullOrWhiteSpace(operation.PanelUrl) &&
            !string.IsNullOrWhiteSpace(operation.PanelUsername) &&
            !string.IsNullOrWhiteSpace(operation.InitialPanelPassword);

        if (hasPanelSecrets)
        {
            return "Gateway install completed. Panel credentials are shown one-time in this operation dialog.";
        }

        return operation.Message;
    }

    private void ResetGatewayOperationDialogState(string title)
    {
        GatewayOperationTitle = title;
        GatewayProgressMessage = string.Empty;
        GatewayProgressPercent = 0;
        IsGatewayProgressIndeterminate = true;
        IsGatewayProgressVisible = false;
        GatewayOperationPanelUrl = string.Empty;
        GatewayOperationPanelUsername = string.Empty;
        GatewayOperationPanelPassword = string.Empty;
        IsGatewayOperationPanelPasswordVisible = false;
        IsGatewayOperationInstallResultVisible = false;
        _operationLogBuilder.Clear();
        OperationLog = string.Empty;
    }

    private void PrepareInstallSecretsForOneTimeDisplay(GatewayOperationResult result)
    {
        if (!result.Success)
        {
            return;
        }

        GatewayOperationPanelUrl = result.PanelUrl?.Trim() ?? string.Empty;
        GatewayOperationPanelUsername = result.PanelUsername?.Trim() ?? string.Empty;
        GatewayOperationPanelPassword = result.InitialPanelPassword ?? string.Empty;
        IsGatewayOperationPanelPasswordVisible = true;

        var hasSecrets =
            !string.IsNullOrWhiteSpace(GatewayOperationPanelUrl) &&
            !string.IsNullOrWhiteSpace(GatewayOperationPanelUsername) &&
            !string.IsNullOrWhiteSpace(GatewayOperationPanelPassword);

        IsGatewayOperationInstallResultVisible = hasSecrets;
        if (hasSecrets)
        {
            AppendLog("WARNING: Panel credentials are shown one-time only. Save them before closing this dialog.");
        }

        // Enforce one-time visibility: never persist/show from page state.
        State.GatewayPanelUrl = string.Empty;
        State.GatewayPanelUsername = string.Empty;
        State.GatewayInitialPanelPassword = string.Empty;
    }

    private void ClearGatewayOperationInstallSecrets()
    {
        GatewayOperationPanelUrl = string.Empty;
        GatewayOperationPanelUsername = string.Empty;
        GatewayOperationPanelPassword = string.Empty;
        IsGatewayOperationPanelPasswordVisible = false;
        IsGatewayOperationInstallResultVisible = false;
    }

    private async Task RefreshGatewayStatusAsync(string sudoPassword)
    {
        try
        {
            var request = BuildGatewayRequest();
            var status = await _gatewayDeployment.GetStatusAsync(request, sudoPassword);
            var activeProtocolLabel = GatewayProtocols.ToLabel(status.ActiveProtocol);
            if (string.Equals(GatewayProtocols.Normalize(status.ActiveProtocol), GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
            {
                GatewayServiceState = $"protocol={activeProtocolLabel}, sing-box={status.SingBoxState}, omni-panel={status.OmniPanelState}, nginx={status.NginxState}, sshd={status.SshState}";
                return;
            }

            if (string.Equals(GatewayProtocols.Normalize(status.ActiveProtocol), GatewayProtocols.OpenVpnTcpRelay, StringComparison.OrdinalIgnoreCase))
            {
                GatewayServiceState = $"protocol={activeProtocolLabel}, openvpn={status.OpenVpnState}, omni-panel={status.OmniPanelState}, nginx={status.NginxState}, sshd={status.SshState}";
                return;
            }

            if (string.Equals(GatewayProtocols.Normalize(status.ActiveProtocol), GatewayProtocols.IpsecL2tpHwdsl2, StringComparison.OrdinalIgnoreCase))
            {
                GatewayServiceState = $"protocol={activeProtocolLabel}, ipsec={status.IpsecState}, xl2tpd={status.Xl2tpdState}, omni-panel={status.OmniPanelState}, nginx={status.NginxState}, sshd={status.SshState}";
                return;
            }

            GatewayServiceState = $"protocol={activeProtocolLabel}, x-ui={status.XuiState}, omni-panel={status.OmniPanelState}, nginx={status.NginxState}, sshd={status.SshState}";
        }
        catch (Exception ex)
        {
            GatewayServiceState = "Unavailable";
            AppendLog($"Gateway status read failed: {BuildGatewayConnectivityHint(ex.Message)}");
        }
    }

    private async Task<bool> RefreshGatewayHealthAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null)
    {
        try
        {
            var report = await _gatewayHealth.GetHealthAsync(request, sudoPassword, progress);
            GatewayHealthReport = report;
            GatewayHealthSummary = report.Healthy
                ? $"Healthy (checked {report.CheckedAtUtc:yyyy-MM-dd HH:mm:ss} UTC)"
                : $"Unhealthy (checked {report.CheckedAtUtc:yyyy-MM-dd HH:mm:ss} UTC)";
            GatewayDnsSummary = $"DNS path={report.DnsPathHealthy}, config={report.DnsConfigPresent}, rules={report.DnsRuleActive}, doh={report.DohReachableViaTunnel}, udp53={report.Udp53PathReady}";
            Feedback = GatewayHealthSummary;
            AppendLog($"Gateway health: {GatewayHealthSummary}");
            AppendLog($"Gateway DNS: {GatewayDnsSummary}");
            return true;
        }
        catch (Exception ex)
        {
            GatewayHealthSummary = $"Health check failed: {BuildGatewayConnectivityHint(ex.Message)}";
            GatewayDnsSummary = "DNS status unavailable";
            Feedback = GatewayHealthSummary;
            AppendLog(GatewayHealthSummary);
            return false;
        }
    }

    private GatewayDeploymentRequest BuildGatewayRequest()
    {
        var config = BuildServiceConfig();

        if (!int.TryParse(_state.GatewayPanelPortText.Trim(), out var panelPort) || panelPort <= 0)
        {
            throw new InvalidOperationException("Gateway panel port must be a positive integer.");
        }

        var selectedProtocol = GatewayProtocols.Normalize(_state.SelectedGatewayProtocol);
        var publicPort = 0;
        if (string.Equals(selectedProtocol, GatewayProtocols.IpsecL2tpHwdsl2, StringComparison.OrdinalIgnoreCase))
        {
            publicPort = 1701;
        }
        else if (!int.TryParse(_state.GatewayPublicPortText.Trim(), out publicPort) || publicPort <= 0)
        {
            throw new InvalidOperationException("Gateway public port must be a positive integer.");
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.VlessReality3xui, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_state.GatewaySni))
            {
                throw new InvalidOperationException("Gateway SNI is required for VLESS Reality.");
            }

            if (string.IsNullOrWhiteSpace(_state.GatewayTarget))
            {
                throw new InvalidOperationException("Gateway target is required for VLESS Reality.");
            }
        }
        else if (string.Equals(selectedProtocol, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_state.ShadowTlsCamouflageServer))
            {
                throw new InvalidOperationException("Camouflage server is required for ShadowTLS.");
            }
        }
        else if (string.Equals(selectedProtocol, GatewayProtocols.OpenVpnTcpRelay, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_state.OpenVpnNetwork))
            {
                throw new InvalidOperationException("OpenVPN tunnel network is required.");
            }
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

        var panelDomain = (_state.GatewayPanelDomain ?? string.Empty).Trim();
        var panelDomainOnly = _state.GatewayPanelDomainOnly;
        var panelSslEnabled = _state.GatewayPanelUseSsl;
        var panelSslMode = panelSslEnabled ? NormalizePanelSslMode(_state.GatewayPanelSslMode) : "none";
        var panelCertPath = (_state.GatewayPanelUploadedCertPath ?? string.Empty).Trim();
        var panelKeyPath = (_state.GatewayPanelUploadedKeyPath ?? string.Empty).Trim();

        if ((panelDomainOnly || panelSslEnabled) && string.IsNullOrWhiteSpace(panelDomain))
        {
            throw new InvalidOperationException("OmniPanel domain is required when Domain Only or SSL is enabled.");
        }

        if (panelSslEnabled && panelSslMode == "uploaded")
        {
            if (string.IsNullOrWhiteSpace(panelCertPath) || string.IsNullOrWhiteSpace(panelKeyPath))
            {
                throw new InvalidOperationException("Uploaded SSL mode requires both certificate and private key files.");
            }

            if (!File.Exists(panelCertPath))
            {
                throw new InvalidOperationException("Selected OmniPanel certificate file was not found.");
            }

            if (!File.Exists(panelKeyPath))
            {
                throw new InvalidOperationException("Selected OmniPanel private key file was not found.");
            }
        }

        return new GatewayDeploymentRequest
        {
            Config = config,
            SelectedGatewayProtocol = selectedProtocol,
            GatewayPublicPort = publicPort,
            GatewayPanelPort = panelPort,
            GatewayPanelUser = (_state.GatewayPanelConfiguredUser ?? string.Empty).Trim(),
            GatewayPanelPassword = _state.GatewayPanelConfiguredPassword ?? string.Empty,
            GatewayPanelDomain = panelDomain,
            GatewayPanelDomainOnly = panelDomainOnly,
            GatewayPanelSslEnabled = panelSslEnabled,
            GatewayPanelSslMode = panelSslMode,
            GatewayPanelCertLocalPath = panelCertPath,
            GatewayPanelKeyLocalPath = panelKeyPath,
            GatewaySni = _state.GatewaySni.Trim(),
            GatewayTarget = _state.GatewayTarget.Trim(),
            ShadowTlsCamouflageServer = _state.ShadowTlsCamouflageServer.Trim(),
            OpenVpnNetwork = _state.OpenVpnNetwork.Trim(),
            OpenVpnClientDns = _state.OpenVpnClientDns.Trim(),
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
            if (snapshot is null)
            {
                return;
            }

            UpdateGatewayProgress(snapshot);
            if (!string.IsNullOrWhiteSpace(snapshot.Message))
            {
                AppendLog(snapshot.Message);
            }
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
            OnPropertyChanged(nameof(TunnelAuthMethodIndex));
            OnPropertyChanged(nameof(AuthenticationDetailLabel));
        }

        if (e.PropertyName == nameof(GatewayStateStore.SelectedGatewayProtocol))
        {
            OnPropertyChanged(nameof(SelectedGatewayProtocolIndex));
            OnPropertyChanged(nameof(IsVlessProtocolSelected));
            OnPropertyChanged(nameof(IsVlessPlain3xuiProtocolSelected));
            OnPropertyChanged(nameof(IsShadowsocks3xuiProtocolSelected));
            OnPropertyChanged(nameof(IsShadowTlsProtocolSelected));
            OnPropertyChanged(nameof(IsOpenVpnProtocolSelected));
            OnPropertyChanged(nameof(IsIpsecL2tpProtocolSelected));
            OnPropertyChanged(nameof(ShowEditableProtocolPort));
            OnPropertyChanged(nameof(FixedProtocolPortsText));
            OnPropertyChanged(nameof(ProtocolPortLabel));
            OnPropertyChanged(nameof(ProtocolPresetSwitchText));
        }

        if (e.PropertyName is nameof(GatewayStateStore.GatewayPanelUseSsl) or nameof(GatewayStateStore.GatewayPanelSslMode))
        {
            OnPropertyChanged(nameof(IsGatewayPanelSslEnabled));
            OnPropertyChanged(nameof(IsGatewayPanelSslUploadMode));
            OnPropertyChanged(nameof(IsGatewayPanelSslLetsEncryptMode));
            OnPropertyChanged(nameof(GatewayPanelSslModeIndex));
        }

        if (e.PropertyName is nameof(GatewayStateStore.TunnelKeyPassphrase) or nameof(GatewayStateStore.TunnelPassword) or nameof(GatewayStateStore.TunnelKeyPath))
        {
            OnPropertyChanged(nameof(KeyPassphraseState));
            OnPropertyChanged(nameof(PasswordState));
            OnPropertyChanged(nameof(HostKeyFileState));
        }

        if (e.PropertyName == nameof(GatewayStateStore.GatewayPanelConfiguredPassword))
        {
            OnPropertyChanged(nameof(GatewayPanelConfiguredPasswordState));
        }

        if (e.PropertyName == nameof(GatewayStateStore.GatewayPanelUploadedCertPath))
        {
            OnPropertyChanged(nameof(GatewayPanelUploadedCertState));
        }

        if (e.PropertyName == nameof(GatewayStateStore.GatewayPanelUploadedKeyPath))
        {
            OnPropertyChanged(nameof(GatewayPanelUploadedKeyState));
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

    private static string MaskSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string('*', value.Length);
    }

    private static string NormalizePanelSslMode(string? value)
    {
        var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
        return mode is "uploaded" ? "uploaded" : "letsencrypt";
    }

    private void BeginGatewayProgress(string operationName)
    {
        GatewayProgressPercent = 0;
        IsGatewayProgressIndeterminate = true;
        GatewayProgressMessage = operationName;
        IsGatewayProgressVisible = true;
    }

    private void UpdateGatewayProgress(DeploymentProgressSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Message))
        {
            GatewayProgressMessage = snapshot.Message;
        }

        if (snapshot.Percent <= 0)
        {
            IsGatewayProgressIndeterminate = true;
            return;
        }

        IsGatewayProgressIndeterminate = false;
        GatewayProgressPercent = Math.Clamp(_progressAggregator.ToOverallPercent(snapshot), 0, 100);
    }

    private void EndGatewayProgress(bool success, string finalMessage)
    {
        GatewayProgressMessage = string.IsNullOrWhiteSpace(finalMessage)
            ? (success ? "Operation completed." : "Operation failed.")
            : finalMessage.Trim();
        IsGatewayProgressIndeterminate = false;
        GatewayProgressPercent = success ? 100 : GatewayProgressPercent;
    }

    private async Task AutoDismissFeedbackAsync(string currentFeedback, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (string.Equals(Feedback, currentFeedback, StringComparison.Ordinal))
                {
                    Feedback = string.Empty;
                }
            });
        }
        catch (TaskCanceledException)
        {
            // Ignore cancellation when a new feedback message replaces the current one.
        }
    }
}
