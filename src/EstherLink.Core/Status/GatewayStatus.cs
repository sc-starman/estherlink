namespace EstherLink.Core.Status;

public sealed class GatewayStatus
{
    public bool ServiceRunning { get; set; }
    public bool ProxyRunning { get; set; }
    public int ProxyListenPort { get; set; }
    public bool TunnelConnected { get; set; }
    public DateTimeOffset? TunnelLastConnectedAtUtc { get; set; }
    public int TunnelReconnectCount { get; set; }
    public string? TunnelLastError { get; set; }
    public bool BootstrapSocksListening { get; set; }
    public bool BootstrapSocksRemoteForwardActive { get; set; }
    public string? BootstrapSocksLastError { get; set; }
    public bool LicenseValid { get; set; }
    public bool LicenseFromCache { get; set; }
    public DateTimeOffset? LicenseCheckedAtUtc { get; set; }
    public DateTimeOffset? LicenseExpiresAtUtc { get; set; }
    public string? WhitelistAdapterIp { get; set; }
    public string? DefaultAdapterIp { get; set; }
    public int WhitelistCount { get; set; }
    public string? LastError { get; set; }
}
