using CommunityToolkit.Mvvm.ComponentModel;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Status;
using OmniRelay.UI.Models;
using System.Collections.ObjectModel;

namespace OmniRelay.UI.Services;

public partial class GatewayStateStore : ObservableObject
{
    public GatewayStateStore()
    {
        shadowTlsCamouflageServer = GatewayCamouflageCatalog.GetRandom();
    }

    public ObservableCollection<AdapterChoiceModel> Adapters { get; } = [];
    public ObservableCollection<RelayConfig> Relays { get; } = [];

    [ObservableProperty]
    private AdapterChoiceModel? vpsAdapter;

    [ObservableProperty]
    private AdapterChoiceModel? outgoingAdapter;

    [ObservableProperty]
    private string proxyPortText = "24080";

    [ObservableProperty]
    private string bootstrapSocksLocalPortText = "24081";

    [ObservableProperty]
    private string bootstrapSocksRemotePortText = "16080";

    [ObservableProperty]
    private string bootstrapMode = GatewayBootstrapModes.Tunnel;

    [ObservableProperty]
    private string tunnelHost = "vps.example.com";

    [ObservableProperty]
    private string tunnelSshPortText = "22";

    [ObservableProperty]
    private string tunnelRemotePortText = "15000";

    [ObservableProperty]
    private string selectedGatewayProtocol = GatewayProtocols.VlessTlsSingbox;

    [ObservableProperty]
    private string gatewayType = GatewayTypes.Remote;

    [ObservableProperty]
    private string gatewayPublicPortText = "443";

    [ObservableProperty]
    private string gatewayPanelPortText = "2054";

    [ObservableProperty]
    private string gatewayPanelConfiguredUser = string.Empty;

    [ObservableProperty]
    private string gatewayPanelConfiguredPassword = string.Empty;

    [ObservableProperty]
    private string gatewayPanelDomain = string.Empty;

    [ObservableProperty]
    private bool gatewayPanelDomainOnly;

    [ObservableProperty]
    private bool gatewayPanelUseSsl;

    [ObservableProperty]
    private string gatewayPanelSslMode = "letsencrypt";

    [ObservableProperty]
    private string gatewayPanelUploadedCertPath = string.Empty;

    [ObservableProperty]
    private string gatewayPanelUploadedKeyPath = string.Empty;

    [ObservableProperty]
    private string gatewayBackendPortText = "15000";

    [ObservableProperty]
    private bool gatewayProtocolTlsEnabled;

    [ObservableProperty]
    private string gatewayProtocolTlsServerName = string.Empty;

    [ObservableProperty]
    private string gatewaySni = string.Empty;

    [ObservableProperty]
    private string gatewayTarget = string.Empty;

    [ObservableProperty]
    private string gatewayProtocolCertPath = string.Empty;

    [ObservableProperty]
    private string gatewayProtocolKeyPath = string.Empty;

    [ObservableProperty]
    private string gatewayProtocolTlsMode = "uploaded";

    [ObservableProperty]
    private string gatewayProtocolAlpnCsv = string.Empty;

    [ObservableProperty]
    private string gatewayProxyUsername = "omni";

    [ObservableProperty]
    private string gatewayProxyPassword = string.Empty;

    [ObservableProperty]
    private string vlessTlsFlow = string.Empty;

    [ObservableProperty]
    private string hysteria2UpMbpsText = "100";

    [ObservableProperty]
    private string hysteria2DownMbpsText = "100";

    [ObservableProperty]
    private string hysteria2ObfsPassword = string.Empty;

    [ObservableProperty]
    private bool hysteria2IgnoreClientBandwidth;

    [ObservableProperty]
    private string hysteria2MasqueradeUrl = string.Empty;

    [ObservableProperty]
    private string naiveNetwork = string.Empty;

    [ObservableProperty]
    private string naiveQuicCongestionControl = string.Empty;

    [ObservableProperty]
    private string shadowTlsCamouflageServer = string.Empty;

    [ObservableProperty]
    private bool shadowTlsStrictMode;

    [ObservableProperty]
    private string shadowTlsWildcardSni = string.Empty;

    [ObservableProperty]
    private string openVpnNetwork = "10.29.0.0/24";

    [ObservableProperty]
    private string localGatewayProtocol = LocalGatewayProtocols.VlessTcpPlain;

    [ObservableProperty]
    private string localGatewayPortText = "443";

    [ObservableProperty]
    private string localGatewayBindAddress = "0.0.0.0";

    [ObservableProperty]
    private string localGatewayRemoteAddress = string.Empty;

    [ObservableProperty]
    private string localGatewayRemark = "OmniRelay Local Gateway";

    [ObservableProperty]
    private bool localGatewayRuntimeEnabled = true;

    [ObservableProperty]
    private string gatewayDohEndpointsText = "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query";

    [ObservableProperty]
    private string gatewayPanelUrl = string.Empty;

    [ObservableProperty]
    private string gatewayPanelUsername = string.Empty;

    [ObservableProperty]
    private string gatewayInitialPanelPassword = string.Empty;

    [ObservableProperty]
    private string tunnelUser = "OmniRelay";

    [ObservableProperty]
    private string tunnelAuthMethod = TunnelAuthMethods.Password;

    [ObservableProperty]
    private string tunnelKeyPath = string.Empty;

    [ObservableProperty]
    private string tunnelKeyPassphrase = string.Empty;

    [ObservableProperty]
    private string tunnelPassword = string.Empty;

    [ObservableProperty]
    private string licenseKey = string.Empty;

    [ObservableProperty]
    private string serviceState = "Unknown";

    [ObservableProperty]
    private bool licenseActivated;

    [ObservableProperty]
    private DateTimeOffset? licenseActivatedExpiresAtUtc;

    [ObservableProperty]
    private GatewayStatus? status;

    [ObservableProperty]
    private AppStatus? appStatus;

    [ObservableProperty]
    private string lastAction = "Ready.";

    partial void OnTunnelRemotePortTextChanged(string value)
    {
        GatewayBackendPortText = value;
    }
}
