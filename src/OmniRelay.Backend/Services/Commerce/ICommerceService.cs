using OmniRelay.Backend.Data.Entities;

namespace OmniRelay.Backend.Services.Commerce;

public interface ILicenseIssuanceService
{
    Task<LicenseEntity> IssuePaidLicenseAsync(Guid userId, Guid orderId, CancellationToken cancellationToken);
}

public interface ITrialPolicyService
{
    Task<bool> HasActiveOrIssuedTrialAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IDownloadCatalogService
{
    Task<DownloadCatalogItem?> GetLatestAsync(string? channel, CancellationToken cancellationToken);
}

public interface ICommerceService
{
    Task<TrialResult> StartTrialAsync(Guid userId, string userEmail, CancellationToken cancellationToken);
    Task<CheckoutQuoteResult> QuoteCheckoutAsync(Guid userId, string? couponCode, CancellationToken cancellationToken);
    Task<CreateCheckoutResult> CreateCheckoutIntentAsync(Guid userId, string userEmail, string? couponCode, CancellationToken cancellationToken);
    Task<OrderStatusResult?> GetOrderStatusAsync(Guid userId, Guid orderId, bool refreshFromProvider, CancellationToken cancellationToken);
    Task<WebhookProcessResult> ProcessWebhookAsync(string payload, string? externalEventId, CancellationToken cancellationToken);
}

public sealed record TrialResult(bool Success, string Message, string? LicenseKey = null, DateTimeOffset? ExpiresAt = null);

public sealed record CreateCheckoutResult(
    Guid OrderId,
    string IntentId,
    string Status,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<PayKryptDepositAddress> DepositAddresses,
    decimal BaseAmount,
    decimal DiscountAmount,
    decimal FinalAmount,
    string Currency,
    string? AppliedCouponCode,
    int? AppliedDiscountPercent);

public sealed record CheckoutQuoteResult(
    bool Success,
    string Message,
    decimal BaseAmount,
    decimal DiscountAmount,
    decimal FinalAmount,
    string Currency,
    string? AppliedCouponCode,
    int? AppliedDiscountPercent);

public sealed record OrderStatusResult(
    Guid OrderId,
    string OrderStatus,
    string IntentId,
    string IntentStatus,
    bool IsPaid,
    string? LicenseKey,
    DateTimeOffset? ExpiresAt,
    decimal Amount,
    string Currency,
    decimal BaseAmount,
    decimal DiscountAmount,
    string? AppliedCouponCode,
    int? AppliedDiscountPercent);

public sealed record WebhookProcessResult(bool Success, string Message);

public sealed record DownloadCatalogItem(
    string Version,
    string Channel,
    DateTimeOffset PublishedAt,
    string DownloadUrl,
    string Sha256,
    string Notes);
