namespace EstherLink.UI.Models;

public sealed class GatewayUiStateModel
{
    public int? VpsAdapterIfIndex { get; set; }
    public int? OutgoingAdapterIfIndex { get; set; }

    public string ProxyPortText { get; set; } = "19080";
    public string BootstrapSocksLocalPortText { get; set; } = "19081";
    public string BootstrapSocksRemotePortText { get; set; } = "16080";
    public string TunnelHost { get; set; } = "vps.example.com";
    public string TunnelSshPortText { get; set; } = "22";
    public string TunnelRemotePortText { get; set; } = "15000";
    public string GatewayPublicPortText { get; set; } = "443";
    public string GatewayPanelPortText { get; set; } = "2054";
    public string GatewayBackendPortText { get; set; } = "15000";
    public string GatewayDnsMode { get; set; } = "hybrid";
    public string GatewayDohEndpointsText { get; set; } = "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query";
    public bool GatewayDnsUdpOnly { get; set; } = true;
    public string TunnelUser { get; set; } = "estherlink";
    public string TunnelAuthMethod { get; set; } = "host_key";
    public string TunnelKeyPath { get; set; } = string.Empty;

    public string EncryptedTunnelKeyPassphrase { get; set; } = string.Empty;
    public string EncryptedTunnelPassword { get; set; } = string.Empty;
    public string EncryptedLicenseKey { get; set; } = string.Empty;

    public string WhitelistText { get; set; } = string.Empty;
}
