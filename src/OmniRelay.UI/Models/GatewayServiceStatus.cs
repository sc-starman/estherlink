namespace OmniRelay.UI.Models;

public class GatewayServiceStatus
{
    public string SshState { get; set; } = "unknown";
    public string XuiState { get; set; } = "unknown";
    public string OmniPanelState { get; set; } = "unknown";
    public string NginxState { get; set; } = "unknown";
    public string Fail2BanState { get; set; } = "unknown";
    public int BackendPort { get; set; }
    public int PublicPort { get; set; }
    public int PanelPort { get; set; }
    public int OmniPanelInternalPort { get; set; }
    public int XuiPanelPort { get; set; }
    public bool BackendListener { get; set; }
    public bool PublicListener { get; set; }
    public bool PanelListener { get; set; }
    public bool OmniPanelInternalListener { get; set; }
    public bool DnsConfigPresent { get; set; }
    public bool DnsRuleActive { get; set; }
    public bool DohReachableViaTunnel { get; set; }
    public bool Udp53PathReady { get; set; }
    public bool DnsPathHealthy { get; set; }
    public string InboundId { get; set; } = string.Empty;
    public string DnsMode { get; set; } = "unknown";
    public bool DnsUdpOnly { get; set; }
    public string DohEndpoints { get; set; } = string.Empty;
}
