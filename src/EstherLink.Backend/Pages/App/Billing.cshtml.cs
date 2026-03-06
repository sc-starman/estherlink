using System.Text.Json;
using EstherLink.Backend.Configuration;
using EstherLink.Backend.Data;
using EstherLink.Backend.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Pages.App;

[Authorize]
public sealed class BillingModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IOptions<PayKryptOptions> _payKryptOptions;

    public BillingModel(AppDbContext dbContext, IOptions<PayKryptOptions> payKryptOptions)
    {
        _dbContext = dbContext;
        _payKryptOptions = payKryptOptions;
    }

    public decimal PriceUsd { get; private set; }
    public List<PaymentAttemptItem> Attempts { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        PriceUsd = _payKryptOptions.Value.PriceUsd;

        var userId = User.GetUserId();
        if (userId is null)
        {
            return;
        }

        var orders = await _dbContext.CommerceOrders
            .AsNoTracking()
            .Include(x => x.PayKryptIntents)
            .Include(x => x.IssuedLicense)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        Attempts = orders
            .SelectMany(order =>
            {
                if (order.PayKryptIntents.Count == 0)
                {
                    return new[]
                    {
                        new PaymentAttemptItem
                        {
                            AttemptId = "n/a",
                            OrderId = order.Id,
                            Amount = order.FiatAmount,
                            Currency = order.Currency,
                            State = order.Status,
                            CreatedAt = order.CreatedAt,
                            UpdatedAt = order.UpdatedAt,
                            IssuedLicenseKey = order.IssuedLicense?.LicenseKey
                        }
                    };
                }

                return order.PayKryptIntents.Select(intent => new PaymentAttemptItem
                {
                    AttemptId = intent.PayKryptIntentId,
                    OrderId = order.Id,
                    Amount = order.FiatAmount,
                    Currency = order.Currency,
                    State = intent.Status,
                    CreatedAt = intent.CreatedAt,
                    UpdatedAt = intent.UpdatedAt,
                    IssuedLicenseKey = order.IssuedLicense?.LicenseKey,
                    ProviderMessage = ExtractProviderMessage(intent.RawJson)
                });
            })
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
    }

    private static string? ExtractProviderMessage(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            if (root.TryGetProperty("statusMessage", out var statusMessage) && statusMessage.ValueKind == JsonValueKind.String)
            {
                return statusMessage.GetString();
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var nested) &&
                    nested.ValueKind == JsonValueKind.String)
                {
                    return nested.GetString();
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public sealed class PaymentAttemptItem
    {
        public string AttemptId { get; set; } = string.Empty;
        public Guid OrderId { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string State { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? IssuedLicenseKey { get; set; }
        public string? ProviderMessage { get; set; }
    }
}
