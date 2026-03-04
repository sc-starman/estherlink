namespace EstherLink.Backend.Contracts.App;

public sealed class AppLatestResponse
{
    public bool UpdateAvailable { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public string MinSupportedVersion { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset PublishedAt { get; set; }
}
