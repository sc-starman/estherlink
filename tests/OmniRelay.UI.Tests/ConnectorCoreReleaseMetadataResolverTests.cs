using System.Net;
using System.Net.Http;
using System.Text;
using OmniRelay.UI.Services;

namespace OmniRelay.UI.Tests;

public sealed class ConnectorCoreReleaseMetadataResolverTests
{
    [Fact]
    public async Task ResolveAsync_MapsStableReleaseMetadata()
    {
        using var client = new HttpClient(new ManifestHandler());

        var result = await ConnectorCoreReleaseMetadataResolver.ResolveAsync(
            client,
            "https://releases.example.test/",
            "stable");

        Assert.Equal("2.3.0", result.Version);
        Assert.Equal("stable", result.Channel);
        Assert.Equal("https://releases.example.test/download/omni-gateway", result.PanelArtifactUrl);
        Assert.Equal(new string('b', 64), result.PanelArtifactSha256);
    }

    [Fact]
    public async Task ResolveAsync_RejectsChannelMismatch()
    {
        using var client = new HttpClient(new ManifestHandler(connectorChannel: "beta"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectorCoreReleaseMetadataResolver.ResolveAsync(client, "https://releases.example.test", "stable"));

        Assert.Contains("requested channel", error.Message);
    }

    private sealed class ManifestHandler(string connectorChannel = "stable") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.Contains("connector-core", StringComparison.Ordinal)
                ? $$"""{"schemaVersion":1,"version":"2.3.0","channel":"{{connectorChannel}}","os":"linux","arch":"amd64","artifact":"connector-core-linux-amd64.tar.gz","sha256":"{{new string('a', 64)}}","sizeBytes":1234}"""
                : $$"""{"schemaVersion":1,"channel":"stable","artifact":"omni-gateway.tar.gz","sha256":"{{new string('b', 64)}}","sizeBytes":5678}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
