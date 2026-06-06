using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Net.Http;
using OmniRelay.UI.Models;

namespace OmniRelay.UI.Services;

public static class ConnectorCoreReleaseMetadataResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<ConnectorCoreReleaseMetadata> ResolveAsync(
        HttpClient httpClient,
        string baseUrl,
        string channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var normalizedChannel = string.Equals(channel?.Trim(), "beta", StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";
        var connectorManifestPath = normalizedChannel == "beta"
            ? "/download/connector-core/beta/linux/amd64/manifest"
            : "/download/connector-core/linux/amd64/manifest";
        var panelManifestPath = normalizedChannel == "beta"
            ? "/download/omni-gateway/beta/manifest"
            : "/download/omni-gateway/manifest";
        var panelArtifactPath = normalizedChannel == "beta"
            ? "/download/omni-gateway/beta"
            : "/download/omni-gateway";

        var connector = await GetJsonAsync<ConnectorManifest>(
            httpClient,
            normalizedBaseUrl + connectorManifestPath,
            cancellationToken);
        var panel = await GetJsonAsync<PanelManifest>(
            httpClient,
            normalizedBaseUrl + panelManifestPath,
            cancellationToken);

        if (connector.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(connector.Version) ||
            !string.Equals(connector.Channel, normalizedChannel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(connector.Os, "linux", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(connector.Arch, "amd64", StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(connector.Sha256))
        {
            throw new InvalidOperationException("Connector-core release manifest is invalid or does not match the requested channel.");
        }
        if (panel.SchemaVersion != 1 ||
            !string.Equals(panel.Channel, normalizedChannel, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(panel.Sha256) ||
            panel.SizeBytes <= 0)
        {
            throw new InvalidOperationException("OmniPanel release manifest is invalid or does not match the requested channel.");
        }

        return new ConnectorCoreReleaseMetadata(
            connector.Version.Trim(),
            normalizedChannel,
            normalizedBaseUrl + panelArtifactPath,
            panel.Sha256.Trim().ToLowerInvariant());
    }

    private static async Task<T> GetJsonAsync<T>(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Release metadata request failed for '{url}' with status {(int)response.StatusCode} ({response.ReasonPhrase}).",
                null,
                response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Release manifest at '{url}' is empty.");
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var value = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw new InvalidOperationException("Connector-core release base URL must be an absolute HTTPS URL.");
        }
        return value;
    }

    private static bool IsSha256(string? value) =>
        Regex.IsMatch(value?.Trim() ?? string.Empty, "^[a-fA-F0-9]{64}$");

    private sealed class ConnectorManifest
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

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class PanelManifest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("channel")]
        public string Channel { get; init; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; init; }
    }
}
