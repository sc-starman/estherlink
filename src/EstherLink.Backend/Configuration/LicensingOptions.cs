namespace EstherLink.Backend.Configuration;

public sealed class LicensingOptions
{
    public int OfflineCacheTtlHours { get; set; } = 24;
    public string SigningSecret { get; set; } = string.Empty;
}
