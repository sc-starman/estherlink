namespace OmniRelay.Core.Configuration;

public sealed class LocalGatewayConfig
{
    public string Protocol { get; set; } = LocalGatewayProtocols.VlessTcpPlain;
    public int Port { get; set; } = 443;
    public string BindAddress { get; set; } = "0.0.0.0";
    public string Remark { get; set; } = "OmniRelay Local Gateway";
    public bool RuntimeEnabled { get; set; } = true;
}
