namespace EstherLink.Backend.Data.Entities;

public sealed class LicenseTransferEntity
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public string? FromFingerprintHash { get; set; }
    public string ToFingerprintHash { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string MetaJson { get; set; } = "{}";

    public LicenseEntity License { get; set; } = null!;
}
