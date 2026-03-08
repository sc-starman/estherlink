namespace OmniRelay.Backend.Contracts.Licensing;

public sealed class AdminCreateLicenseRequest
{
    public string LicenseKey { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public string Plan { get; set; } = "standard";
    public DateTimeOffset? ExpiresAt { get; set; }
    public int MaxDevices { get; set; } = 1;
}
