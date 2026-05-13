namespace OmniRelay.Backend.Contracts.Licensing;

public sealed class LicenseCertificate
{
    public string LicenseId { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public string DeviceFingerprintHash { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; }
    public bool IsPerpetual { get; set; }
    public DateTimeOffset? UpdateEntitlementUntil { get; set; }
    public string SignatureAlg { get; set; } = "Ed25519";
    public string KeyId { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}
