using System.Text.Json;
using OmniRelay.Backend.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OmniRelay.Backend.Pages.Admin;

public sealed class BillingModel : PageModel
{
    private const int DefaultPageSize = 50;
    private readonly AppDbContext _dbContext;

    public BillingModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public int PageSize { get; } = DefaultPageSize;
    public int TotalItems { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));
    public List<BillingOrderItem> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (PageNumber < 1)
        {
            PageNumber = 1;
        }

        var query = _dbContext.CommerceOrders
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt);

        TotalItems = await query.CountAsync(cancellationToken);
        if (PageNumber > TotalPages)
        {
            PageNumber = TotalPages;
        }

        var skip = (PageNumber - 1) * PageSize;

        var rows = await query
            .Skip(skip)
            .Take(PageSize)
            .Select(x => new
            {
                x.Id,
                UserEmail = x.User.Email,
                x.BaseFiatAmount,
                x.DiscountAmount,
                x.FiatAmount,
                x.Currency,
                x.Status,
                x.DiscountCode,
                x.DiscountPercent,
                x.CreatedAt,
                x.UpdatedAt,
                IssuedLicenseKey = x.IssuedLicense != null ? x.IssuedLicense.LicenseKey : null,
                LatestIntentId = x.PayKryptIntents
                    .OrderByDescending(i => i.CreatedAt)
                    .Select(i => i.PayKryptIntentId)
                    .FirstOrDefault(),
                LatestIntentStatus = x.PayKryptIntents
                    .OrderByDescending(i => i.CreatedAt)
                    .Select(i => i.Status)
                    .FirstOrDefault(),
                LatestIntentExpiresAt = x.PayKryptIntents
                    .OrderByDescending(i => i.CreatedAt)
                    .Select(i => i.ExpiresAt)
                    .FirstOrDefault(),
                LatestIntentRawJson = x.PayKryptIntents
                    .OrderByDescending(i => i.CreatedAt)
                    .Select(i => i.RawJson)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        Items = rows.Select(x => new BillingOrderItem
        {
            OrderId = x.Id,
            UserEmail = x.UserEmail ?? string.Empty,
            BaseAmount = x.BaseFiatAmount,
            DiscountAmount = x.DiscountAmount,
            Amount = x.FiatAmount,
            Currency = x.Currency,
            OrderStatus = x.Status,
            AppliedCouponCode = x.DiscountCode,
            AppliedDiscountPercent = x.DiscountPercent,
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt,
            IssuedLicenseKey = x.IssuedLicenseKey,
            LatestIntentId = x.LatestIntentId,
            LatestIntentStatus = x.LatestIntentStatus,
            LatestIntentExpiresAt = x.LatestIntentExpiresAt,
            LatestProviderMessage = ExtractProviderMessage(x.LatestIntentRawJson)
        }).ToList();
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

    public sealed class BillingOrderItem
    {
        public Guid OrderId { get; set; }
        public string UserEmail { get; set; } = string.Empty;
        public decimal BaseAmount { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string OrderStatus { get; set; } = string.Empty;
        public string? AppliedCouponCode { get; set; }
        public int? AppliedDiscountPercent { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? IssuedLicenseKey { get; set; }
        public string? LatestIntentId { get; set; }
        public string? LatestIntentStatus { get; set; }
        public DateTimeOffset? LatestIntentExpiresAt { get; set; }
        public string? LatestProviderMessage { get; set; }
    }
}
