using System.Text.Json;
using OmniRelay.Backend.Configuration;
using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Data.Enums;
using OmniRelay.Backend.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.Services.Commerce;

public sealed class CommerceService : ICommerceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly AppDbContext _dbContext;
    private readonly IPayKryptClient _payKryptClient;
    private readonly ILicenseIssuanceService _licenseIssuanceService;
    private readonly ITrialPolicyService _trialPolicyService;
    private readonly IOptions<CommerceOptions> _commerceOptions;
    private readonly IOptions<PayKryptOptions> _payKryptOptions;
    private readonly ILogger<CommerceService> _logger;

    public CommerceService(
        AppDbContext dbContext,
        IPayKryptClient payKryptClient,
        ILicenseIssuanceService licenseIssuanceService,
        ITrialPolicyService trialPolicyService,
        IOptions<CommerceOptions> commerceOptions,
        IOptions<PayKryptOptions> payKryptOptions,
        ILogger<CommerceService> logger)
    {
        _dbContext = dbContext;
        _payKryptClient = payKryptClient;
        _licenseIssuanceService = licenseIssuanceService;
        _trialPolicyService = trialPolicyService;
        _commerceOptions = commerceOptions;
        _payKryptOptions = payKryptOptions;
        _logger = logger;
    }

    public async Task<TrialResult> StartTrialAsync(Guid userId, string userEmail, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (await _trialPolicyService.HasActiveOrIssuedTrialAsync(userId, cancellationToken))
        {
            return new TrialResult(false, "Trial already issued for this account.");
        }

        var key = GenerateLicenseKey("TRIAL");
        while (await _dbContext.Licenses.AnyAsync(x => x.LicenseKey == key, cancellationToken))
        {
            key = GenerateLicenseKey("TRIAL");
        }

        var trialExpiresAt = now.AddDays(_commerceOptions.Value.TrialDays);

        var license = new LicenseEntity
        {
            Id = Guid.NewGuid(),
            LicenseKey = key,
            Status = LicenseStatus.Active,
            Plan = "trial",
            ExpiresAt = trialExpiresAt,
            MaxDevices = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Licenses.Add(license);

        _dbContext.UserLicenses.Add(new UserLicenseEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            LicenseId = license.Id,
            Source = "trial",
            CreatedAt = now,
            UpdatesEntitledUntil = null
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Trial license created userId={UserId} emailHash={EmailHash}", userId, Sha256Util.HashHex(userEmail));
        return new TrialResult(true, "Trial started successfully.", license.LicenseKey, trialExpiresAt);
    }

    public async Task<CreateCheckoutResult> CreateCheckoutIntentAsync(Guid userId, string userEmail, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var payOpts = _payKryptOptions.Value;

        var order = new CommerceOrderEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrderType = "license_purchase",
            FiatAmount = payOpts.PriceUsd,
            Currency = "USD",
            Status = "creating_intent",
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.CommerceOrders.Add(order);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var createRequest = new PayKryptCreateIntentRequest
        {
            Amount = payOpts.PriceUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Currency = "USD",
            Description = $"OmniRelay License Order {order.Id}",
            CustomerEmail = userEmail,
            AllowedChains = payOpts.AllowedChains.Count == 0 ? null : payOpts.AllowedChains,
            AllowedAssets = payOpts.AllowedAssets.Count == 0 ? null : payOpts.AllowedAssets,
            ExpiresInMinutes = payOpts.ExpiresInMinutes
        };

        var idempotencyKey = Guid.NewGuid().ToString("D");
        var intent = await _payKryptClient.CreatePaymentIntentAsync(createRequest, idempotencyKey, cancellationToken);

        var intentEntity = new PayKryptIntentEntity
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            PayKryptIntentId = intent.Id,
            Status = intent.Status,
            ExpiresAt = intent.ExpiresAt,
            RawJson = JsonSerializer.Serialize(intent, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now
        };

        order.Status = intent.Status;
        order.UpdatedAt = now;
        _dbContext.PayKryptIntents.Add(intentEntity);

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Checkout intent created orderId={OrderId} userId={UserId} intentId={IntentId}",
            order.Id,
            userId,
            intent.Id);

        return new CreateCheckoutResult(
            order.Id,
            intent.Id,
            intent.Status,
            intent.ExpiresAt,
            intent.DepositAddresses ?? []);
    }

    public async Task<OrderStatusResult?> GetOrderStatusAsync(Guid userId, Guid orderId, bool refreshFromProvider, CancellationToken cancellationToken)
    {
        var order = await _dbContext.CommerceOrders
            .Include(x => x.IssuedLicense)
            .Include(x => x.PayKryptIntents)
            .FirstOrDefaultAsync(x => x.Id == orderId && x.UserId == userId, cancellationToken);

        if (order is null)
        {
            return null;
        }

        var intent = order.PayKryptIntents.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (intent is null)
        {
            return new OrderStatusResult(order.Id, order.Status, string.Empty, "missing", false, null, null, order.FiatAmount, order.Currency);
        }

        if (refreshFromProvider)
        {
            await ReconcileIntentAsync(intent.PayKryptIntentId, cancellationToken);
            await _dbContext.Entry(order).ReloadAsync(cancellationToken);
            await _dbContext.Entry(order).Collection(x => x.PayKryptIntents).LoadAsync(cancellationToken);
            await _dbContext.Entry(order).Reference(x => x.IssuedLicense).LoadAsync(cancellationToken);
            intent = order.PayKryptIntents.OrderByDescending(x => x.CreatedAt).First();
        }

        return new OrderStatusResult(
            order.Id,
            order.Status,
            intent.PayKryptIntentId,
            intent.Status,
            string.Equals(order.Status, "paid", StringComparison.OrdinalIgnoreCase),
            order.IssuedLicense?.LicenseKey,
            intent.ExpiresAt,
            order.FiatAmount,
            order.Currency);
    }

    public async Task<WebhookProcessResult> ProcessWebhookAsync(string payload, string? externalEventId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = string.IsNullOrWhiteSpace(externalEventId)
            ? $"evt_{Guid.NewGuid():N}"
            : externalEventId.Trim();
        var payloadHash = Sha256Util.HashHex(payload);

        var exists = await _dbContext.PayKryptWebhookEvents.AnyAsync(x => x.EventId == eventId || x.PayloadHash == payloadHash, cancellationToken);
        if (exists)
        {
            return new WebhookProcessResult(true, "Duplicate webhook ignored.");
        }

        var eventEntity = new PayKryptWebhookEventEntity
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            PayloadHash = payloadHash,
            ReceivedAt = now,
            Result = "received",
            RawJson = payload
        };

        _dbContext.PayKryptWebhookEvents.Add(eventEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var intentId = TryExtractIntentId(payload);
        if (string.IsNullOrWhiteSpace(intentId))
        {
            eventEntity.ProcessedAt = DateTimeOffset.UtcNow;
            eventEntity.Result = "ignored_missing_intent";
            await _dbContext.SaveChangesAsync(cancellationToken);
            return new WebhookProcessResult(true, "Webhook accepted (no intent id found).");
        }

        var reconciled = await ReconcileIntentAsync(intentId, cancellationToken);
        eventEntity.ProcessedAt = DateTimeOffset.UtcNow;
        eventEntity.Result = reconciled ? "processed" : "intent_not_found";
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new WebhookProcessResult(true, reconciled ? "Webhook processed." : "Webhook accepted but no matching intent.");
    }

    private async Task<bool> ReconcileIntentAsync(string intentId, CancellationToken cancellationToken)
    {
        var intentEntity = await _dbContext.PayKryptIntents
            .Include(x => x.Order)
            .ThenInclude(x => x.IssuedLicense)
            .FirstOrDefaultAsync(x => x.PayKryptIntentId == intentId, cancellationToken);

        if (intentEntity is null)
        {
            return false;
        }

        PayKryptIntentResponse? remote;
        try
        {
            remote = await _payKryptClient.GetPaymentIntentAsync(intentId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reconcile intent intentId={IntentId}", intentId);
            return true;
        }

        if (remote is null)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        intentEntity.Status = remote.Status;
        intentEntity.ExpiresAt = remote.ExpiresAt;
        intentEntity.RawJson = JsonSerializer.Serialize(remote, JsonOptions);
        intentEntity.UpdatedAt = now;
        intentEntity.Order.Status = remote.Status;
        intentEntity.Order.UpdatedAt = now;

        var isPaid = string.Equals(remote.Status, "confirmed", StringComparison.OrdinalIgnoreCase)
            && (remote.TransactionsSummary?.IsFullyPaid ?? false);

        if (isPaid)
        {
            intentEntity.Order.Status = "paid";

            if (!intentEntity.Order.IssuedLicenseId.HasValue)
            {
                await _licenseIssuanceService.IssuePaidLicenseAsync(intentEntity.Order.UserId, intentEntity.Order.Id, cancellationToken);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? TryExtractIntentId(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("paymentIntentId", out var pi) && pi.ValueKind == JsonValueKind.String)
            {
                return pi.GetString();
            }

            if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                var value = idProp.GetString();
                if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("pi_", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            if (root.TryGetProperty("data", out var dataObj)
                && dataObj.ValueKind == JsonValueKind.Object
                && dataObj.TryGetProperty("id", out var nestedId)
                && nestedId.ValueKind == JsonValueKind.String)
            {
                var value = nestedId.GetString();
                if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("pi_", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateLicenseKey(string prefix)
    {
        Span<byte> buffer = stackalloc byte[8];
        Random.Shared.NextBytes(buffer);
        var hex = Convert.ToHexString(buffer);
        return $"OMNI-{prefix}-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }
}