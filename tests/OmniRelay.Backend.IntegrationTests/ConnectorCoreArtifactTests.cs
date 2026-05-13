using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
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

        using (var content = new MultipartFormDataContent())
        using (var fileContent = new ByteArrayContent(gzipBytes))
        {
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/gzip");
            content.Add(fileContent, "artifact", "connector-core-linux-amd64.tar.gz");
            content.Add(new StringContent("linux"), "os");
            content.Add(new StringContent("amd64"), "arch");

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

        using (var content = new MultipartFormDataContent())
        using (var fileContent = new ByteArrayContent(gzipBytes))
        {
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/gzip");
            content.Add(fileContent, "artifact", "connector-core-linux-amd64.tar.gz");
            content.Add(new StringContent("linux"), "os");
            content.Add(new StringContent("amd64"), "arch");
            content.Add(new StringContent("beta"), "channel");

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

    private async Task ClearArtifactsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IInstallerStorageService>();

        var stableAmd64Path = storage.GetConnectorCoreArtifactPath("stable", "linux", "amd64");
        var betaAmd64Path = storage.GetConnectorCoreArtifactPath("beta", "linux", "amd64");

        if (File.Exists(stableAmd64Path))
        {
            File.Delete(stableAmd64Path);
        }

        if (File.Exists(betaAmd64Path))
        {
            File.Delete(betaAmd64Path);
        }
    }
}
