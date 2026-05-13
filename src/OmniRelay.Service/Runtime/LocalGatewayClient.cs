namespace OmniRelay.Service.Runtime;

public sealed class LocalGatewayClient
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Remark { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public double TotalGB { get; set; }
    public long ExpiryTime { get; set; }
    public int SpeedLimitKbps { get; set; }
    public long LastSeenAtUnixMs { get; set; }
    public int ActiveConnections { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
