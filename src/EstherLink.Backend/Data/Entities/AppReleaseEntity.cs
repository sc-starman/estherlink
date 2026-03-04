namespace EstherLink.Backend.Data.Entities;

public sealed class AppReleaseEntity
{
    public Guid Id { get; set; }
    public string Channel { get; set; } = "stable";
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset PublishedAt { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string MinSupportedVersion { get; set; } = string.Empty;
}
