namespace OmniRelay.Backend.Contracts.Licensing;

public sealed class LicenseVerifyResponse
{
    public bool Valid { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool TransferRequired { get; set; }
    public string? ActiveDeviceIdHint { get; set; }
    public int TransferLimitPerRollingYear { get; set; }
    public int TransfersUsedInWindow { get; set; }
    public int TransfersRemainingInWindow { get; set; }
    public DateTimeOffset? TransferWindowStartAt { get; set; }
    public string? Plan { get; set; }
    public DateTimeOffset? LicenseExpiresAt { get; set; }
    public DateTimeOffset CacheExpiresAt { get; set; }
    public DateTimeOffset ServerTime { get; set; }
    public string SignatureAlg { get; set; } = "Ed25519";
    public string KeyId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public Dictionary<string, string>? Features { get; set; }
}
