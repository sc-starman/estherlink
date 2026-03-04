using CommunityToolkit.Mvvm.ComponentModel;
using EstherLink.Core.Configuration;
using EstherLink.Core.Status;
using EstherLink.UI.Models;
using System.Collections.ObjectModel;

namespace EstherLink.UI.Services;

public partial class GatewayStateStore : ObservableObject
{
    public ObservableCollection<AdapterChoiceModel> Adapters { get; } = [];

    [ObservableProperty]
    private AdapterChoiceModel? vpsAdapter;

    [ObservableProperty]
    private AdapterChoiceModel? outgoingAdapter;

    [ObservableProperty]
    private string proxyPortText = "19080";

    [ObservableProperty]
    private string tunnelHost = "vps.example.com";

    [ObservableProperty]
    private string tunnelSshPortText = "22";

    [ObservableProperty]
    private string tunnelRemotePortText = "15000";

    [ObservableProperty]
    private string gatewayPublicPortText = "443";

    [ObservableProperty]
    private string gatewayPanelPortText = "8443";

    [ObservableProperty]
    private string gatewayBackendPortText = "15000";

    [ObservableProperty]
    private string tunnelUser = "estherlink";

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
    private GatewayStatus? status;

    [ObservableProperty]
    private string lastAction = "Ready.";

    partial void OnTunnelRemotePortTextChanged(string value)
    {
        GatewayBackendPortText = value;
    }
}
