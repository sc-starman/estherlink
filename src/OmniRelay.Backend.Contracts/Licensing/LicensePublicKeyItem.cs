namespace OmniRelay.Backend.Contracts.Licensing;

public sealed class LicensePublicKeyItem
{
    public string KeyId { get; set; } = string.Empty;
    public string SignatureAlg { get; set; } = "Ed25519";
    public string PublicKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
