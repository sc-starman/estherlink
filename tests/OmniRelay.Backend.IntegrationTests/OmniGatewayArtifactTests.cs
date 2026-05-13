using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Backend.Services.Installers;

namespace OmniRelay.Backend.IntegrationTests;

public sealed class OmniGatewayArtifactTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public OmniGatewayArtifactTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DownloadOmniGateway_ShouldReturnNotFound_WhenArtifactNotUploaded()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();

        var client = _factory.CreateClient();

        var response = await client.GetAsync("/download/omni-gateway");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UploadOmniGateway_ShouldRejectInvalidExtension()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", "dev-admin-key");

        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent([1, 2, 3, 4]);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        content.Add(fileContent, "artifact", "omni-gateway.zip");

        var response = await client.PostAsync("/api/installer/upload-omni-gateway", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadOmniGateway_ThenDownload_ShouldReturnArtifact()
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
            content.Add(fileContent, "artifact", "omni-gateway.tar.gz");

            var uploadResponse = await client.PostAsync("/api/installer/upload-omni-gateway", content);
            uploadResponse.EnsureSuccessStatusCode();
        }

        var downloadResponse = await client.GetAsync("/download/omni-gateway");
        downloadResponse.EnsureSuccessStatusCode();
        Assert.Equal("application/gzip", downloadResponse.Content.Headers.ContentType?.MediaType);

        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(gzipBytes, downloadedBytes);
    }

    [Fact]
    public async Task UploadOmniGateway_WithBetaChannel_ShouldDownloadFromBetaRouteOnly()
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
            content.Add(fileContent, "artifact", "omni-gateway.tar.gz");
            content.Add(new StringContent("beta"), "channel");

            var uploadResponse = await client.PostAsync("/api/installer/upload-omni-gateway", content);
            uploadResponse.EnsureSuccessStatusCode();
        }

        var stableResponse = await client.GetAsync("/download/omni-gateway");
        Assert.Equal(HttpStatusCode.NotFound, stableResponse.StatusCode);

        var betaResponse = await client.GetAsync("/download/omni-gateway/beta");
        betaResponse.EnsureSuccessStatusCode();

        var downloadedBytes = await betaResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(gzipBytes, downloadedBytes);
    }

    [Fact]
    public async Task UploadOmniGateway_WithInvalidChannel_ShouldReturnBadRequest()
    {
        await _factory.ResetDatabaseAsync();
        await ClearArtifactsAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", "dev-admin-key");

        var gzipBytes = BuildMinimalGzip();

        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(gzipBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/gzip");
        content.Add(fileContent, "artifact", "omni-gateway.tar.gz");
        content.Add(new StringContent("rc"), "channel");

        var response = await client.PostAsync("/api/installer/upload-omni-gateway", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static byte[] BuildMinimalGzip()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("omni-gateway-test");
            gzip.Write(payload, 0, payload.Length);
        }

        return buffer.ToArray();
    }

    private async Task ClearArtifactsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IInstallerStorageService>();
        var stablePath = storage.GetOmniGatewayArtifactPath("stable");
        var betaPath = storage.GetOmniGatewayArtifactPath("beta");

        if (File.Exists(stablePath))
        {
            File.Delete(stablePath);
        }

        if (File.Exists(betaPath))
        {
            File.Delete(betaPath);
        }
    }
}
