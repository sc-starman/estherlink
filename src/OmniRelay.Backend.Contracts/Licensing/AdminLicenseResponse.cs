namespace OmniRelay.Backend.Contracts.Licensing;

public sealed class AdminLicenseResponse
{
    public Guid Id { get; set; }
    public string LicenseKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public int MaxDevices { get; set; }
    public int ActivationCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
