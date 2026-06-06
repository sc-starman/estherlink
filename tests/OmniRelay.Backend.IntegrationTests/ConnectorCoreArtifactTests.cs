using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Backend.Services.Installers;

namespace OmniRelay.Backend.IntegrationTests;

public sealed class ConnectorCoreArtifactTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public ConnectorCoreArtifactTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DownloadConnectorCoreBeta_ShouldReturnNotFound_WhenNotUploaded()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/download/connector-core/beta/linux/amd64");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UploadConnectorCore_ThenDownloadStable_ShouldReturnArtifact()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", "dev-admin-key");
        var gzipBytes = BuildMinimalGzip();

        using (var content = BuildSignedReleaseContent(gzipBytes, "stable"))
        {
            var uploadResponse = await client.PostAsync("/api/installer/upload-connector-core", content);
            uploadResponse.EnsureSuccessStatusCode();
        }

        var stableResponse = await client.GetAsync("/download/connector-core/linux/amd64");
        stableResponse.EnsureSuccessStatusCode();

        var downloadedBytes = await stableResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(gzipBytes, downloadedBytes);
    }

    [Fact]
    public async Task UploadConnectorCore_WithBetaChannel_ShouldDownloadFromBetaRouteOnly()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", "dev-admin-key");
        var gzipBytes = BuildMinimalGzip();

        using (var content = BuildSignedReleaseContent(gzipBytes, "beta"))
        {
            var uploadResponse = await client.PostAsync("/api/installer/upload-connector-core", content);
            uploadResponse.EnsureSuccessStatusCode();
        }

        var stableResponse = await client.GetAsync("/download/connector-core/linux/amd64");
        Assert.Equal(HttpStatusCode.NotFound, stableResponse.StatusCode);

        var betaResponse = await client.GetAsync("/download/connector-core/beta/linux/amd64");
        betaResponse.EnsureSuccessStatusCode();

        var downloadedBytes = await betaResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(gzipBytes, downloadedBytes);
    }

    [Fact]
    public async Task UploadConnectorCore_WithInvalidChannel_ShouldReturnBadRequest()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", "dev-admin-key");
        var gzipBytes = BuildMinimalGzip();

        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(gzipBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/gzip");
        content.Add(fileContent, "artifact", "connector-core-linux-amd64.tar.gz");
        content.Add(new StringContent("linux"), "os");
        content.Add(new StringContent("amd64"), "arch");
        content.Add(new StringContent("preview"), "channel");

        var response = await client.PostAsync("/api/installer/upload-connector-core", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadConnectorCore_WithoutManifestAndSignature_ShouldReturnBadRequest()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", "dev-admin-key");
        var gzipBytes = BuildMinimalGzip();

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(gzipBytes), "artifact", "connector-core-linux-amd64.tar.gz");
        content.Add(new StringContent("linux"), "os");
        content.Add(new StringContent("amd64"), "arch");

        var response = await client.PostAsync("/api/installer/upload-connector-core", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadConnectorCore_WithMismatchedManifestHash_ShouldReturnBadRequest()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", "dev-admin-key");
        var gzipBytes = BuildMinimalGzip();

        using var content = BuildSignedReleaseContent(gzipBytes, "stable", new string('0', 64));
        var response = await client.PostAsync("/api/installer/upload-connector-core", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadConnectorCore_ThenDownloadManifestAndSignature_ShouldReturnReleaseFiles()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", "dev-admin-key");
        var gzipBytes = BuildMinimalGzip();

        using var content = BuildSignedReleaseContent(gzipBytes, "stable");
        (await client.PostAsync("/api/installer/upload-connector-core", content)).EnsureSuccessStatusCode();

        var manifestResponse = await client.GetAsync("/download/connector-core/linux/amd64/manifest");
        var signatureResponse = await client.GetAsync("/download/connector-core/linux/amd64/signature");
        manifestResponse.EnsureSuccessStatusCode();
        signatureResponse.EnsureSuccessStatusCode();
        Assert.Contains("\"schemaVersion\":1", await manifestResponse.Content.ReadAsStringAsync());
        Assert.Equal("test-signature", await signatureResponse.Content.ReadAsStringAsync());
    }

    private static byte[] BuildMinimalGzip()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("connector-core-test");
            gzip.Write(payload, 0, payload.Length);
        }

        return buffer.ToArray();
    }

    private static MultipartFormDataContent BuildSignedReleaseContent(
        byte[] artifact,
        string channel,
        string? overrideSha256 = null)
    {
        var sha256 = overrideSha256 ?? Convert.ToHexString(SHA256.HashData(artifact)).ToLowerInvariant();
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "1.2.3-test",
            channel,
            os = "linux",
            arch = "amd64",
            artifact = "connector-core-linux-amd64.tar.gz",
            sha256,
            sizeBytes = artifact.LongLength,
            publishedAtUtc = DateTimeOffset.UtcNow
        });

        var content = new MultipartFormDataContent();
        var artifactContent = new ByteArrayContent(artifact);
        artifactContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/gzip");
        content.Add(artifactContent, "artifact", "connector-core-linux-amd64.tar.gz");
        content.Add(new StringContent(manifest), "manifest", "manifest.json");
        content.Add(new StringContent("test-signature"), "signature", "manifest.sig");
        content.Add(new StringContent("linux"), "os");
        content.Add(new StringContent("amd64"), "arch");
        content.Add(new StringContent(channel), "channel");
        return content;
    }

    private async Task ClearArtifactsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IInstallerStorageService>();

        var paths = new[]
        {
            storage.GetConnectorCoreArtifactPath("stable", "linux", "amd64"),
            storage.GetConnectorCoreManifestPath("stable", "linux", "amd64"),
            storage.GetConnectorCoreSignaturePath("stable", "linux", "amd64"),
            storage.GetConnectorCoreArtifactPath("beta", "linux", "amd64"),
            storage.GetConnectorCoreManifestPath("beta", "linux", "amd64"),
            storage.GetConnectorCoreSignaturePath("beta", "linux", "amd64")
        };
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
