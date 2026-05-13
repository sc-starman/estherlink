namespace OmniRelay.Core.Configuration;

public sealed class ServiceConfig
{
    public int SchemaVersion { get; set; } = 8;
    public string LicenseKey { get; set; } = string.Empty;
    public List<RelayConfig> Relays { get; set; } = [];

    // Legacy single-relay fields are kept while the old service/UI classes are retired.
    public string GatewayType { get; set; } = GatewayTypes.Remote;
    public int LocalProxyListenPort { get; set; } = 24080;
    public int BootstrapSocksLocalPort { get; set; } = 24081;
    public int BootstrapSocksRemotePort { get; set; } = 16080;
    public bool GatewayOnlineInstallEnabled { get; set; } = true;
    public int WhitelistAdapterIfIndex { get; set; } = -1;
    public int DefaultAdapterIfIndex { get; set; } = -1;
    public string TunnelHost { get; set; } = string.Empty;
    public int TunnelSshPort { get; set; } = 22;
    public int TunnelRemotePort { get; set; } = 15000;
    public string TunnelUser { get; set; } = "OmniRelay";
    public string TunnelAuthMethod { get; set; } = TunnelAuthMethods.Password;
    public string TunnelPrivateKeyPath { get; set; } = string.Empty;
    public string TunnelPrivateKeyPassphrase { get; set; } = string.Empty;
    public string TunnelPassword { get; set; } = string.Empty;
    public LocalGatewayConfig LocalGateway { get; set; } = new();
}
