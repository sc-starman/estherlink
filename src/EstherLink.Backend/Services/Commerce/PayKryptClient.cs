using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EstherLink.Backend.Configuration;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Services.Commerce;

public sealed class PayKryptClient : IPayKryptClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<PayKryptOptions> _options;
    private readonly ILogger<PayKryptClient> _logger;

    public PayKryptClient(
        IHttpClientFactory httpClientFactory,
        IOptions<PayKryptOptions> options,
        ILogger<PayKryptClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<PayKryptIntentResponse> CreatePaymentIntentAsync(
        PayKryptCreateIntentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/payment-intents")
        {
            Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json")
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("PayKrypt create intent failed status={StatusCode} body={Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"PayKrypt create intent failed ({(int)response.StatusCode}).");
        }

        var parsed = JsonSerializer.Deserialize<PayKryptIntentResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("PayKrypt create intent response was invalid.");

        return parsed;
    }

    public async Task<PayKryptIntentResponse?> GetPaymentIntentAsync(string intentId, CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        using var response = await http.GetAsync($"/v1/payment-intents/{Uri.EscapeDataString(intentId)}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if ((int)response.StatusCode == 404)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("PayKrypt get intent failed intentId={IntentId} status={StatusCode} body={Body}", intentId, (int)response.StatusCode, body);
            throw new InvalidOperationException($"PayKrypt get intent failed ({(int)response.StatusCode}).");
        }

        return JsonSerializer.Deserialize<PayKryptIntentResponse>(body, JsonOptions);
    }

    private HttpClient CreateHttpClient()
    {
        var http = _httpClientFactory.CreateClient(nameof(PayKryptClient));
        var opts = _options.Value;

        if (string.IsNullOrWhiteSpace(opts.BaseUrl))
        {
            throw new InvalidOperationException("PayKrypt base URL is not configured.");
        }

        if (string.IsNullOrWhiteSpace(opts.SecretApiKey))
        {
            throw new InvalidOperationException("PayKrypt secret API key is not configured.");
        }

        http.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/'));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.SecretApiKey);
        return http;
    }
}