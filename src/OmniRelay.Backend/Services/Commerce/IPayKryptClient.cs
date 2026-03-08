using System.Text.Json.Serialization;

namespace OmniRelay.Backend.Services.Commerce;

public sealed class PayKryptCreateIntentRequest
{
    [JsonPropertyName("amount")]
    public string Amount { get; set; } = "0";

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("customerEmail")]
    public string? CustomerEmail { get; set; }

    [JsonPropertyName("allowedChains")]
    public List<string>? AllowedChains { get; set; }

    [JsonPropertyName("allowedAssets")]
    public List<string>? AllowedAssets { get; set; }

    [JsonPropertyName("expiresInMinutes")]
    public int? ExpiresInMinutes { get; set; }
}

public sealed class PayKryptIntentResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public string Amount { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("depositAddresses")]
    public List<PayKryptDepositAddress>? DepositAddresses { get; set; }

    [JsonPropertyName("transactionsSummary")]
    public PayKryptTransactionsSummary? TransactionsSummary { get; set; }
}

public sealed class PayKryptTransactionsSummary
{
    [JsonPropertyName("isFullyPaid")]
    public bool IsFullyPaid { get; set; }

    [JsonPropertyName("outstandingFiat")]
    public string? OutstandingFiat { get; set; }
}

public sealed class PayKryptDepositAddress
{
    [JsonPropertyName("chain")]
    public string? Chain { get; set; }

    [JsonPropertyName("asset")]
    public string? Asset { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

public interface IPayKryptClient
{
    Task<PayKryptIntentResponse> CreatePaymentIntentAsync(PayKryptCreateIntentRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task<PayKryptIntentResponse?> GetPaymentIntentAsync(string intentId, CancellationToken cancellationToken);
}