using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.Core.Configuration;
using OmniRelay.UI.Models;
using OmniRelay.UI.Services;
using OmniRelay.UI.Views.Dialogs;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
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

    [ObservableProperty]
    private bool isPanelPasswordVisible;

    [ObservableProperty]
    private bool isFeedbackVisible;

    private CancellationTokenSource? _feedbackDismissCts;
    private CancellationTokenSource? _gatewayProgressHideCts;

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

    public string PanelPasswordDisplay =>
        IsPanelPasswordVisible
            ? (State.GatewayInitialPanelPassword ?? string.Empty)
            : MaskSecret(State.GatewayInitialPanelPassword);

    public string PanelPasswordToggleText => IsPanelPasswordVisible ? "Hide" : "Show";
    public string PanelPasswordToggleIconGlyph => IsPanelPasswordVisible ? "\uE8F4" : "\uE890";

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
    }

    partial void OnIsPanelPasswordVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(PanelPasswordDisplay));
        OnPropertyChanged(nameof(PanelPasswordToggleText));
        OnPropertyChanged(nameof(PanelPasswordToggleIconGlyph));
    }

    partial void OnGatewayProgressPercentChanged(double value)
    {
        OnPropertyChanged(nameof(GatewayProgressPercentText));
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
            BeginGatewayProgress("Running gateway bootstrap check...");
            var success = false;
            var finalMessage = "Gateway bootstrap check did not complete.";
            try
            {
                var op = await _gatewayDeployment.CheckGatewayBootstrapAsync(request, sudo, progress);
                GatewayBootstrapState = op.Success ? "Passed" : "Failed";
                Feedback = op.Message;
                AppendLog(op.Message);
                success = op.Success;
                finalMessage = op.Message;
            }
            finally
            {
                EndGatewayProgress(success, finalMessage);
            }
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
            BeginGatewayProgress("Installing gateway...");
            var success = false;
            var finalMessage = "Gateway install did not complete.";
            try
            {
                var op = await _gatewayDeployment.InstallGatewayAsync(request, sudo, progress);
                Feedback = op.Message;
                AppendLog(op.Message);
                if (op.Success)
                {
                    if (!string.IsNullOrWhiteSpace(op.PanelUrl))
                    {
                        State.GatewayPanelUrl = op.PanelUrl;
                    }

                    if (!string.IsNullOrWhiteSpace(op.PanelUsername))
                    {
                        State.GatewayPanelUsername = op.PanelUsername;
                    }

                    if (!string.IsNullOrWhiteSpace(op.InitialPanelPassword))
                    {
                        State.GatewayInitialPanelPassword = op.InitialPanelPassword;
                    }
                }

                await RefreshGatewayStatusAsync(sudo);
                await RefreshGatewayHealthAsync(request, sudo, progress);
                success = op.Success;
                finalMessage = op.Message;
            }
            finally
            {
                EndGatewayProgress(success, finalMessage);
            }
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
            BeginGatewayProgress("Starting gateway services...");
            var success = false;
            var finalMessage = "Gateway start did not complete.";
            try
            {
                var op = await _gatewayDeployment.StartGatewayAsync(request, sudo, progress);
                Feedback = op.Message;
                AppendLog(op.Message);
                await RefreshGatewayStatusAsync(sudo);
                success = op.Success;
                finalMessage = op.Message;
            }
            finally
            {
                EndGatewayProgress(success, finalMessage);
            }
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
            BeginGatewayProgress("Stopping gateway services...");
            var success = false;
            var finalMessage = "Gateway stop did not complete.";
            try
            {
                var op = await _gatewayDeployment.StopGatewayAsync(request, sudo, progress);
                Feedback = op.Message;
                AppendLog(op.Message);
                await RefreshGatewayStatusAsync(sudo);
                success = op.Success;
                finalMessage = op.Message;
            }
            finally
            {
                EndGatewayProgress(success, finalMessage);
            }
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
            BeginGatewayProgress("Uninstalling gateway...");
            var success = false;
            var finalMessage = "Gateway uninstall did not complete.";
            try
            {
                var op = await _gatewayDeployment.UninstallGatewayAsync(request, sudo, progress);
                Feedback = op.Message;
                AppendLog(op.Message);
                await RefreshGatewayStatusAsync(sudo);
                GatewayHealthReport = null;
                GatewayHealthSummary = "Not checked";
                success = op.Success;
                finalMessage = op.Message;
            }
            finally
            {
                EndGatewayProgress(success, finalMessage);
            }
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
            var progress = CreateGatewayProgressReporter();
            BeginGatewayProgress("Checking gateway health...");
            var success = false;
            var finalMessage = "Gateway health check did not complete.";
            try
            {
                success = await RefreshGatewayHealthAsync(request, sudo, progress);
                finalMessage = GatewayHealthSummary;
            }
            finally
            {
                EndGatewayProgress(success, finalMessage);
            }
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
            BeginGatewayProgress("Applying gateway DNS profile...");
            var success = false;
            var finalMessage = "DNS apply did not complete.";
            try
            {
                var op = await _gatewayDeployment.ApplyGatewayDnsAsync(request, sudo, progress);
                Feedback = op.Message;
                AppendLog(op.Message);
                await RefreshGatewayHealthAsync(request, sudo, progress);
                success = op.Success;
                finalMessage = op.Message;
            }
            finally
            {
                EndGatewayProgress(success, finalMessage);
            }
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
            BeginGatewayProgress("Checking gateway DNS path...");
            var success = false;
            var finalMessage = "DNS check did not complete.";
            try
            {
                var op = await _gatewayDeployment.CheckGatewayDnsAsync(request, sudo, progress);
                Feedback = op.Message;
                AppendLog(op.Message);
                await RefreshGatewayHealthAsync(request, sudo, progress);
                success = op.Success;
                finalMessage = op.Message;
            }
            finally
            {
                EndGatewayProgress(success, finalMessage);
            }
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
            BeginGatewayProgress("Repairing gateway DNS path...");
            var success = false;
            var finalMessage = "DNS repair did not complete.";
            try
            {
                var op = await _gatewayDeployment.RepairGatewayDnsAsync(request, sudo, progress);
                Feedback = op.Message;
                AppendLog(op.Message);
                await RefreshGatewayHealthAsync(request, sudo, progress);
                success = op.Success;
                finalMessage = op.Message;
            }
            finally
            {
                EndGatewayProgress(success, finalMessage);
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

    [RelayCommand]
    private void TogglePanelPasswordVisibility()
    {
        IsPanelPasswordVisible = !IsPanelPasswordVisible;
    }

    [RelayCommand]
    private void CopyPanelUsername()
    {
        var username = State.GatewayPanelUsername?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username))
        {
            Feedback = "Panel username is empty.";
            return;
        }

        Clipboard.SetText(username);
        Feedback = "Panel username copied.";
    }

    [RelayCommand]
    private void CopyPanelPassword()
    {
        var password = State.GatewayInitialPanelPassword ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            Feedback = "Panel password is empty.";
            return;
        }

        Clipboard.SetText(password);
        Feedback = "Panel password copied.";
    }

    [RelayCommand]
    private void OpenPanelUrl()
    {
        var panelUrl = State.GatewayPanelUrl?.Trim() ?? string.Empty;
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

        return new GatewayDeploymentRequest
        {
            Config = config,
            SelectedGatewayProtocol = selectedProtocol,
            GatewayPublicPort = publicPort,
            GatewayPanelPort = panelPort,
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

        if (e.PropertyName is nameof(GatewayStateStore.TunnelKeyPassphrase) or nameof(GatewayStateStore.TunnelPassword) or nameof(GatewayStateStore.TunnelKeyPath))
        {
            OnPropertyChanged(nameof(KeyPassphraseState));
            OnPropertyChanged(nameof(PasswordState));
            OnPropertyChanged(nameof(HostKeyFileState));
        }

        if (e.PropertyName == nameof(GatewayStateStore.GatewayInitialPanelPassword))
        {
            OnPropertyChanged(nameof(PanelPasswordDisplay));
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

    private void BeginGatewayProgress(string operationName)
    {
        _gatewayProgressHideCts?.Cancel();
        _gatewayProgressHideCts?.Dispose();
        _gatewayProgressHideCts = null;

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

        _gatewayProgressHideCts?.Cancel();
        _gatewayProgressHideCts?.Dispose();
        _gatewayProgressHideCts = new CancellationTokenSource();
        _ = HideGatewayProgressAsync(_gatewayProgressHideCts.Token);
    }

    private async Task HideGatewayProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.2), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsGatewayProgressVisible = false;
                IsGatewayProgressIndeterminate = true;
                GatewayProgressPercent = 0;
                GatewayProgressMessage = string.Empty;
            });
        }
        catch (TaskCanceledException)
        {
            // Ignore cancellation when a new operation starts before auto-hide.
        }
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
