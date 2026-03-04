namespace EstherLink.Core.Status;

public sealed class GatewayStatus
{
    public bool ServiceRunning { get; set; }
    public bool ProxyRunning { get; set; }
    public int ProxyListenPort { get; set; }
    public bool LicenseValid { get; set; }
    public bool LicenseFromCache { get; set; }
    public DateTimeOffset? LicenseCheckedAtUtc { get; set; }
    public DateTimeOffset? LicenseExpiresAtUtc { get; set; }
    public string? WhitelistAdapterIp { get; set; }
    public string? DefaultAdapterIp { get; set; }
    public int WhitelistCount { get; set; }
    public string WhitelistMode { get; set; } = string.Empty;
    public string? LastError { get; set; }
}
