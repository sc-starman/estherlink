namespace EstherLink.Backend.Contracts.Licensing;

public sealed class LicenseVerifyResponse
{
    public bool Valid { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Plan { get; set; }
    public DateTimeOffset? LicenseExpiresAt { get; set; }
    public DateTimeOffset CacheExpiresAt { get; set; }
    public DateTimeOffset ServerTime { get; set; }
    public string Signature { get; set; } = string.Empty;
    public Dictionary<string, string>? Features { get; set; }
}
