using OmniRelay.Core.Configuration;

namespace OmniRelay.UI.Models;

public sealed class GatewayUiStateModel
{
    public string GatewayType { get; set; } = GatewayTypes.Remote;

    // Legacy flat fields are retained for backward compatibility with older UI state files.
    public int? VpsAdapterIfIndex { get; set; }
    public int? OutgoingAdapterIfIndex { get; set; }
    public string ProxyPortText { get; set; } = "19080";
    public string BootstrapSocksLocalPortText { get; set; } = "19081";
    public string BootstrapSocksRemotePortText { get; set; } = "16080";
    public string BootstrapMode { get; set; } = GatewayBootstrapModes.Tunnel;
    public string TunnelHost { get; set; } = "vps.example.com";
    public string TunnelSshPortText { get; set; } = "22";
    public string TunnelRemotePortText { get; set; } = "15000";
    public string SelectedGatewayProtocol { get; set; } = GatewayProtocols.VlessReality3xui;
    public string GatewayPublicPortText { get; set; } = "443";
    public string GatewayPanelPortText { get; set; } = "2054";
    public string GatewayPanelConfiguredUser { get; set; } = string.Empty;
    public string EncryptedGatewayPanelConfiguredPassword { get; set; } = string.Empty;
    public string GatewayPanelDomain { get; set; } = string.Empty;
    public bool GatewayPanelDomainOnly { get; set; }
    public bool GatewayPanelUseSsl { get; set; }
    public string GatewayPanelSslMode { get; set; } = "letsencrypt";
    public string GatewayPanelUploadedCertPath { get; set; } = string.Empty;
    public string GatewayPanelUploadedKeyPath { get; set; } = string.Empty;
    public string GatewayBackendPortText { get; set; } = "15000";
    public string GatewaySni { get; set; } = string.Empty;
    public string GatewayTarget { get; set; } = string.Empty;
    public string ShadowTlsCamouflageServer { get; set; } = string.Empty;
    public string OpenVpnNetwork { get; set; } = "10.29.0.0/24";
    public string OpenVpnClientDns { get; set; } = "1.1.1.1,8.8.8.8";
    public string GatewayDnsMode { get; set; } = "hybrid";
    public string GatewayDohEndpointsText { get; set; } = "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query";
    public bool GatewayDnsUdpOnly { get; set; } = true;
    public string GatewayPanelUrl { get; set; } = string.Empty;
    public string GatewayPanelUsername { get; set; } = string.Empty;
    public string GatewayInitialPanelPassword { get; set; } = string.Empty;
    public string TunnelUser { get; set; } = "OmniRelay";
    public string TunnelAuthMethod { get; set; } = "host_key";
    public string TunnelKeyPath { get; set; } = string.Empty;
    public string EncryptedTunnelKeyPassphrase { get; set; } = string.Empty;
    public string EncryptedTunnelPassword { get; set; } = string.Empty;
    public string EncryptedLicenseKey { get; set; } = string.Empty;

    // New split profiles.
    public GatewayRemoteUiProfileModel RemoteProfile { get; set; } = new();
    public GatewayLocalUiProfileModel LocalProfile { get; set; } = new();
}

public sealed class GatewayRemoteUiProfileModel
{
    public int? VpsAdapterIfIndex { get; set; }
    public int? OutgoingAdapterIfIndex { get; set; }
    public string ProxyPortText { get; set; } = "19080";
    public string BootstrapSocksLocalPortText { get; set; } = "19081";
    public string BootstrapSocksRemotePortText { get; set; } = "16080";
    public string BootstrapMode { get; set; } = GatewayBootstrapModes.Tunnel;
    public string TunnelHost { get; set; } = "vps.example.com";
    public string TunnelSshPortText { get; set; } = "22";
    public string TunnelRemotePortText { get; set; } = "15000";
    public string SelectedGatewayProtocol { get; set; } = GatewayProtocols.VlessReality3xui;
    public string GatewayPublicPortText { get; set; } = "443";
    public string GatewayPanelPortText { get; set; } = "2054";
    public string GatewayPanelConfiguredUser { get; set; } = string.Empty;
    public string EncryptedGatewayPanelConfiguredPassword { get; set; } = string.Empty;
    public string GatewayPanelDomain { get; set; } = string.Empty;
    public bool GatewayPanelDomainOnly { get; set; }
    public bool GatewayPanelUseSsl { get; set; }
    public string GatewayPanelSslMode { get; set; } = "letsencrypt";
    public string GatewayBackendPortText { get; set; } = "15000";
    public string GatewaySni { get; set; } = string.Empty;
    public string GatewayTarget { get; set; } = string.Empty;
    public string ShadowTlsCamouflageServer { get; set; } = string.Empty;
    public string OpenVpnNetwork { get; set; } = "10.29.0.0/24";
    public string OpenVpnClientDns { get; set; } = "1.1.1.1,8.8.8.8";
    public string GatewayDnsMode { get; set; } = "hybrid";
    public string GatewayDohEndpointsText { get; set; } = "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query";
    public bool GatewayDnsUdpOnly { get; set; } = true;
    public string TunnelUser { get; set; } = "OmniRelay";
    public string TunnelAuthMethod { get; set; } = "host_key";
    public string TunnelKeyPath { get; set; } = string.Empty;
    public string EncryptedTunnelKeyPassphrase { get; set; } = string.Empty;
    public string EncryptedTunnelPassword { get; set; } = string.Empty;
    public string EncryptedLicenseKey { get; set; } = string.Empty;
}

public sealed class GatewayLocalUiProfileModel
{
    public int? OutgoingAdapterIfIndex { get; set; }
    public string LocalGatewayProtocol { get; set; } = LocalGatewayProtocols.VlessTcpPlain;
    public string LocalGatewayPortText { get; set; } = "443";
    public string LocalGatewayBindAddress { get; set; } = "0.0.0.0";
    public string LocalGatewayRemoteAddress { get; set; } = string.Empty;
    public string LocalGatewayRemark { get; set; } = "OmniRelay Local Gateway";
    public bool LocalGatewayRuntimeEnabled { get; set; } = true;
    public string EncryptedLicenseKey { get; set; } = string.Empty;
}
