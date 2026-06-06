using System.Collections.ObjectModel;
using System.Formats.Asn1;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Threading;
using System.Threading;
using Microsoft.Win32;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;
using OmniRelay.Core.Policy;
using OmniRelay.Core.Status;
using OmniRelay.Ipc;
using OmniRelay.UI.Models;
using CoreLocalGatewayProtocols = OmniRelay.Core.Configuration.LocalGatewayProtocols;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace OmniRelay.UI.Views.Dialogs;

public partial class RelayEditDialog : Window
{
    private readonly ObservableCollection<AdapterChoiceModel> _adapters;
    private readonly ObservableCollection<RelayPolicyListItemResult> _policyLists = [];
    private RelayStatus? _status;
    private readonly DispatcherTimer _statusRefreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string _tunnelKeyPassphrase = string.Empty;
    private bool _loading;
    private bool _isRelaySaved;
    private string? _selectedPolicyListId;
    private CancellationTokenSource? _toastCts;
    private string _lastResolvedTunnelHostKey = string.Empty;

    public sealed class FrpHostProfileResolution
    {
        public bool Found { get; init; }
        public bool HasConflict { get; init; }
        public int FrpServerPort { get; init; } = 7000;
        public string AuthToken { get; init; } = string.Empty;
        public int SuggestedTunnelRemotePort { get; init; } = 15000;
        public string ConflictMessage { get; init; } = string.Empty;
    }

    public RelayEditDialog(RelayConfig relay, ObservableCollection<AdapterChoiceModel> adapters, RelayStatus? status, bool isRelaySaved = false)
    {
        InitializeComponent();
        Relay = relay;
        _adapters = adapters;
        _status = status;
        _isRelaySaved = isRelaySaved;
        _statusRefreshTimer.Tick += async (_, _) => await RefreshStatusFromSourceAsync();
        Closed += (_, _) => _statusRefreshTimer.Stop();
        TunnelHostTextBox.TextChanged += TunnelHostTextBox_TextChanged;

        IncomingAdapterCombo.ItemsSource = adapters;
        OutgoingAdapterCombo.ItemsSource = adapters;
        PolicyListsGrid.ItemsSource = _policyLists;
        LoadRelay();
    }

    public RelayConfig Relay { get; }

    public event Func<RelayConfig, Task<bool>>? ApplyRequested;
    public event Func<RelayConfig, Task<OperationResult>>? TestTunnelRequested;
    public event Func<RelayConfig, string, Task<OperationResult>>? GatewayOperationRequested;
    public event Func<Task<OperationResult>>? ClearCachedSudoRequested;
    public event Func<string, Task<RelayPolicyListsResult>>? LoadPolicyListsRequested;
    public event Func<string, string, Task<RelayPolicyListDetailsResult>>? LoadPolicyListRequested;
    public event Func<string, string, string, int?, Task<RelayPolicyMutationResult>>? CreatePolicyListRequested;
    public event Func<string, string, string, string, Task<RelayPolicyMutationResult>>? UpdatePolicyListMetaRequested;
    public event Func<string, IReadOnlyList<string>, Task<OperationResult>>? ReorderPolicyListsRequested;
    public event Func<string, string, IReadOnlyList<string>, Task<PolicyCommitSummary>>? ReplacePolicyEntriesRequested;
    public event Func<string, string, Task<OperationResult>>? DeletePolicyListRequested;
    public event Func<string, Task<RelayStatus?>>? RefreshRelayStatusRequested;
    public event Func<string, FrpHostProfileResolution?>? ResolveFrpProfileForHostRequested;

    private bool IsRemote => string.Equals(GatewayTypes.Normalize(Relay.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase);

    private void LoadRelay()
    {
        _loading = true;
        try
        {
            NameTextBox.Text = Relay.Name;
            EnabledCheckBox.IsChecked = Relay.Enabled;
            GatewayTypeText.Text = GatewayTypes.Normalize(Relay.GatewayType);
            IncomingAdapterCombo.SelectedItem = ResolveSelectedAdapter(Relay.IncomingAdapterId, Relay.IncomingAdapterIfIndex);
            OutgoingAdapterCombo.SelectedItem = ResolveSelectedAdapter(Relay.OutgoingAdapterId, Relay.OutgoingAdapterIfIndex);
            RefreshAdapterIdentityText();

            HostTab.Visibility = IsRemote ? Visibility.Visible : Visibility.Collapsed;
            TunnelTab.Visibility = IsRemote ? Visibility.Visible : Visibility.Collapsed;
            DnsTab.Visibility = IsRemote ? Visibility.Visible : Visibility.Collapsed;
            AdminPanelTab.Visibility = Visibility.Visible;
            OperationsTab.Visibility = IsRemote ? Visibility.Visible : Visibility.Collapsed;

            LoadHost();
            LoadTunnel();
            LoadDns();
            LoadProtocol();
            LoadStatus();
            UpdateHeaderSummary();
            LoadOperationCenterSummary();
            RefreshRelayScopedTabs();
        }
        finally
        {
            _loading = false;
        }

        RefreshAuthMethodVisibility();
        RefreshProtocolFieldVisibility();
        RefreshPanelSslFieldVisibility();
    }

    private void LoadHost()
    {
        TunnelAuthMethodCombo.ItemsSource = new List<Option>
        {
            new Option(TunnelAuthMethods.Password, "Password"),
            new Option(TunnelAuthMethods.HostKey, "Host Key File")
        };

        TunnelHostTextBox.Text = Relay.RemoteGateway.TunnelHost;
        TunnelSshPortTextBox.Text = Relay.RemoteGateway.TunnelSshPort.ToString();
        TunnelUserTextBox.Text = Relay.RemoteGateway.TunnelUser;
        TunnelKeyPathTextBox.Text = Relay.RemoteGateway.TunnelPrivateKeyPath;
        _tunnelKeyPassphrase = Relay.RemoteGateway.TunnelPrivateKeyPassphrase;
        RefreshKeyPassphraseState();
        TunnelPasswordBox.Password = Relay.RemoteGateway.TunnelPassword;
        SelectOption(TunnelAuthMethodCombo, TunnelAuthMethods.Normalize(Relay.RemoteGateway.TunnelAuthMethod));
    }

    private void LoadTunnel()
    {
        FrpServerPortTextBox.Text = (Relay.FrpProfilePortOverride is > 0 and <= 65535 ? Relay.FrpProfilePortOverride : 7000).ToString();
        TunnelRemotePortTextBox.Text = (Relay.RemoteGateway.TunnelRemotePort is > 0 and <= 65535 ? Relay.RemoteGateway.TunnelRemotePort : 15000).ToString();
        TunnelProbeUrlTextBox.Text = string.IsNullOrWhiteSpace(Relay.RemoteGateway.TunnelProbeUrl)
            ? "https://1.1.1.1/cdn-cgi/trace"
            : Relay.RemoteGateway.TunnelProbeUrl.Trim();

        var token = (Relay.FrpProfileTokenOverride ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            token = GenerateFrpToken();
        }

        FrpAuthTokenTextBox.Text = token;
        ResolveTunnelProfileFromHost(force: true);
    }

    private void TunnelHostTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsRemote)
        {
            return;
        }

