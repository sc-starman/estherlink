namespace OmniRelay.Backend.Data.Entities;

public sealed class LicenseActivationEntity
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public string FingerprintHash { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool IsBlocked { get; set; }
    public string MetaJson { get; set; } = "{}";

    public LicenseEntity License { get; set; } = null!;
}
