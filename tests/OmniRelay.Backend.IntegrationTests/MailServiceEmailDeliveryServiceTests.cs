using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OmniRelay.Backend.Configuration;
using OmniRelay.Backend.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.IntegrationTests;

public sealed class MailServiceEmailDeliveryServiceTests
{
    [Fact]
    public async Task SendAsync_ShouldSendExpectedRequestContract()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
        };
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new MailServiceOptions
        {
            BaseUrl = "https://mail-sender.omnirelay.net:8443",
            SendPath = "/send",
            ApiKey = "test-key",
            ApiKeyHeader = "x-api-key",
            TimeoutSeconds = 30,
            RetryCount = 0
        });

        var service = new MailServiceEmailDeliveryService(httpClient, options, NullLogger<MailServiceEmailDeliveryService>.Instance);

        await service.SendAsync(
            new EmailDeliveryMessage(
                ToEmail: "user@example.com",
                Subject: "Verification",
                Body: "Line1\nLine2"),
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://mail-sender.omnirelay.net:8443/send", handler.LastUri);
        Assert.Equal("test-key", handler.LastApiKey);

        Assert.NotNull(handler.LastBodyJson);
        Assert.Equal("user@example.com", handler.LastBodyJson.RootElement.GetProperty("to").GetString());
        Assert.Equal("Verification", handler.LastBodyJson.RootElement.GetProperty("subject").GetString());
        Assert.Equal("<p>Line1<br/>Line2</p>", handler.LastBodyJson.RootElement.GetProperty("html").GetString());
    }

    [Fact]
    public async Task SendAsync_ShouldFailFast_WhenMailServiceReturnsServerError()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream failure", Encoding.UTF8, MediaTypeHeaderValue.Parse("text/plain"))
            }
        };
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new MailServiceOptions
        {
            BaseUrl = "https://mail-sender.omnirelay.net:8443",
            SendPath = "/send",
            ApiKey = "test-key",
            ApiKeyHeader = "x-api-key",
            TimeoutSeconds = 30,
            RetryCount = 0
        });

        var service = new MailServiceEmailDeliveryService(httpClient, options, NullLogger<MailServiceEmailDeliveryService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(
            new EmailDeliveryMessage(
                ToEmail: "user@example.com",
                Subject: "Verification",
                Body: "Line1"),
            CancellationToken.None));

        Assert.Contains("502", exception.Message);
        Assert.Contains("upstream failure", exception.Message);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public HttpMethod? LastMethod { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastApiKey { get; private set; }
        public JsonDocument? LastBodyJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri?.ToString();
            LastApiKey = request.Headers.TryGetValues("x-api-key", out var values) ? values.SingleOrDefault() : null;

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body))
            {
                LastBodyJson = JsonDocument.Parse(body);
            }

            return Response;
        }
    }
}