        ResolveTunnelProfileFromHost(force: false);
    }

    private void ResolveTunnelProfileFromHost(bool force)
    {
        var host = TunnelHostTextBox.Text.Trim();
        var hostKey = host.ToLowerInvariant();
        if (!force && string.Equals(hostKey, _lastResolvedTunnelHostKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastResolvedTunnelHostKey = hostKey;
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        var profile = ResolveFrpProfileForHostRequested?.Invoke(host);
        if (profile?.Found == true)
        {
            FrpServerPortTextBox.Text = (profile.FrpServerPort is > 0 and <= 65535 ? profile.FrpServerPort : 7000).ToString();
            FrpAuthTokenTextBox.Text = string.IsNullOrWhiteSpace(profile.AuthToken)
                ? GenerateFrpToken()
                : profile.AuthToken.Trim();
            var currentTunnelRemotePort = ParsePort(TunnelRemotePortTextBox.Text, 15000);
            var shouldApplySuggestedPort =
                Relay.RemoteGateway.TunnelRemotePort <= 0 ||
                currentTunnelRemotePort == 15000 ||
                currentTunnelRemotePort == ParsePort(Relay.RemoteGateway.TunnelRemotePort.ToString(), 15000);
            if (shouldApplySuggestedPort)
            {
                TunnelRemotePortTextBox.Text = (profile.SuggestedTunnelRemotePort is > 0 and <= 65535
                    ? profile.SuggestedTunnelRemotePort
                    : 15000).ToString();
            }
            if (profile.HasConflict)
            {
                FeedbackTextBlock.Text = string.IsNullOrWhiteSpace(profile.ConflictMessage)
                    ? "FRP profile conflict detected for this host."
                    : profile.ConflictMessage;
            }
            return;
        }

        if (!int.TryParse(FrpServerPortTextBox.Text, out var frpsPort) || frpsPort <= 0 || frpsPort > 65535)
        {
            FrpServerPortTextBox.Text = "7000";
        }

        if (string.IsNullOrWhiteSpace(FrpAuthTokenTextBox.Text))
        {
            FrpAuthTokenTextBox.Text = GenerateFrpToken();
        }
    }

    private void LoadDns()
    {
        DohEndpointsTextBox.Text = Relay.RemoteGateway.DohEndpoints;
    }

    private void LoadProtocol()
    {
            if (IsRemote)
            {
                ProtocolCombo.ItemsSource = GatewayProtocols.All
                    .Select(x => new Option(x.Value, x.Label))
                    .ToList();
                BootstrapModeCombo.ItemsSource = new List<Option>
                {
                    new Option(GatewayBootstrapModes.Tunnel, "Tunnel"),
                    new Option(GatewayBootstrapModes.Direct, "Direct")
                };
                SelectOption(ProtocolCombo, GatewayProtocols.Normalize(Relay.RemoteGateway.Protocol));
                SelectOption(BootstrapModeCombo, GatewayBootstrapModes.Normalize(Relay.RemoteGateway.BootstrapMode));
                ProtocolTlsModeCombo.ItemsSource = new List<Option>
                {
                    new Option("uploaded", "Uploaded certificate"),
                    new Option("letsencrypt", "Let's Encrypt")
                };
                NaiveNetworkCombo.ItemsSource = new List<Option>
                {
                    new Option(string.Empty, "Default"),
                    new Option("tcp", "TCP"),
                    new Option("udp", "UDP"),
                    new Option("tcp,udp", "TCP+UDP")
                };
                VlessFlowCombo.ItemsSource = new List<Option>
                {
                    new Option(string.Empty, "None"),
                    new Option("xtls-rprx-vision", "xtls-rprx-vision")
                };
                ProtocolTlsAlpnListBox.ItemsSource = new[] { "h2", "http/1.1", "h3" };
                ProtocolPortTextBox.Text = Relay.RemoteGateway.PublicPort.ToString();
                ProtocolTlsEnabledCheckBox.IsChecked = Relay.RemoteGateway.ProtocolTlsEnabled;
                ProtocolTlsServerNameTextBox.Text = Relay.RemoteGateway.ProtocolTlsServerName;
                ProtocolTlsCertPathTextBox.Text = Relay.RemoteGateway.ProtocolCertPath;
                ProtocolTlsKeyPathTextBox.Text = Relay.RemoteGateway.ProtocolKeyPath;
                SelectOption(ProtocolTlsModeCombo, string.IsNullOrWhiteSpace(Relay.RemoteGateway.ProtocolTlsMode) ? "uploaded" : Relay.RemoteGateway.ProtocolTlsMode);
                ApplyTlsAlpnSelection(Relay.RemoteGateway.ProtocolAlpnCsv);
                GatewayProxyUsernameTextBox.Text = Relay.RemoteGateway.ProxyUsername;
                GatewayProxyPasswordBox.Password = Relay.RemoteGateway.ProxyPassword;
                SelectOption(VlessFlowCombo, Relay.RemoteGateway.VlessTlsFlow);
                Hysteria2UpMbpsTextBox.Text = Relay.RemoteGateway.Hysteria2UpMbps.ToString();
                Hysteria2DownMbpsTextBox.Text = Relay.RemoteGateway.Hysteria2DownMbps.ToString();
                Hysteria2ObfsPasswordTextBox.Text = Relay.RemoteGateway.Hysteria2ObfsPassword;
                Hysteria2IgnoreClientBandwidthCheckBox.IsChecked = Relay.RemoteGateway.Hysteria2IgnoreClientBandwidth;
                Hysteria2MasqueradeUrlTextBox.Text = Relay.RemoteGateway.Hysteria2MasqueradeUrl;
                SelectOption(NaiveNetworkCombo, Relay.RemoteGateway.NaiveNetwork);
                NaiveQuicCcTextBox.Text = Relay.RemoteGateway.NaiveQuicCongestionControl;
                ShadowTlsCamouflageTextBox.Text = string.IsNullOrWhiteSpace(Relay.RemoteGateway.ShadowTlsCamouflageServer)
                    ? GatewayCamouflageCatalog.GetRandom()
                    : Relay.RemoteGateway.ShadowTlsCamouflageServer;
                ShadowTlsStrictModeCheckBox.IsChecked = Relay.RemoteGateway.ShadowTlsStrictMode;
                ShadowTlsWildcardSniTextBox.Text = Relay.RemoteGateway.ShadowTlsWildcardSni;
                OpenVpnNetworkTextBox.Text = Relay.RemoteGateway.OpenVpnNetwork;
                IpsecL2tpNetworkTextBox.Text = Relay.RemoteGateway.IpsecL2tpNetwork;
                OpenVpnSharedCaCertPathTextBox.Text = Relay.RemoteGateway.OpenVpnSharedCaCertPath;
                OpenVpnSharedClientCertPathTextBox.Text = Relay.RemoteGateway.OpenVpnSharedClientCertPath;
                OpenVpnSharedClientKeyPathTextBox.Text = Relay.RemoteGateway.OpenVpnSharedClientKeyPath;
                OpenVpnSharedTlsCryptKeyPathTextBox.Text = Relay.RemoteGateway.OpenVpnSharedTlsCryptKeyPath;
            }

            PanelPortTextBox.Text = Relay.OmniPanel.Port.ToString();
            PanelUserTextBox.Text = Relay.OmniPanel.Username;
            PanelPasswordBox.Password = Relay.OmniPanel.Password;
            PanelDomainTextBox.Text = Relay.OmniPanel.Domain;
            PanelDomainOnlyCheckBox.IsChecked = Relay.OmniPanel.DomainOnly;
            PanelUseSslCheckBox.IsChecked = Relay.OmniPanel.UseSsl;
            PanelSslModeCombo.ItemsSource = new List<Option>
            {
                new Option("letsencrypt", "Let's Encrypt"),
                new Option("uploaded", "Uploaded certificate")
            };
            SelectOption(PanelSslModeCombo, NormalizePanelSslMode(Relay.OmniPanel.SslMode));
            PanelUploadedCertPathTextBox.Text = Relay.OmniPanel.UploadedCertPath;
            PanelUploadedKeyPathTextBox.Text = Relay.OmniPanel.UploadedKeyPath;

            if (IsRemote)
            {
                return;
            }

        ProtocolCombo.ItemsSource = new List<Option>
        {
            new Option(CoreLocalGatewayProtocols.VlessTcpPlain, "VLESS TCP Plain"),
            new Option(CoreLocalGatewayProtocols.Shadowsocks, "Shadowsocks"),
            new Option(CoreLocalGatewayProtocols.OpenVpnTcp, "OpenVPN")
        };
        BootstrapModeCombo.ItemsSource = new List<Option>();
        SelectOption(ProtocolCombo, CoreLocalGatewayProtocols.Normalize(Relay.LocalGateway.Protocol));
        ProtocolPortTextBox.Text = Relay.LocalGateway.Port.ToString();
        OpenVpnNetworkTextBox.Text = string.IsNullOrWhiteSpace(Relay.RemoteGateway.OpenVpnNetwork)
            ? "10.29.0.0/24"
            : Relay.RemoteGateway.OpenVpnNetwork;
        IpsecL2tpNetworkTextBox.Text = string.IsNullOrWhiteSpace(Relay.RemoteGateway.IpsecL2tpNetwork)
            ? "10.39.0.0/24"
            : Relay.RemoteGateway.IpsecL2tpNetwork;
        OpenVpnSharedCaCertPathTextBox.Text = Relay.RemoteGateway.OpenVpnSharedCaCertPath;
        OpenVpnSharedClientCertPathTextBox.Text = Relay.RemoteGateway.OpenVpnSharedClientCertPath;
        OpenVpnSharedClientKeyPathTextBox.Text = Relay.RemoteGateway.OpenVpnSharedClientKeyPath;
        OpenVpnSharedTlsCryptKeyPathTextBox.Text = Relay.RemoteGateway.OpenVpnSharedTlsCryptKeyPath;
        OpenVpnPublicHostTextBox.Text = Relay.LocalGateway.RemoteAddress;
    }

    private void LoadStatus()
    {
        StatusStaleText.Text = FormatBool(_status?.StatusStale);
        ProxyRunningText.Text = FormatBool(_status?.DataPlaneListening);
        SetGridRowVisibility(StatusGrid, 8, true);
        SetGridRowVisibility(StatusGrid, 9, true);
        SetGridRowVisibility(StatusGrid, 10, true);
        SetGridRowVisibility(StatusGrid, 11, true);
        SetGridRowVisibility(StatusGrid, 5, true);
        SetGridRowVisibility(StatusGrid, 6, true);
        StatusRow2Label.Text = "Tunnel State";
        StatusRow3Label.Text = "Health State";
        StatusRow4Label.Text = "Health Reason";
        StatusRow5Label.Text = "Recovery Tier";
        StatusRow6Label.Text = "Recovery Action";
        StatusRow7Label.Text = "Consecutive Failures";
        StatusRow8Label.Text = "Last Healthy";
        StatusRow9Label.Text = "Last Local Probe";
        StatusRow10Label.Text = "Last End-to-End Probe";
        StatusRow11Label.Text = "Tunnel Last Error";

        if (!IsRemote)
        {
            StatusRow2Label.Text = "Local Gateway State";
            StatusRow3Label.Text = "Health State";
            StatusRow4Label.Text = "Health Reason";
            StatusRow7Label.Text = "Last Status Update";
            SetGridRowVisibility(StatusGrid, 8, false);
            SetGridRowVisibility(StatusGrid, 9, false);
            SetGridRowVisibility(StatusGrid, 10, false);
            SetGridRowVisibility(StatusGrid, 11, false);
            SetGridRowVisibility(StatusGrid, 5, false);
            SetGridRowVisibility(StatusGrid, 6, false);
            TunnelStateText.Text = _status?.LocalGatewayState ?? (_status?.DataPlaneListening == true ? "active" : "inactive");
            HealthStateText.Text = _status?.HealthState ?? (_status?.LocalGatewayHealthReason ?? string.Empty);
            HealthReasonText.Text = _status?.HealthReasonCode ?? _status?.TunnelLastError ?? string.Empty;
            RecoveryTierText.Text = string.Empty;
            RecoveryActionText.Text = string.Empty;
            ConsecutiveFailuresText.Text = FormatDate(_status?.LastStatusUpdateUtc);
            LastHealthyText.Text = string.Empty;
            LastLocalProbeText.Text = string.Empty;
            LastEndToEndProbeText.Text = string.Empty;
            TunnelLastErrorText.Text = string.Empty;
            return;
        }

        TunnelStateText.Text = _status?.TunnelState ?? "Unavailable";
        HealthStateText.Text = _status?.HealthState ?? "Unavailable";
        HealthReasonText.Text = _status?.HealthReasonCode ?? _status?.LastError ?? string.Empty;
        RecoveryTierText.Text = _status?.RecoveryTier.ToString() ?? "Unavailable";
        RecoveryActionText.Text = _status?.RecoveryAction ?? string.Empty;
        ConsecutiveFailuresText.Text = _status?.ConsecutiveFailures.ToString() ?? "Unavailable";
        LastHealthyText.Text = FormatDate(_status?.LastHealthyUtc);
        LastLocalProbeText.Text = FormatDate(_status?.LastLocalProbeUtc);
        LastEndToEndProbeText.Text = FormatDate(_status?.LastEndToEndProbeUtc);
        TunnelLastErrorText.Text = _status?.TunnelLastError ?? string.Empty;
        UpdateHeaderSummary();
    }

    private void UpdateHeaderSummary()
    {
        RelayIdSummaryText.Text = string.IsNullOrWhiteSpace(Relay.Id) ? "(new relay)" : Relay.Id;

        if (IsRemote)
        {
            TunnelStateSummaryText.Text = _status?.TunnelState ?? "Unavailable";
            HealthStateSummaryText.Text = _status?.HealthState ?? "Unavailable";
            return;
        }

        TunnelStateSummaryText.Text = _status?.LocalGatewayState ?? "LocalMode";
        HealthStateSummaryText.Text = _status?.HealthState ?? "Unavailable";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        await PersistRelayAsync(closeOnSuccess: false);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await PersistRelayAsync(closeOnSuccess: true);
    }

    private async Task PersistRelayAsync(bool closeOnSuccess)
    {
        if (!TryUpdateRelayFromControls())
        {
            return;
        }

        if (ApplyRequested is null)
        {
            FeedbackTextBlock.Text = "No apply handler is available.";
            return;
        }

        try
        {
            IsEnabled = false;
            FeedbackTextBlock.Text = closeOnSuccess ? "Saving..." : "Applying...";
            var wasRelaySaved = _isRelaySaved;
            var success = await ApplyRequested(Relay);
            FeedbackTextBlock.Text = success ? "Applied." : "Apply failed. Check service status and logs.";
            if (success)
            {
                _isRelaySaved = true;
                RefreshRelayScopedTabs();
                await RefreshStatusFromSourceAsync();
                var allowClose = closeOnSuccess;
                if (IsRemote && !wasRelaySaved && GatewayOperationRequested is not null)
                {
                    OperationFeedbackText.Text = "Running initial gateway install...";
                    var installResult = await GatewayOperationRequested(Relay, "install");
                    OperationFeedbackText.Text = installResult.Message;
                    ApplyGatewayOperationSummary("install", installResult);
                    await RefreshStatusFromSourceAsync();

                    if (!installResult.Success)
                    {
                        allowClose = false;
                        FeedbackTextBlock.Text = $"Applied locally, but remote gateway install failed: {installResult.Message}";
                    }
                }

                if (allowClose)
                {
                    DialogResult = true;
                }
            }
        }
        catch (Exception ex)
        {
            FeedbackTextBlock.Text = $"{(closeOnSuccess ? "Save" : "Apply")} failed: {ex.Message}";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private bool TryUpdateRelayFromControls()
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            FeedbackTextBlock.Text = "Relay name is required.";
            return false;
        }

        Relay.Name = NameTextBox.Text.Trim();
        Relay.Enabled = EnabledCheckBox.IsChecked == true;
        Relay.IncomingAdapterId = (IncomingAdapterCombo.SelectedItem as AdapterChoiceModel)?.AdapterId ?? string.Empty;
        Relay.IncomingAdapterIfIndex = (IncomingAdapterCombo.SelectedItem as AdapterChoiceModel)?.IfIndex ?? -1;
        Relay.OutgoingAdapterId = (OutgoingAdapterCombo.SelectedItem as AdapterChoiceModel)?.AdapterId ?? string.Empty;
        Relay.OutgoingAdapterIfIndex = (OutgoingAdapterCombo.SelectedItem as AdapterChoiceModel)?.IfIndex ?? -1;

            Relay.OmniPanel.Port = ParsePort(PanelPortTextBox.Text, 2054);
            Relay.OmniPanel.Username = PanelUserTextBox.Text.Trim();
            Relay.OmniPanel.Password = PanelPasswordBox.Password;
            Relay.OmniPanel.Domain = PanelDomainTextBox.Text.Trim();
            Relay.OmniPanel.DomainOnly = PanelDomainOnlyCheckBox.IsChecked == true;
            Relay.OmniPanel.UseSsl = PanelUseSslCheckBox.IsChecked == true;
            Relay.OmniPanel.SslMode = Relay.OmniPanel.UseSsl
                ? NormalizePanelSslMode(GetSelectedValue(PanelSslModeCombo, "letsencrypt"))
                : "none";
            Relay.OmniPanel.UploadedCertPath = PanelUploadedCertPathTextBox.Text.Trim();
            Relay.OmniPanel.UploadedKeyPath = PanelUploadedKeyPathTextBox.Text.Trim();
            Relay.RemoteGateway.PanelPort = Relay.OmniPanel.Port;
            Relay.RemoteGateway.PanelUser = Relay.OmniPanel.Username;
            Relay.RemoteGateway.PanelPassword = Relay.OmniPanel.Password;
            Relay.RemoteGateway.PanelDomain = Relay.OmniPanel.Domain;
            Relay.RemoteGateway.PanelDomainOnly = Relay.OmniPanel.DomainOnly;
            Relay.RemoteGateway.PanelUseSsl = Relay.OmniPanel.UseSsl;
            Relay.RemoteGateway.PanelSslMode = Relay.OmniPanel.SslMode;
            Relay.RemoteGateway.PanelUploadedCertPath = Relay.OmniPanel.UploadedCertPath;
            Relay.RemoteGateway.PanelUploadedKeyPath = Relay.OmniPanel.UploadedKeyPath;

            if (Relay.OmniPanel.UseSsl &&
                string.Equals(Relay.OmniPanel.SslMode, "uploaded", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(Relay.OmniPanel.UploadedCertPath) || string.IsNullOrWhiteSpace(Relay.OmniPanel.UploadedKeyPath)))
            {
                FeedbackTextBlock.Text = "Uploaded SSL mode requires both OmniPanel certificate and key files.";
                return false;
            }

            var selectedRemoteProtocol = GatewayProtocols.Normalize(GetSelectedValue(ProtocolCombo, GatewayProtocols.VlessTlsSingbox));
            var selectedLocalProtocol = CoreLocalGatewayProtocols.Normalize(GetSelectedValue(ProtocolCombo, CoreLocalGatewayProtocols.VlessTcpPlain));
            var openVpnSelected = (IsRemote && selectedRemoteProtocol == GatewayProtocols.OpenVpnTcpSingbox) ||
                                  (!IsRemote && selectedLocalProtocol == CoreLocalGatewayProtocols.OpenVpnTcp);

            Relay.RemoteGateway.OpenVpnNetwork = string.IsNullOrWhiteSpace(OpenVpnNetworkTextBox.Text) ? "10.29.0.0/24" : OpenVpnNetworkTextBox.Text.Trim();
            Relay.RemoteGateway.IpsecL2tpNetwork = string.IsNullOrWhiteSpace(IpsecL2tpNetworkTextBox.Text) ? "10.39.0.0/24" : IpsecL2tpNetworkTextBox.Text.Trim();
            if (selectedRemoteProtocol == GatewayProtocols.IpsecL2tpSingbox &&
                string.IsNullOrWhiteSpace(Relay.RemoteGateway.IpsecL2tpPreSharedKey))
            {
                Relay.RemoteGateway.IpsecL2tpPreSharedKey = GenerateFrpToken();
            }
            Relay.RemoteGateway.OpenVpnSharedCaCertPath = OpenVpnSharedCaCertPathTextBox.Text.Trim();
            Relay.RemoteGateway.OpenVpnSharedClientCertPath = OpenVpnSharedClientCertPathTextBox.Text.Trim();
            Relay.RemoteGateway.OpenVpnSharedClientKeyPath = OpenVpnSharedClientKeyPathTextBox.Text.Trim();
            Relay.RemoteGateway.OpenVpnSharedTlsCryptKeyPath = OpenVpnSharedTlsCryptKeyPathTextBox.Text.Trim();

            if (openVpnSelected)
            {
                if (!IsValidIpv4Cidr(Relay.RemoteGateway.OpenVpnNetwork))
                {
                    FeedbackTextBlock.Text = "OpenVPN network must be a valid IPv4 CIDR (example: 10.29.0.0/24).";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(Relay.RemoteGateway.OpenVpnSharedCaCertPath) ||
                    string.IsNullOrWhiteSpace(Relay.RemoteGateway.OpenVpnSharedClientCertPath) ||
                    string.IsNullOrWhiteSpace(Relay.RemoteGateway.OpenVpnSharedClientKeyPath) ||
                    string.IsNullOrWhiteSpace(Relay.RemoteGateway.OpenVpnSharedTlsCryptKeyPath))
                {
                    FeedbackTextBlock.Text = "OpenVPN shared bundle is required: CA cert, client cert, client key, and tls-crypt key.";
                    return false;
                }
            }

            if (IsRemote && selectedRemoteProtocol == GatewayProtocols.IpsecL2tpSingbox && !IsValidIpv4Cidr(Relay.RemoteGateway.IpsecL2tpNetwork))
            {
                FeedbackTextBlock.Text = "IPSec/L2TP network must be a valid IPv4 CIDR (example: 10.39.0.0/24).";
                return false;
            }

        if (IsRemote)
        {
            Relay.RemoteGateway.TunnelHost = TunnelHostTextBox.Text.Trim();
            Relay.RemoteGateway.TunnelSshPort = ParsePort(TunnelSshPortTextBox.Text, 22);
            Relay.FrpProfilePortOverride = ParsePort(FrpServerPortTextBox.Text, 7000);
            Relay.RemoteGateway.TunnelRemotePort = ParsePort(TunnelRemotePortTextBox.Text, 15000);
            Relay.RemoteGateway.TunnelProbeUrl = string.IsNullOrWhiteSpace(TunnelProbeUrlTextBox.Text)
                ? "https://1.1.1.1/cdn-cgi/trace"
                : TunnelProbeUrlTextBox.Text.Trim();
            Relay.FrpProfileTokenOverride = FrpAuthTokenTextBox.Text.Trim();
            Relay.RemoteGateway.TunnelUser = string.IsNullOrWhiteSpace(TunnelUserTextBox.Text) ? "OmniRelay" : TunnelUserTextBox.Text.Trim();
            Relay.RemoteGateway.TunnelAuthMethod = GetSelectedValue(TunnelAuthMethodCombo, TunnelAuthMethods.Password);
            Relay.RemoteGateway.TunnelPrivateKeyPath = TunnelKeyPathTextBox.Text.Trim();
            Relay.RemoteGateway.TunnelPrivateKeyPassphrase = _tunnelKeyPassphrase;
            Relay.RemoteGateway.TunnelPassword = TunnelPasswordBox.Password;
            Relay.RemoteGateway.DohEndpoints = DohEndpointsTextBox.Text.Trim();
            Relay.RemoteGateway.BootstrapMode = GatewayBootstrapModes.Normalize(GetSelectedValue(BootstrapModeCombo, GatewayBootstrapModes.Tunnel));
            Relay.RemoteGateway.Protocol = selectedRemoteProtocol;
            Relay.RemoteGateway.PublicPort = GatewayProtocols.Normalize(Relay.RemoteGateway.Protocol) == GatewayProtocols.IpsecL2tpSingbox
                ? 500
                : ParsePort(ProtocolPortTextBox.Text, 443);
            Relay.RemoteGateway.ProtocolTlsEnabled = ProtocolTlsEnabledCheckBox.IsChecked == true;
            Relay.RemoteGateway.ProtocolTlsServerName = ProtocolTlsServerNameTextBox.Text.Trim();
            Relay.RemoteGateway.ProtocolCertPath = ProtocolTlsCertPathTextBox.Text.Trim();
            Relay.RemoteGateway.ProtocolKeyPath = ProtocolTlsKeyPathTextBox.Text.Trim();
            Relay.RemoteGateway.ProtocolTlsMode = GetSelectedValue(ProtocolTlsModeCombo, "uploaded");
            Relay.RemoteGateway.ProtocolAlpnCsv = GetSelectedTlsAlpnCsv();
            Relay.RemoteGateway.ProxyUsername = GatewayProxyUsernameTextBox.Text.Trim();
            Relay.RemoteGateway.ProxyPassword = GatewayProxyPasswordBox.Password;
            Relay.RemoteGateway.VlessTlsFlow = GetSelectedValue(VlessFlowCombo, string.Empty);
            Relay.RemoteGateway.Hysteria2UpMbps = ParsePositiveInt(Hysteria2UpMbpsTextBox.Text, 100);
            Relay.RemoteGateway.Hysteria2DownMbps = ParsePositiveInt(Hysteria2DownMbpsTextBox.Text, 100);
            Relay.RemoteGateway.Hysteria2ObfsPassword = Hysteria2ObfsPasswordTextBox.Text.Trim();
            Relay.RemoteGateway.Hysteria2IgnoreClientBandwidth = Hysteria2IgnoreClientBandwidthCheckBox.IsChecked == true;
            Relay.RemoteGateway.Hysteria2MasqueradeUrl = Hysteria2MasqueradeUrlTextBox.Text.Trim();
            Relay.RemoteGateway.NaiveNetwork = GetSelectedValue(NaiveNetworkCombo, string.Empty);
            Relay.RemoteGateway.NaiveQuicCongestionControl = NaiveQuicCcTextBox.Text.Trim();
            Relay.RemoteGateway.ShadowTlsCamouflageServer = ShadowTlsCamouflageTextBox.Text.Trim();
            Relay.RemoteGateway.ShadowTlsStrictMode = ShadowTlsStrictModeCheckBox.IsChecked == true;
            Relay.RemoteGateway.ShadowTlsWildcardSni = ShadowTlsWildcardSniTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(Relay.FrpProfileTokenOverride))
            {
                FeedbackTextBlock.Text = "FRP token is required.";
                return false;
            }

            if (!Uri.TryCreate(Relay.RemoteGateway.TunnelProbeUrl, UriKind.Absolute, out var probeUri) ||
                (probeUri.Scheme != Uri.UriSchemeHttps && probeUri.Scheme != Uri.UriSchemeHttp))
            {
                FeedbackTextBlock.Text = "Tunnelctl probe URL must be a valid absolute http/https URL.";
                return false;
            }

            if (Relay.FrpProfilePortOverride == Relay.RemoteGateway.TunnelRemotePort)
            {
                FeedbackTextBlock.Text = "FRPS port and data tunnel remote port must be distinct.";
                return false;
            }
        }
        else
        {
            Relay.LocalGateway.Protocol = selectedLocalProtocol;
            Relay.LocalGateway.Port = ParsePort(ProtocolPortTextBox.Text, 443);
            Relay.LocalGateway.RemoteAddress = OpenVpnPublicHostTextBox.Text.Trim();
            Relay.LocalGateway.RuntimeEnabled = true;
        }

        FeedbackTextBlock.Text = string.Empty;
        return true;
    }

    private void TunnelAuthMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            RefreshAuthMethodVisibility();
        }
    }

    private void TunnelAuthMethodCombo_DropDownClosed(object sender, EventArgs e)
    {
        RefreshAuthMethodVisibility();
    }

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            if (IsRemote &&
                GatewayProtocols.Normalize(GetSelectedValue(ProtocolCombo, GatewayProtocols.VlessTlsSingbox)) == GatewayProtocols.IpsecL2tpSingbox &&
                string.IsNullOrWhiteSpace(IpsecL2tpNetworkTextBox.Text))
            {
                IpsecL2tpNetworkTextBox.Text = "10.39.0.0/24";
            }
            if (!IsRemote &&
                CoreLocalGatewayProtocols.Normalize(GetSelectedValue(ProtocolCombo, CoreLocalGatewayProtocols.VlessTcpPlain)) == CoreLocalGatewayProtocols.OpenVpnTcp &&
                string.IsNullOrWhiteSpace(OpenVpnNetworkTextBox.Text))
            {
                OpenVpnNetworkTextBox.Text = "10.29.0.0/24";
            }
            RefreshProtocolFieldVisibility();
        }
    }

    private void ProtocolCombo_DropDownClosed(object sender, EventArgs e)
    {
        RefreshProtocolFieldVisibility();
    }

    private void ProtocolTlsEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            RefreshProtocolFieldVisibility();
        }
    }

    private void ProtocolTlsModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            RefreshProtocolFieldVisibility();
        }
    }

    private void PanelUseSslCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            RefreshPanelSslFieldVisibility();
        }
    }

    private void PanelSslModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            RefreshPanelSslFieldVisibility();
        }
    }

    private void IncomingAdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshAdapterIdentityText();
    }

    private void OutgoingAdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshAdapterIdentityText();
    }

    private void SwitchCamouflage_Click(object sender, RoutedEventArgs e)
    {
        ShadowTlsCamouflageTextBox.Text = GetNextCamouflage(ShadowTlsCamouflageTextBox.Text);
    }

    private void BrowseTunnelKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select SSH private key file",
            Filter = "SSH Key files|id_*;*.pem;*.ppk;*.*|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            TunnelKeyPathTextBox.Text = dialog.FileName;
        }
    }

    private void ClearTunnelKeyPath_Click(object sender, RoutedEventArgs e)
    {
        TunnelKeyPathTextBox.Text = string.Empty;
    }

    private void BrowseTlsCertPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select TLS certificate file",
            Filter = "Certificate files|*.crt;*.pem;*.cer;*.*|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            ProtocolTlsCertPathTextBox.Text = dialog.FileName;
        }
    }

    private void ClearTlsCertPath_Click(object sender, RoutedEventArgs e)
    {
        ProtocolTlsCertPathTextBox.Text = string.Empty;
    }

    private void BrowseTlsKeyPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select TLS private key file",
            Filter = "Key files|*.key;*.pem;*.*|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            ProtocolTlsKeyPathTextBox.Text = dialog.FileName;
        }
    }

    private void ClearTlsKeyPath_Click(object sender, RoutedEventArgs e)
    {
        ProtocolTlsKeyPathTextBox.Text = string.Empty;
    }

    private void BrowseOpenVpnSharedCaCertPath_Click(object sender, RoutedEventArgs e) => BrowseFileInto(OpenVpnSharedCaCertPathTextBox, "Select OpenVPN CA certificate", "Certificate files|*.crt;*.pem;*.cer;*.*|All files|*.*");
    private void ClearOpenVpnSharedCaCertPath_Click(object sender, RoutedEventArgs e) => OpenVpnSharedCaCertPathTextBox.Text = string.Empty;
    private void BrowseOpenVpnSharedClientCertPath_Click(object sender, RoutedEventArgs e) => BrowseFileInto(OpenVpnSharedClientCertPathTextBox, "Select OpenVPN client certificate", "Certificate files|*.crt;*.pem;*.cer;*.*|All files|*.*");
    private void ClearOpenVpnSharedClientCertPath_Click(object sender, RoutedEventArgs e) => OpenVpnSharedClientCertPathTextBox.Text = string.Empty;
    private void BrowseOpenVpnSharedClientKeyPath_Click(object sender, RoutedEventArgs e) => BrowseFileInto(OpenVpnSharedClientKeyPathTextBox, "Select OpenVPN client private key", "Key files|*.key;*.pem;*.*|All files|*.*");
    private void ClearOpenVpnSharedClientKeyPath_Click(object sender, RoutedEventArgs e) => OpenVpnSharedClientKeyPathTextBox.Text = string.Empty;
    private void BrowseOpenVpnSharedTlsCryptKeyPath_Click(object sender, RoutedEventArgs e) => BrowseFileInto(OpenVpnSharedTlsCryptKeyPathTextBox, "Select OpenVPN tls-crypt key", "Key files|*.key;*.pem;*.*|All files|*.*");
    private void ClearOpenVpnSharedTlsCryptKeyPath_Click(object sender, RoutedEventArgs e) => OpenVpnSharedTlsCryptKeyPathTextBox.Text = string.Empty;
    private void BrowsePanelUploadedCertPath_Click(object sender, RoutedEventArgs e) => BrowseFileInto(PanelUploadedCertPathTextBox, "Select OmniPanel TLS certificate", "Certificate files|*.crt;*.pem;*.cer;*.*|All files|*.*");
    private void ClearPanelUploadedCertPath_Click(object sender, RoutedEventArgs e) => PanelUploadedCertPathTextBox.Text = string.Empty;
    private void BrowsePanelUploadedKeyPath_Click(object sender, RoutedEventArgs e) => BrowseFileInto(PanelUploadedKeyPathTextBox, "Select OmniPanel TLS private key", "Key files|*.key;*.pem;*.*|All files|*.*");
    private void ClearPanelUploadedKeyPath_Click(object sender, RoutedEventArgs e) => PanelUploadedKeyPathTextBox.Text = string.Empty;

    private async void GenerateOpenVpnBundle_Click(object sender, RoutedEventArgs e)
    {
        var relayId = string.IsNullOrWhiteSpace(Relay.Id) ? Guid.NewGuid().ToString("N") : Relay.Id.Trim();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniRelay", "openvpn-bundles", relayId);
        Directory.CreateDirectory(root);
        var caCrt = Path.Combine(root, "ca.crt");
        var clientCrt = Path.Combine(root, "client.crt");
        var clientKey = Path.Combine(root, "client.key");
        var taKey = Path.Combine(root, "ta.key");

        try
        {
            await Task.Run(() =>
            {
                GenerateOpenVpnSharedBundle(root, caCrt, clientCrt, clientKey, taKey);
            });
            OpenVpnSharedCaCertPathTextBox.Text = caCrt;
            OpenVpnSharedClientCertPathTextBox.Text = clientCrt;
            OpenVpnSharedClientKeyPathTextBox.Text = clientKey;
            OpenVpnSharedTlsCryptKeyPathTextBox.Text = taKey;
            FeedbackTextBlock.Text = $"Generated OpenVPN shared bundle at {root}";
        }
        catch (Exception ex)
        {
            FeedbackTextBlock.Text = $"OpenVPN bundle generation failed: {ex.Message}";
        }
    }

    private void ExportOpenVpnBundle_Click(object sender, RoutedEventArgs e)
    {
        var files = new[]
        {
            (Source: OpenVpnSharedCaCertPathTextBox.Text.Trim(), Name: "ca.crt"),
            (Source: OpenVpnSharedClientCertPathTextBox.Text.Trim(), Name: "client.crt"),
            (Source: OpenVpnSharedClientKeyPathTextBox.Text.Trim(), Name: "client.key"),
            (Source: OpenVpnSharedTlsCryptKeyPathTextBox.Text.Trim(), Name: "ta.key")
        };

        var missing = files.Where(x => string.IsNullOrWhiteSpace(x.Source) || !File.Exists(x.Source)).Select(x => x.Name).ToList();
        if (missing.Count > 0)
        {
            FeedbackTextBlock.Text = $"OpenVPN bundle export failed: missing selected file(s): {string.Join(", ", missing)}";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Select destination folder for OpenVPN bundle export",
            FileName = "select-folder",
            Filter = "Folder|*.folder",
            AddExtension = false,
            CheckFileExists = false,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }
        var destinationDir = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(destinationDir))
        {
            FeedbackTextBlock.Text = "OpenVPN bundle export failed: invalid destination folder.";
            return;
        }
        Directory.CreateDirectory(destinationDir);

        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(destinationDir, file.Name);
                File.Copy(file.Source, target, overwrite: true);
            }
            FeedbackTextBlock.Text = $"OpenVPN bundle exported to {destinationDir}";
        }
        catch (Exception ex)
        {
            FeedbackTextBlock.Text = $"OpenVPN bundle export failed: {ex.Message}";
        }
    }

    private void SetKeyPassphrase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SecretInputDialog(
            "Key Passphrase",
            "Enter the SSH private key passphrase used for tunnel authentication.",
            _tunnelKeyPassphrase)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _tunnelKeyPassphrase = dialog.SecretValue;
            RefreshKeyPassphraseState();
        }
    }

    private void ClearKeyPassphrase_Click(object sender, RoutedEventArgs e)
    {
        _tunnelKeyPassphrase = string.Empty;
        RefreshKeyPassphraseState();
    }

    private async void TestTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (!TryUpdateRelayFromControls())
        {
            return;
        }

        if (TestTunnelRequested is null)
        {
            SetActionFeedback("Connection test handler is unavailable.");
            ShowToast("Connection test handler is unavailable.", isError: true);
            return;
        }

        try
        {
            SetActionFeedback("Testing connection...");
            var result = await TestTunnelRequested(Relay);
            SetActionFeedback(result.Message);
            ShowToast(result.Message, isError: !result.Success);
        }
        catch (Exception ex)
        {
            var message = $"Connection test failed: {ex.Message}";
            SetActionFeedback(message);
            ShowToast(message, isError: true);
        }
    }

    private void OpenOmniPanel_Click(object sender, RoutedEventArgs e)
    {
        if (!TryUpdateRelayFromControls())
        {
            return;
        }

        var url = BuildOmniPanelUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            FeedbackTextBlock.Text = "OmniPanel URL is not configured. Set domain or host first.";
            return;
        }
        Relay.OmniPanel.PublicUrl = url;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            FeedbackTextBlock.Text = $"Opened OmniPanel: {url}";
        }
        catch (Exception ex)
        {
            FeedbackTextBlock.Text = $"Failed opening OmniPanel URL: {ex.Message}";
        }
    }

    private async void RefreshStats_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusFromSourceAsync();
        OperationFeedbackText.Text = "Status refreshed.";
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !ReferenceEquals(e.Source, Tabs))
        {
            return;
        }

        if (_isRelaySaved &&
            Tabs.SelectedItem is TabItem tab &&
            string.Equals(tab.Header?.ToString(), "Policies", StringComparison.OrdinalIgnoreCase))
        {
            await LoadPolicyListsAsync();
        }

        if (_isRelaySaved)
        {
            await RefreshStatusFromSourceAsync();
            _statusRefreshTimer.Start();
        }
    }

    private async Task RefreshStatusFromSourceAsync()
    {
        if (!_isRelaySaved || RefreshRelayStatusRequested is null || string.IsNullOrWhiteSpace(Relay.Id))
        {
            LoadStatus();
            LoadOperationCenterSummary();
            return;
        }

        try
        {
            var latest = await RefreshRelayStatusRequested(Relay.Id);
            if (latest is not null)
            {
                _status = latest;
            }
        }
        catch
        {
        }

        LoadStatus();
        LoadOperationCenterSummary();
    }

    private async void TestRuntimeTunnel_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("test_tunnel");
    private async void BootstrapCheck_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("bootstrap_check");
    private async void RefreshGateway_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("refresh");
    private async void InstallGateway_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("install");
    private async void StartGateway_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("start");
    private async void StopGateway_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("stop");
    private async void UninstallGateway_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("uninstall");
    private async void HealthCheckGateway_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("health_check");
    private async void ApplyDns_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("apply_dns");
    private async void CheckDns_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("check_dns");
    private async void RepairDns_Click(object sender, RoutedEventArgs e) => await RunGatewayOperationAsync("repair_dns");

    private async void ClearCachedSudo_Click(object sender, RoutedEventArgs e)
    {
        if (ClearCachedSudoRequested is null)
        {
            OperationFeedbackText.Text = "No sudo cache handler is available.";
            return;
        }

        var result = await ClearCachedSudoRequested();
        OperationFeedbackText.Text = result.Message;
        if (result.Success)
        {
            OperationSudoCachedText.Text = "False";
        }
    }

    private async Task RunGatewayOperationAsync(string operation)
    {
        if (!TryUpdateRelayFromControls())
        {
            return;
        }

        if (!_isRelaySaved || GatewayOperationRequested is null)
        {
            OperationFeedbackText.Text = "Apply this Relay first.";
            return;
        }

        try
        {
            OperationFeedbackText.Text = "Running gateway operation...";
            var result = await GatewayOperationRequested(Relay, operation);
            OperationFeedbackText.Text = result.Message;
            ApplyGatewayOperationSummary(operation, result);
        }
        catch (Exception ex)
        {
            OperationFeedbackText.Text = $"Gateway operation failed: {ex.Message}";
            ApplyGatewayOperationSummary(operation, new OperationResult(false, ex.Message));
        }
    }

    private void SetActionFeedback(string message)
    {
        OperationFeedbackText.Text = message;
        FeedbackTextBlock.Text = message;
    }

    private void ShowToast(string message, bool isError)
    {
        ToastTextBlock.Text = message;
        ToastBorder.Background = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(153, 27, 27))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 101, 52));
        ToastBorder.BorderBrush = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(127, 29, 29))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 128, 61));
        ToastBorder.Visibility = Visibility.Visible;

        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        ToastBorder.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void LoadOperationCenterSummary()
    {
        OperationGatewayServiceStateText.Text = _status?.TunnelState ?? "Unknown";
        OperationBootstrapStateText.Text = "Not checked";
        OperationHealthSummaryText.Text = _status?.HealthState ?? "Not checked";
        OperationSudoCachedText.Text = "False";
        OperationDnsSummaryText.Text = "Not checked";
    }

    private void ApplyGatewayOperationSummary(string operation, OperationResult result)
    {
        var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
        var successText = result.Success ? "OK" : "Failed";

        // Read-only operations can run without sudo.
        if (normalized is not ("clear_cached_sudo" or "health_check" or "refresh"))
        {
            OperationSudoCachedText.Text = "True";
        }

        switch (normalized)
        {
            case "test_tunnel":
            case "bootstrap_check":
                OperationBootstrapStateText.Text = result.Success ? "Passed" : $"Failed ({result.Message})";
                break;
            case "refresh":
                OperationGatewayServiceStateText.Text = result.Message;
                OperationHealthSummaryText.Text = result.Success ? "Healthy" : $"Unhealthy ({result.Message})";
                break;
            case "health_check":
                OperationHealthSummaryText.Text = result.Success ? "Healthy" : $"Unhealthy ({result.Message})";
                break;
            case "apply_dns":
            case "check_dns":
            case "repair_dns":
                OperationDnsSummaryText.Text = $"{successText}: {result.Message}";
                break;
            case "install":
            case "start":
            case "stop":
            case "uninstall":
                OperationGatewayServiceStateText.Text = result.Message;
                break;
        }
    }

    private async Task LoadPolicyListsAsync()
    {
        if (!_isRelaySaved || LoadPolicyListsRequested is null)
        {
            PolicyFeedbackText.Text = "Apply this Relay first.";
            return;
        }

        try
        {
            PolicyFeedbackText.Text = "Loading policy lists...";
            var result = await LoadPolicyListsRequested(Relay.Id);
            PolicyFeedbackText.Text = result.Message;
            if (!result.Success)
            {
                return;
            }

            _policyLists.Clear();
            foreach (var item in result.Lists.OrderBy(x => x.Priority))
            {
                _policyLists.Add(item);
            }

            PolicyListsSummaryText.Text = $"Lists={result.TotalListCount}, Entries={result.TotalEntryCount}, Revision={result.Revision}";
            if (_policyLists.Count == 0)
            {
                _selectedPolicyListId = null;
                PolicyListDetailSummaryText.Text = "Loaded 0 policy list(s).";
                return;
            }

            var selected = _policyLists.FirstOrDefault(x => string.Equals(x.ListId, _selectedPolicyListId, StringComparison.Ordinal))
                ?? _policyLists[0];
            PolicyListsGrid.SelectedItem = selected;
            _selectedPolicyListId = selected.ListId;
            await LoadSelectedPolicyListAsync(selected.ListId);
        }
        catch (Exception ex)
        {
            PolicyFeedbackText.Text = $"Policy load failed: {ex.Message}";
        }
    }

    private async void PolicyListsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PolicyListsGrid.SelectedItem is not RelayPolicyListItemResult item)
        {
            return;
        }

        _selectedPolicyListId = item.ListId;
        await LoadSelectedPolicyListAsync(item.ListId);
    }

    private async Task LoadSelectedPolicyListAsync(string listId)
    {
        if (LoadPolicyListRequested is null)
        {
            return;
        }

        var details = await LoadPolicyListRequested(Relay.Id, listId);
        if (!details.Success)
        {
            PolicyFeedbackText.Text = details.Message;
            return;
        }

        PolicyListDetailSummaryText.Text = $"Priority={details.Priority}, Entries={details.Count}, Revision={details.Revision}, Updated={details.UpdatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
    }

    private async void AddPolicyList_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRelaySaved || CreatePolicyListRequested is null)
        {
            PolicyFeedbackText.Text = "Apply this Relay first.";
            return;
        }

        var createRequest = ShowPolicyListEditorDialog("Create Policy List", string.Empty, PolicyListTypes.Whitelist, string.Empty);
        if (!createRequest.Accepted)
        {
            return;
        }

        await SavePolicyListFromDialogAsync(null, createRequest);
    }

    private async void EditPolicyList_Click(object sender, RoutedEventArgs e)
    {
        await OpenPolicyListEditorAsync();
    }

    private async void PolicyListsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        await OpenPolicyListEditorAsync();
    }

    private async Task OpenPolicyListEditorAsync()
    {
        if (LoadPolicyListRequested is null || PolicyListsGrid.SelectedItem is not RelayPolicyListItemResult selected)
        {
            PolicyFeedbackText.Text = "Select a list first.";
            return;
        }

        var details = await LoadPolicyListRequested(Relay.Id, selected.ListId);
        if (!details.Success)
        {
            PolicyFeedbackText.Text = details.Message;
            return;
        }

        var editor = ShowPolicyListEditorDialog(
            "Edit Policy List",
            details.Label,
            PolicyListTypes.Normalize(details.ListType),
            string.Join(Environment.NewLine, details.Entries));
        if (!editor.Accepted)
        {
            return;
        }

        await SavePolicyListFromDialogAsync(selected.ListId, editor);
    }

    private async Task SavePolicyListFromDialogAsync(string? existingListId, PolicyListEditorResult editor)
    {
        if (ReplacePolicyEntriesRequested is null)
        {
            PolicyFeedbackText.Text = "Policy entry update handler is unavailable.";
            return;
        }

        string listId;
        if (string.IsNullOrWhiteSpace(existingListId))
        {
            if (CreatePolicyListRequested is null)
            {
                PolicyFeedbackText.Text = "Policy create handler is unavailable.";
                return;
            }

            var create = await CreatePolicyListRequested(Relay.Id, editor.Label, editor.ListType, null);
            if (!create.Success || string.IsNullOrWhiteSpace(create.ListId))
            {
                PolicyFeedbackText.Text = create.Message;
                return;
            }

            listId = create.ListId;
        }
        else
        {
            if (UpdatePolicyListMetaRequested is null)
            {
                PolicyFeedbackText.Text = "Policy meta update handler is unavailable.";
                return;
            }

            var update = await UpdatePolicyListMetaRequested(Relay.Id, existingListId, editor.Label, editor.ListType);
            if (!update.Success || string.IsNullOrWhiteSpace(update.ListId))
            {
                PolicyFeedbackText.Text = update.Message;
                return;
            }

            listId = update.ListId;
        }

        var replace = await ReplacePolicyEntriesRequested(Relay.Id, listId, editor.Entries);
        PolicyFeedbackText.Text = replace.Message;
        if (!replace.Success)
        {
            return;
        }

        _selectedPolicyListId = listId;
        await LoadPolicyListsAsync();
    }

    private async void DeletePolicyList_Click(object sender, RoutedEventArgs e)
    {
        if (DeletePolicyListRequested is null || string.IsNullOrWhiteSpace(_selectedPolicyListId))
        {
            PolicyFeedbackText.Text = "Select a list first.";
            return;
        }

        var selected = PolicyListsGrid.SelectedItem as RelayPolicyListItemResult;
        var confirmation = MessageBox.Show(
            $"Delete policy list '{selected?.Label ?? _selectedPolicyListId}'?",
            "Delete Policy List",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await DeletePolicyListRequested(Relay.Id, _selectedPolicyListId);
        PolicyFeedbackText.Text = result.Message;
        if (!result.Success)
        {
            return;
        }

        _selectedPolicyListId = null;
        await LoadPolicyListsAsync();
    }

    private async void MovePolicyListUp_Click(object sender, RoutedEventArgs e)
    {
        await ReorderSelectedListAsync(-1);
    }

    private async void MovePolicyListDown_Click(object sender, RoutedEventArgs e)
    {
        await ReorderSelectedListAsync(1);
    }

    private async Task ReorderSelectedListAsync(int delta)
    {
        if (ReorderPolicyListsRequested is null || PolicyListsGrid.SelectedItem is not RelayPolicyListItemResult selected)
        {
            PolicyFeedbackText.Text = "Select a list first.";
            return;
        }

        var currentIndex = _policyLists.ToList().FindIndex(x => string.Equals(x.ListId, selected.ListId, StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            return;
        }

        var targetIndex = currentIndex + delta;
        if (targetIndex < 0 || targetIndex >= _policyLists.Count)
        {
            return;
        }

        var ordered = _policyLists.Select(x => x.ListId).ToList();
        (ordered[currentIndex], ordered[targetIndex]) = (ordered[targetIndex], ordered[currentIndex]);
        var result = await ReorderPolicyListsRequested(Relay.Id, ordered);
        PolicyFeedbackText.Text = result.Message;
        if (!result.Success)
        {
            return;
        }

        _selectedPolicyListId = selected.ListId;
        await LoadPolicyListsAsync();
    }

    private void RefreshRelayScopedTabs()
    {
        var scopedVisibility = _isRelaySaved ? Visibility.Visible : Visibility.Collapsed;
        var lockedVisibility = _isRelaySaved ? Visibility.Collapsed : Visibility.Visible;
        OperationContentPanel.Visibility = scopedVisibility;
        OperationLockedPanel.Visibility = lockedVisibility;
        PoliciesContentPanel.Visibility = scopedVisibility;
        PoliciesLockedPanel.Visibility = lockedVisibility;

        if (_isRelaySaved &&
            Tabs.SelectedItem is TabItem tab &&
            string.Equals(tab.Header?.ToString(), "Policies", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadPolicyListsAsync();
        }

        if (_isRelaySaved)
        {
            _statusRefreshTimer.Start();
            _ = RefreshStatusFromSourceAsync();
        }
        else
        {
            _statusRefreshTimer.Stop();
        }
    }

    private void RefreshAuthMethodVisibility()
    {
        var authMethod = GetSelectedValue(TunnelAuthMethodCombo, TunnelAuthMethods.Password);
        var usesPassword = string.Equals(TunnelAuthMethods.Normalize(authMethod), TunnelAuthMethods.Password, StringComparison.OrdinalIgnoreCase);
        SetGridRowVisibility(HostGrid, 4, !usesPassword);
        SetGridRowVisibility(HostGrid, 5, !usesPassword);
        SetGridRowVisibility(HostGrid, 6, usesPassword);
    }

    private void RefreshKeyPassphraseState()
    {
        TunnelKeyPassphraseStateText.Text = string.IsNullOrWhiteSpace(_tunnelKeyPassphrase) ? "Not set" : "Configured";
    }

    private void RefreshProtocolFieldVisibility()
    {
        var protocol = GetSelectedValue(ProtocolCombo, IsRemote ? GatewayProtocols.VlessTlsSingbox : CoreLocalGatewayProtocols.VlessTcpPlain);
        protocol = IsRemote ? GatewayProtocols.Normalize(protocol) : CoreLocalGatewayProtocols.Normalize(protocol);
        var openVpnSelected = (IsRemote && protocol == GatewayProtocols.OpenVpnTcpSingbox) ||
                              (!IsRemote && protocol == CoreLocalGatewayProtocols.OpenVpnTcp);
        var localOpenVpnSelected = !IsRemote && protocol == CoreLocalGatewayProtocols.OpenVpnTcp;
        var showsTlsProtocol = IsRemote && (
            protocol == GatewayProtocols.VlessTlsSingbox ||
            protocol == GatewayProtocols.TrojanSingbox ||
            protocol == GatewayProtocols.Hysteria2Singbox ||
            protocol == GatewayProtocols.NaiveSingbox);
        var tlsEnabled = ProtocolTlsEnabledCheckBox.IsChecked == true;
        var tlsMode = GetSelectedValue(ProtocolTlsModeCombo, "uploaded");
        var showsTlsFields = showsTlsProtocol && tlsEnabled;
        var showsTlsCertAndKey = showsTlsFields && !string.Equals(tlsMode, "letsencrypt", StringComparison.OrdinalIgnoreCase);
        var showsProxyCreds = IsRemote && (
            protocol == GatewayProtocols.MixedSingbox ||
            protocol == GatewayProtocols.SocksSingbox ||
            protocol == GatewayProtocols.HttpSingbox ||
            protocol == GatewayProtocols.Hysteria2Singbox ||
            protocol == GatewayProtocols.TrojanSingbox ||
            protocol == GatewayProtocols.NaiveSingbox);

        SetGridRowVisibility(ClientProtocolGrid, 1, true);
        SetGridRowVisibility(ClientProtocolGrid, 2, showsTlsProtocol);
        SetGridRowVisibility(ClientProtocolGrid, 3, showsTlsFields);
        SetGridRowVisibility(ClientProtocolGrid, 4, showsTlsFields);
        SetGridRowVisibility(ClientProtocolGrid, 5, showsTlsCertAndKey);
        SetGridRowVisibility(ClientProtocolGrid, 6, showsTlsCertAndKey);
        SetGridRowVisibility(ClientProtocolGrid, 7, showsTlsFields);
        SetGridRowVisibility(ClientProtocolGrid, 8, showsProxyCreds);
        SetGridRowVisibility(ClientProtocolGrid, 9, showsProxyCreds);
        SetGridRowVisibility(ClientProtocolGrid, 10, IsRemote && protocol == GatewayProtocols.VlessTlsSingbox && tlsEnabled);
        SetGridRowVisibility(ClientProtocolGrid, 11, IsRemote && protocol == GatewayProtocols.Hysteria2Singbox);
        SetGridRowVisibility(ClientProtocolGrid, 12, IsRemote && protocol == GatewayProtocols.Hysteria2Singbox);
        SetGridRowVisibility(ClientProtocolGrid, 13, IsRemote && protocol == GatewayProtocols.Hysteria2Singbox);
        SetGridRowVisibility(ClientProtocolGrid, 14, IsRemote && protocol == GatewayProtocols.Hysteria2Singbox);
        SetGridRowVisibility(ClientProtocolGrid, 15, IsRemote && protocol == GatewayProtocols.NaiveSingbox);
        SetGridRowVisibility(ClientProtocolGrid, 16, IsRemote && protocol == GatewayProtocols.NaiveSingbox);
        SetGridRowVisibility(ClientProtocolGrid, 17, IsRemote && protocol == GatewayProtocols.ShadowTlsV3ShadowsocksSingbox);
        SetGridRowVisibility(ClientProtocolGrid, 18, IsRemote && protocol == GatewayProtocols.ShadowTlsV3ShadowsocksSingbox);
        SetGridRowVisibility(ClientProtocolGrid, 19, IsRemote && protocol == GatewayProtocols.ShadowTlsV3ShadowsocksSingbox);
        SetGridRowVisibility(ClientProtocolGrid, 20, openVpnSelected);
        SetGridRowVisibility(ClientProtocolGrid, 21, openVpnSelected);
        SetGridRowVisibility(ClientProtocolGrid, 22, openVpnSelected);
        SetGridRowVisibility(ClientProtocolGrid, 23, openVpnSelected);
        SetGridRowVisibility(ClientProtocolGrid, 24, openVpnSelected);
        SetGridRowVisibility(ClientProtocolGrid, 25, openVpnSelected);
        SetGridRowVisibility(ClientProtocolGrid, 26, localOpenVpnSelected);
        SetGridRowVisibility(ClientProtocolGrid, 27, IsRemote && protocol == GatewayProtocols.IpsecL2tpSingbox);

        var isIpsec = IsRemote && protocol == GatewayProtocols.IpsecL2tpSingbox;
        ProtocolPortTextBox.Visibility = isIpsec ? Visibility.Collapsed : Visibility.Visible;
        FixedProtocolPortsTextBlock.Visibility = isIpsec ? Visibility.Visible : Visibility.Collapsed;
        FixedProtocolPortsTextBlock.Text = isIpsec ? "Uses fixed UDP ports 500, 4500 and 1701." : string.Empty;
    }

    private void RefreshPanelSslFieldVisibility()
    {
        var sslEnabled = PanelUseSslCheckBox.IsChecked == true;
        var sslMode = GetSelectedValue(PanelSslModeCombo, "letsencrypt");
        var uploadedSelected = sslEnabled && string.Equals(sslMode, "uploaded", StringComparison.OrdinalIgnoreCase);
        var letsEncryptSelected = sslEnabled && string.Equals(sslMode, "letsencrypt", StringComparison.OrdinalIgnoreCase);

        PanelSslModeLabel.Visibility = sslEnabled ? Visibility.Visible : Visibility.Collapsed;
        PanelSslModeCombo.Visibility = sslEnabled ? Visibility.Visible : Visibility.Collapsed;
        PanelUploadedCertLabel.Visibility = uploadedSelected ? Visibility.Visible : Visibility.Collapsed;
        PanelUploadedCertRow.Visibility = uploadedSelected ? Visibility.Visible : Visibility.Collapsed;
        PanelUploadedKeyLabel.Visibility = uploadedSelected ? Visibility.Visible : Visibility.Collapsed;
        PanelUploadedKeyRow.Visibility = uploadedSelected ? Visibility.Visible : Visibility.Collapsed;
        PanelLetsEncryptHintText.Visibility = letsEncryptSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetGridRowVisibility(Grid grid, int row, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (UIElement child in grid.Children)
        {
            if (Grid.GetRow(child) == row)
            {
                child.Visibility = visibility;
            }
        }
    }

    private static void SelectOption(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is Option option && string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = option;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private static string GetSelectedValue(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is Option option ? option.Value : fallback;
    }

    private AdapterChoiceModel? ResolveSelectedAdapter(string? adapterId, int fallbackIfIndex)
    {
        var normalizedId = (adapterId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedId))
        {
            var byId = _adapters.FirstOrDefault(x => string.Equals(x.AdapterId, normalizedId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        return _adapters.FirstOrDefault(x => x.IfIndex == fallbackIfIndex);
    }

    private void RefreshAdapterIdentityText()
    {
        var incoming = IncomingAdapterCombo.SelectedItem as AdapterChoiceModel;
        var outgoing = OutgoingAdapterCombo.SelectedItem as AdapterChoiceModel;
        IncomingAdapterMacText.Text = string.IsNullOrWhiteSpace(incoming?.MacAddress) ? "Unavailable" : incoming.MacAddress;
        OutgoingAdapterMacText.Text = string.IsNullOrWhiteSpace(outgoing?.MacAddress) ? "Unavailable" : outgoing.MacAddress;
    }

    private static int ParsePort(string value, int fallback)
    {
        return int.TryParse(value, out var port) && port > 0 && port <= 65535 ? port : fallback;
    }

    private static int ParsePositiveInt(string value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static string GenerateFrpToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

    private void CopyRelayIdSummary_Click(object sender, RoutedEventArgs e)
    {
        var relayId = RelayIdSummaryText.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(relayId) || string.Equals(relayId, "(new relay)", StringComparison.OrdinalIgnoreCase))
        {
            FeedbackTextBlock.Text = "Relay ID is not available yet.";
            return;
        }

        try
        {
            Clipboard.SetText(relayId);
            FeedbackTextBlock.Text = "Relay ID copied to clipboard.";
        }
        catch (Exception ex)
        {
            FeedbackTextBlock.Text = $"Failed to copy Relay ID: {ex.Message}";
        }
    }

    private static bool IsValidIpv4Cidr(string? cidr)
    {
        var value = (cidr ?? string.Empty).Trim();
        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 32)
        {
            return false;
        }

        return true;
    }

    private void BrowseFileInto(TextBox target, string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            target.Text = dialog.FileName;
        }
    }

    private static void GenerateOpenVpnSharedBundle(
        string root,
        string caCertPath,
        string clientCertPath,
        string clientKeyPath,
        string taKeyPath)
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest(
            "CN=OmniRelay-Shared-CA",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        using var clientKey = RSA.Create(2048);
        var clientReq = new CertificateRequest(
            "CN=OmniRelay-Shared-Client",
            clientKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        clientReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        clientReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(clientReq.PublicKey, false));
        clientReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        var eku = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"), // TLS Web Server Auth
            new("1.3.6.1.5.5.7.3.2")  // TLS Web Client Auth
        };
        clientReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        clientReq.CertificateExtensions.Add(new X509Extension("2.5.29.17", BuildClientSubjectAltNameDer("OmniRelay-Shared-Client"), false));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var clientCert = clientReq.Create(caCert, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10), serial);

        File.WriteAllText(caCertPath, ToPem("CERTIFICATE", caCert.Export(X509ContentType.Cert)));
        File.WriteAllText(clientCertPath, ToPem("CERTIFICATE", clientCert.Export(X509ContentType.Cert)));
        File.WriteAllText(clientKeyPath, ToPem("PRIVATE KEY", clientKey.ExportPkcs8PrivateKey()));
        File.WriteAllText(taKeyPath, BuildOpenVpnStaticKeyFile());
    }

    private static byte[] BuildClientSubjectAltNameDer(string dnsName)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.WriteCharacterString(
            UniversalTagNumber.IA5String,
            dnsName,
            new Asn1Tag(TagClass.ContextSpecific, 2));
        writer.PopSequence();
        return writer.Encode();
    }

    private static string ToPem(string label, byte[] der)
    {
        var builder = new StringBuilder();
        builder.Append("-----BEGIN ").Append(label).AppendLine("-----");
        builder.AppendLine(Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks));
        builder.Append("-----END ").Append(label).AppendLine("-----");
        return builder.ToString();
    }

    private static string BuildOpenVpnStaticKeyFile()
    {
        var key = new byte[256];
        RandomNumberGenerator.Fill(key);
        var sb = new StringBuilder();
        sb.AppendLine("#");
        sb.AppendLine("# 2048 bit OpenVPN static key");
        sb.AppendLine("#");
        sb.AppendLine("-----BEGIN OpenVPN Static key V1-----");
        for (var i = 0; i < key.Length; i += 16)
        {
            var chunk = Convert.ToHexString(key, i, Math.Min(16, key.Length - i)).ToLowerInvariant();
            sb.AppendLine(chunk);
        }
        sb.AppendLine("-----END OpenVPN Static key V1-----");
        return sb.ToString();
    }

    private void ApplyTlsAlpnSelection(string? csv)
    {
        ProtocolTlsAlpnListBox.SelectedItems.Clear();
        var items = (csv ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (items.Count == 0)
        {
            items.Add("h2");
            items.Add("http/1.1");
        }

        foreach (var item in ProtocolTlsAlpnListBox.Items)
        {
            if (item is string value && items.Contains(value))
            {
                ProtocolTlsAlpnListBox.SelectedItems.Add(item);
            }
        }
    }

    private string GetSelectedTlsAlpnCsv()
    {
        var selected = ProtocolTlsAlpnListBox.SelectedItems
            .OfType<string>()
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0)
        {
            selected.Add("h2");
            selected.Add("http/1.1");
        }

        return string.Join(",", selected);
    }

    private static string NormalizePanelSslMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, "uploaded", StringComparison.OrdinalIgnoreCase))
        {
            return "uploaded";
        }

        return "letsencrypt";
    }

    private string BuildOmniPanelUrl()
    {
        var domain = (Relay.OmniPanel.Domain ?? string.Empty).Trim();
        var host = !string.IsNullOrWhiteSpace(domain)
            ? domain
            : IsRemote
                ? (Relay.RemoteGateway.TunnelHost ?? string.Empty).Trim()
                : (Relay.LocalGateway.RemoteAddress ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            if (NetworkAdapterCatalog.TryGetPrimaryIpv4(Relay.IncomingAdapterId, Relay.IncomingAdapterIfIndex, out var incomingIp, out _) &&
                incomingIp is not null)
            {
                host = incomingIp.ToString();
            }
            else if (NetworkAdapterCatalog.TryGetPrimaryIpv4(Relay.OutgoingAdapterId, Relay.OutgoingAdapterIfIndex, out var outgoingIp, out _) &&
                     outgoingIp is not null)
            {
                host = outgoingIp.ToString();
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var port = Relay.OmniPanel.Port is > 0 and <= 65535 ? Relay.OmniPanel.Port : 2054;
        var https = Relay.OmniPanel.UseSsl || Relay.OmniPanel.DomainOnly;
        var scheme = https ? "https" : "http";
        return $"{scheme}://{host}:{port}/panel";
    }

    private static string FormatBool(bool? value)
    {
        return value.HasValue ? (value.Value ? "Yes" : "No") : "Unavailable";
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "Unavailable";
    }

    private static string GetNextCamouflage(string? current)
    {
        var catalog = GatewayCamouflageCatalog.All;
        var currentValue = (current ?? string.Empty).Trim();
        var currentIndex = -1;
        for (var i = 0; i < catalog.Count; i++)
        {
            if (string.Equals(catalog[i], currentValue, StringComparison.OrdinalIgnoreCase))
            {
                currentIndex = i;
                break;
            }
        }

        return catalog[(currentIndex + 1) % catalog.Count];
    }

    private static IReadOnlyList<string> ExtractPolicyEntries(string? text, out IReadOnlyList<string> errors)
    {
        var entries = new List<string>();
        var problems = new List<string>();
        var lines = (text ?? string.Empty).Split(["\r\n", "\n"], StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var value = (lines[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#'))
            {
                continue;
            }

            var commentIndex = value.IndexOf('#');
            if (commentIndex >= 0)
            {
                value = value[..commentIndex].Trim();
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (NetworkRule.TryParse(value, out _, out var error))
            {
                entries.Add(value);
            }
            else
            {
                problems.Add($"Line {i + 1}: {error ?? "invalid policy entry"}");
            }
        }

        errors = problems;
        return entries;
    }

    private static ImportParseResult ParseImportFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".csv" ? ParseCsvImport(path) : ParseTextImport(path);
    }

    private static ImportParseResult ParseTextImport(string path)
    {
        var entries = new List<string>();
        var invalid = 0;
        foreach (var line in File.ReadLines(path))
        {
            var token = StripPolicyComment(line);
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (NetworkRule.TryParse(token, out _, out _))
            {
                entries.Add(token);
            }
            else
            {
                invalid++;
            }
        }

        return new ImportParseResult(entries, invalid);
    }

    private static ImportParseResult ParseCsvImport(string path)
    {
        var entries = new List<string>();
        var invalid = 0;
        foreach (var rawLine in File.ReadLines(path))
        {
            var found = false;
            foreach (var column in ParseCsvLine(rawLine))
            {
                var token = StripPolicyComment(column);
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (NetworkRule.TryParse(token, out _, out _))
                {
                    entries.Add(token);
                    found = true;
                    break;
                }
            }

            if (!found && !string.IsNullOrWhiteSpace(rawLine))
            {
                invalid++;
            }
        }

        return new ImportParseResult(entries, invalid);
    }

    private static string StripPolicyComment(string? value)
    {
        var token = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token) || token.StartsWith('#'))
        {
            return string.Empty;
        }

        var commentIndex = token.IndexOf('#');
        return commentIndex >= 0 ? token[..commentIndex].Trim() : token;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        values.Add(sb.ToString());
        return values;
    }

    private PolicyListEditorResult ShowPolicyListEditorDialog(string title, string label, string listType, string entriesText)
    {
        var primaryButtonStyle = TryFindResource("PrimaryButtonStyle") as Style;
        var secondaryButtonStyle = TryFindResource("SecondaryButtonStyle") as Style;

        var window = new Window
        {
            Title = title,
            Width = 760,
            Height = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.SingleBorderWindow,
            Background = TryFindResource("Brush.Background") as System.Windows.Media.Brush
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // label/type row
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // entries label
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // entries box
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // helper actions
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // feedback
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // save/cancel
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });

        var labelText = new TextBlock { Text = "Label", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8) };
        Grid.SetRow(labelText, 0);
        Grid.SetColumn(labelText, 0);
        root.Children.Add(labelText);

        var labelBox = new TextBox
        {
            Text = label ?? string.Empty,
            Margin = new Thickness(0, 0, 8, 8),
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 34
        };
        Grid.SetRow(labelBox, 0);
        Grid.SetColumn(labelBox, 1);
        Grid.SetColumnSpan(labelBox, 2);
        root.Children.Add(labelBox);

        var typeText = new TextBlock { Text = "Type", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8) };
        Grid.SetRow(typeText, 0);
        Grid.SetColumn(typeText, 3);
        root.Children.Add(typeText);

        var typeCombo = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 8),
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 34,
            ItemsSource = new List<Option>
            {
                new Option(PolicyListTypes.Whitelist, "Whitelist"),
                new Option(PolicyListTypes.Blacklist, "Blacklist")
            }
        };
        SelectOption(typeCombo, PolicyListTypes.Normalize(listType));
        Grid.SetRow(typeCombo, 0);
        Grid.SetColumn(typeCombo, 4);
        root.Children.Add(typeCombo);

        var entriesLabel = new TextBlock
        {
            Text = "Entries (one IP/CIDR per line)",
            Margin = new Thickness(0, 0, 0, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(entriesLabel, 1);
        Grid.SetColumn(entriesLabel, 0);
        Grid.SetColumnSpan(entriesLabel, 5);
        root.Children.Add(entriesLabel);

        var entriesBox = new TextBox
        {
            Text = entriesText ?? string.Empty,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 340
        };
        Grid.SetRow(entriesBox, 2);
        Grid.SetColumn(entriesBox, 0);
        Grid.SetColumnSpan(entriesBox, 5);
        root.Children.Add(entriesBox);

        var helperActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        var importButton = new Button { Content = "Import TXT/CSV", Width = 130, Margin = new Thickness(0, 0, 8, 0) };
        var validateButton = new Button { Content = "Validate", Width = 90 };
        if (secondaryButtonStyle is not null)
        {
            importButton.Style = secondaryButtonStyle;
            validateButton.Style = secondaryButtonStyle;
        }
        helperActions.Children.Add(importButton);
        helperActions.Children.Add(validateButton);
        Grid.SetRow(helperActions, 3);
        Grid.SetColumn(helperActions, 0);
        Grid.SetColumnSpan(helperActions, 5);
        root.Children.Add(helperActions);

        var feedbackText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindResource("Brush.TextMuted") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(feedbackText, 4);
        Grid.SetColumn(feedbackText, 0);
        Grid.SetColumnSpan(feedbackText, 5);
        root.Children.Add(feedbackText);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancelButton = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var okButton = new Button { Content = "Save", Width = 90 };
        if (secondaryButtonStyle is not null)
        {
            cancelButton.Style = secondaryButtonStyle;
        }
        if (primaryButtonStyle is not null)
        {
            okButton.Style = primaryButtonStyle;
        }
        actions.Children.Add(cancelButton);
        actions.Children.Add(okButton);
        Grid.SetRow(actions, 5);
        Grid.SetColumn(actions, 0);
        Grid.SetColumnSpan(actions, 5);
        root.Children.Add(actions);

        var accepted = false;
        var parsedEntries = Array.Empty<string>();

        importButton.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import policy entries",
                Filter = "Text/CSV files|*.txt;*.csv|Text files|*.txt|CSV files|*.csv|All files|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(window) != true)
            {
                return;
            }

            var parsed = ParseImportFile(dialog.FileName);
            if (parsed.Entries.Count == 0)
            {
                feedbackText.Text = "No valid policy entries found in import file.";
                return;
            }

            entriesBox.Text = string.Join(Environment.NewLine, parsed.Entries);
            feedbackText.Text = $"Imported {parsed.Entries.Count} entries. Invalid rows={parsed.InvalidCount}.";
        };

        validateButton.Click += (_, _) =>
        {
            parsedEntries = [.. ExtractPolicyEntries(entriesBox.Text, out var errors)];
            if (errors.Count > 0)
            {
                feedbackText.Text = string.Join(Environment.NewLine, errors);
                return;
            }

            feedbackText.Text = parsedEntries.Length == 0
                ? "Validation completed: no entries found."
                : $"Validation completed: {parsedEntries.Length} valid entr{(parsedEntries.Length == 1 ? "y" : "ies")}.";
        };

        cancelButton.Click += (_, _) => window.Close();
        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(labelBox.Text))
            {
                MessageBox.Show(window, "Label is required.", "Policy List", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            parsedEntries = [.. ExtractPolicyEntries(entriesBox.Text, out var errors)];
            if (errors.Count > 0)
            {
                feedbackText.Text = string.Join(Environment.NewLine, errors);
                return;
            }

            accepted = true;
            window.Close();
        };

        window.Content = root;
        window.ShowDialog();
        if (!accepted)
        {
            return new PolicyListEditorResult(false, string.Empty, PolicyListTypes.Whitelist, []);
        }

        return new PolicyListEditorResult(
            true,
            labelBox.Text.Trim(),
            GetSelectedValue(typeCombo, PolicyListTypes.Whitelist),
            parsedEntries);
    }

    private sealed record Option(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record PolicyListEditorResult(bool Accepted, string Label, string ListType, IReadOnlyList<string> Entries);

    private sealed record ImportParseResult(IReadOnlyList<string> Entries, int InvalidCount);

}



