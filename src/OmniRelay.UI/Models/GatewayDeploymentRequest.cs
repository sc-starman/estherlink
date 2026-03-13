using OmniRelay.Core.Configuration;

namespace OmniRelay.UI.Models;

public sealed class GatewayDeploymentRequest
{
    public required ServiceConfig Config { get; init; }
    public string SelectedGatewayProtocol { get; init; } = GatewayProtocols.VlessReality3xui;
    public int GatewayPublicPort { get; init; }
    public int GatewayPanelPort { get; init; }
    public string GatewaySni { get; init; } = string.Empty;
    public string GatewayTarget { get; init; } = string.Empty;
    public string ShadowTlsCamouflageServer { get; init; } = string.Empty;
    public string OpenVpnNetwork { get; init; } = "10.29.0.0/24";
    public string OpenVpnClientDns { get; init; } = string.Empty;
    public string GatewayDnsMode { get; init; } = "hybrid";
    public string GatewayDohEndpoints { get; init; } = "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query";
    public bool GatewayDnsUdpOnly { get; init; } = true;
}
