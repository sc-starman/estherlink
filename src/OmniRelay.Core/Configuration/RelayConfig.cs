namespace OmniRelay.Core.Configuration;

public sealed class RelayConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Relay";
    public string GatewayType { get; set; } = GatewayTypes.Remote;
    public bool Enabled { get; set; } = true;
    public string IncomingAdapterId { get; set; } = string.Empty;
    public int IncomingAdapterIfIndex { get; set; } = -1;
    public string OutgoingAdapterId { get; set; } = string.Empty;
    public int OutgoingAdapterIfIndex { get; set; } = -1;
    public int DataPlaneLocalPort { get; set; }
    public int BootstrapSocksLocalPort { get; set; }
    public int BootstrapSocksRemotePort { get; set; } = 16080;
    public RelayOmniPanelConfig OmniPanel { get; set; } = new();
    public RemoteGatewayConfig RemoteGateway { get; set; } = new();
    public LocalGatewayConfig LocalGateway { get; set; } = new();
}

public sealed class RelayOmniPanelConfig
{
    public int Port { get; set; } = 2054;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public bool DomainOnly { get; set; }
    public bool UseSsl { get; set; }
    public string SslMode { get; set; } = "letsencrypt";
    public string UploadedCertPath { get; set; } = string.Empty;
    public string UploadedKeyPath { get; set; } = string.Empty;
    public string PublicUrl { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
}

public sealed class RemoteGatewayConfig
{
    public string TunnelHost { get; set; } = string.Empty;
    public int TunnelSshPort { get; set; } = 22;
    public int TunnelRemotePort { get; set; } = 15000;
    public string TunnelUser { get; set; } = "OmniRelay";
    public string TunnelAuthMethod { get; set; } = TunnelAuthMethods.Password;
    public string TunnelPrivateKeyPath { get; set; } = string.Empty;
    public string TunnelPrivateKeyPassphrase { get; set; } = string.Empty;
    public string TunnelPassword { get; set; } = string.Empty;
    public string BootstrapMode { get; set; } = "tunnel";
    public string Protocol { get; set; } = "vless_tls_singbox";
    public int PublicPort { get; set; } = 443;
    public int PanelPort { get; set; } = 2054;
    public string PanelUser { get; set; } = string.Empty;
    public string PanelPassword { get; set; } = string.Empty;
    public string PanelDomain { get; set; } = string.Empty;
    public bool PanelDomainOnly { get; set; }
    public bool PanelUseSsl { get; set; }
    public string PanelSslMode { get; set; } = "letsencrypt";
    public string PanelUploadedCertPath { get; set; } = string.Empty;
    public string PanelUploadedKeyPath { get; set; } = string.Empty;
    public bool ProtocolTlsEnabled { get; set; }
    public string ProtocolTlsServerName { get; set; } = string.Empty;
    public string ProtocolCertPath { get; set; } = string.Empty;
    public string ProtocolKeyPath { get; set; } = string.Empty;
    public string ProtocolTlsMode { get; set; } = "uploaded";
    public string ProtocolAlpnCsv { get; set; } = string.Empty;
    public string ProxyUsername { get; set; } = "omni";
    public string ProxyPassword { get; set; } = string.Empty;
    public string VlessTlsFlow { get; set; } = string.Empty;
    public int Hysteria2UpMbps { get; set; } = 100;
    public int Hysteria2DownMbps { get; set; } = 100;
    public string Hysteria2ObfsPassword { get; set; } = string.Empty;
    public bool Hysteria2IgnoreClientBandwidth { get; set; }
    public string Hysteria2MasqueradeUrl { get; set; } = string.Empty;
    public string NaiveNetwork { get; set; } = string.Empty;
    public string NaiveQuicCongestionControl { get; set; } = string.Empty;
    public string ShadowTlsCamouflageServer { get; set; } = string.Empty;
    public bool ShadowTlsStrictMode { get; set; }
    public string ShadowTlsWildcardSni { get; set; } = string.Empty;
    public string OpenVpnNetwork { get; set; } = "10.29.0.0/24";
    public string IpsecL2tpNetwork { get; set; } = "10.39.0.0/24";
    public string OpenVpnSharedCaCertPath { get; set; } = string.Empty;
    public string OpenVpnSharedClientCertPath { get; set; } = string.Empty;
    public string OpenVpnSharedClientKeyPath { get; set; } = string.Empty;
    public string OpenVpnSharedTlsCryptKeyPath { get; set; } = string.Empty;
    public string DohEndpoints { get; set; } = "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query";
}
