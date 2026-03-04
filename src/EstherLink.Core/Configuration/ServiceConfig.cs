namespace EstherLink.Core.Configuration;

public sealed class ServiceConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string VpsHost { get; set; } = string.Empty;
    public int VpsPort { get; set; } = 443;
    public int LocalProxyListenPort { get; set; } = 19080;
    public int WhitelistAdapterIfIndex { get; set; } = -1;
    public int DefaultAdapterIfIndex { get; set; } = -1;
    public bool TunnelEnabled { get; set; }
    public string TunnelHost { get; set; } = string.Empty;
    public int TunnelSshPort { get; set; } = 22;
    public int TunnelRemotePort { get; set; } = 15000;
    public string TunnelUser { get; set; } = "estherlink";
    public string TunnelPrivateKeyPath { get; set; } = string.Empty;
    public string LicenseServerUrl { get; set; } = string.Empty;
    public string LicenseKey { get; set; } = string.Empty;
}
