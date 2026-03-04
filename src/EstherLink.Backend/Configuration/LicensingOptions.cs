namespace EstherLink.Backend.Configuration;

public sealed class LicensingOptions
{
    public int OfflineCacheTtlHours { get; set; } = 24;
    public int SigningKeyRotationDays { get; set; } = 90;
}
