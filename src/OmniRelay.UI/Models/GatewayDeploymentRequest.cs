using OmniRelay.Core.Configuration;

namespace OmniRelay.UI.Models;

public sealed class GatewayDeploymentRequest
{
    public string RelayId { get; init; } = string.Empty;
    public required ServiceConfig Config { get; init; }
    public string BootstrapMode { get; init; } = GatewayBootstrapModes.Tunnel;
    public string SelectedGatewayProtocol { get; init; } = GatewayProtocols.VlessTlsSingbox;
    public int GatewayPublicPort { get; init; }
    public int GatewayPanelPort { get; init; }
    public string GatewayPanelUser { get; init; } = string.Empty;
    public string GatewayPanelPassword { get; init; } = string.Empty;
    public string GatewayPanelDomain { get; init; } = string.Empty;
    public bool GatewayPanelDomainOnly { get; init; }
    public bool GatewayPanelSslEnabled { get; init; }
    public string GatewayPanelSslMode { get; init; } = "none";
    public string GatewayPanelCertLocalPath { get; init; } = string.Empty;
    public string GatewayPanelKeyLocalPath { get; init; } = string.Empty;
    public string GatewayPanelCertRemotePath { get; init; } = string.Empty;
    public string GatewayPanelKeyRemotePath { get; init; } = string.Empty;
    public bool GatewayProtocolTlsEnabled { get; init; }
    public string GatewayProtocolTlsServerName { get; init; } = string.Empty;
    public string GatewayProtocolCertPath { get; init; } = string.Empty;
    public string GatewayProtocolKeyPath { get; init; } = string.Empty;
    public string GatewayProtocolTlsMode { get; init; } = "uploaded";
    public string GatewayProtocolAlpnCsv { get; init; } = string.Empty;
    public string GatewaySni { get; init; } = string.Empty;
    public string GatewayTarget { get; init; } = string.Empty;
    public string GatewayProxyUsername { get; init; } = string.Empty;
    public string GatewayProxyPassword { get; init; } = string.Empty;
    public string VlessTlsFlow { get; init; } = string.Empty;
    public int Hysteria2UpMbps { get; init; }
    public int Hysteria2DownMbps { get; init; }
    public string Hysteria2ObfsPassword { get; init; } = string.Empty;
    public bool Hysteria2IgnoreClientBandwidth { get; init; }
    public string Hysteria2MasqueradeUrl { get; init; } = string.Empty;
    public string NaiveNetwork { get; init; } = string.Empty;
    public string NaiveQuicCongestionControl { get; init; } = string.Empty;
    public string ShadowTlsCamouflageServer { get; init; } = string.Empty;
    public bool ShadowTlsStrictMode { get; init; }
    public string ShadowTlsWildcardSni { get; init; } = string.Empty;
    public string OpenVpnNetwork { get; init; } = "10.29.0.0/24";
    public string IpsecL2tpNetwork { get; init; } = "10.39.0.0/24";
    public string OpenVpnSharedCaCertLocalPath { get; init; } = string.Empty;
    public string OpenVpnSharedClientCertLocalPath { get; init; } = string.Empty;
    public string OpenVpnSharedClientKeyLocalPath { get; init; } = string.Empty;
    public string OpenVpnSharedTlsCryptKeyLocalPath { get; init; } = string.Empty;
    public string GatewayDohEndpoints { get; init; } = "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query";
}
