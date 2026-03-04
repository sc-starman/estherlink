namespace EstherLink.UI.Models;

public class GatewayServiceStatus
{
    public string SshState { get; set; } = "unknown";
    public string XuiState { get; set; } = "unknown";
    public string Fail2BanState { get; set; } = "unknown";
    public int BackendPort { get; set; }
    public int PublicPort { get; set; }
    public int PanelPort { get; set; }
    public bool BackendListener { get; set; }
    public bool PublicListener { get; set; }
    public bool PanelListener { get; set; }
}
