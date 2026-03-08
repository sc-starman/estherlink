namespace OmniRelay.UI.Models;

public sealed class GatewayHealthReport : GatewayServiceStatus
{
    public bool Healthy { get; set; }
    public string DnsLastError { get; set; } = string.Empty;
    public string RawJson { get; set; } = string.Empty;
    public DateTimeOffset CheckedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
