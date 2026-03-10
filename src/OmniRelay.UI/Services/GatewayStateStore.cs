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
        var pair = GatewayRealityTargetCatalog.GetRandom();
        gatewaySni = pair.Sni;
        gatewayTarget = pair.Target;
    }

    public ObservableCollection<AdapterChoiceModel> Adapters { get; } = [];

    [ObservableProperty]
    private AdapterChoiceModel? vpsAdapter;

    [ObservableProperty]
    private AdapterChoiceModel? outgoingAdapter;

    [ObservableProperty]
    private string proxyPortText = "19080";

    [ObservableProperty]
    private string bootstrapSocksLocalPortText = "19081";

    [ObservableProperty]
    private string bootstrapSocksRemotePortText = "16080";

    [ObservableProperty]
    private string tunnelHost = "vps.example.com";

    [ObservableProperty]
    private string tunnelSshPortText = "22";

    [ObservableProperty]
    private string tunnelRemotePortText = "15000";

    [ObservableProperty]
    private string gatewayPublicPortText = "443";

    [ObservableProperty]
    private string gatewayPanelPortText = "2054";

    [ObservableProperty]
    private string gatewayBackendPortText = "15000";

    [ObservableProperty]
    private string gatewaySni = string.Empty;

    [ObservableProperty]
    private string gatewayTarget = string.Empty;

    [ObservableProperty]
    private string gatewayDnsMode = "hybrid";

    [ObservableProperty]
    private string gatewayDohEndpointsText = "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query";

    [ObservableProperty]
    private bool gatewayDnsUdpOnly = true;

    [ObservableProperty]
    private string gatewayPanelUrl = string.Empty;

    [ObservableProperty]
    private string gatewayPanelUsername = string.Empty;

    [ObservableProperty]
    private string gatewayInitialPanelPassword = string.Empty;

    [ObservableProperty]
    private string tunnelUser = "OmniRelay";

    [ObservableProperty]
    private string tunnelAuthMethod = TunnelAuthMethods.HostKey;

    [ObservableProperty]
    private string tunnelKeyPath = string.Empty;

    [ObservableProperty]
    private string tunnelKeyPassphrase = string.Empty;

    [ObservableProperty]
    private string tunnelPassword = string.Empty;

    [ObservableProperty]
    private string licenseKey = string.Empty;

    [ObservableProperty]
    private string whitelistText = string.Empty;

    [ObservableProperty]
    private string serviceState = "Unknown";

    [ObservableProperty]
    private bool licenseActivated;

    [ObservableProperty]
    private DateTimeOffset? licenseActivatedExpiresAtUtc;

    [ObservableProperty]
    private GatewayStatus? status;

    [ObservableProperty]
    private string lastAction = "Ready.";

    partial void OnTunnelRemotePortTextChanged(string value)
    {
        GatewayBackendPortText = value;
    }
}
