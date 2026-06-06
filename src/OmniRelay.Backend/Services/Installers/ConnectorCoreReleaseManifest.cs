using System.Text.Json.Serialization;

namespace OmniRelay.Backend.Services.Installers;

public sealed class ConnectorCoreReleaseManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    [JsonPropertyName("os")]
    public string Os { get; init; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Arch { get; init; } = string.Empty;

    [JsonPropertyName("artifact")]
    public string Artifact { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTimeOffset PublishedAtUtc { get; init; }
}
