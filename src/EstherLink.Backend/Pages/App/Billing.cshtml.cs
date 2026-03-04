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
    private readonly IOptions<WebOptions> _webOptions;

    public BillingModel(AppDbContext dbContext, IOptions<PayKryptOptions> payKryptOptions, IOptions<WebOptions> webOptions)
    {
        _dbContext = dbContext;
        _payKryptOptions = payKryptOptions;
        _webOptions = webOptions;
    }

    public decimal PriceUsd { get; private set; }
    public string DocumentationUrl { get; private set; } = string.Empty;
    public List<OrderItem> Orders { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        DocumentationUrl = _webOptions.Value.DocumentationUrl;
        PriceUsd = _payKryptOptions.Value.PriceUsd;

        var userId = User.GetUserId();
        if (userId is null)
        {
            return;
        }

        Orders = await _dbContext.CommerceOrders
            .AsNoTracking()
            .Include(x => x.PayKryptIntents)
            .Include(x => x.IssuedLicense)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(10)
            .Select(x => new OrderItem
            {
                OrderId = x.Id,
                Status = x.Status,
                IntentId = x.PayKryptIntents.OrderByDescending(i => i.CreatedAt).Select(i => i.PayKryptIntentId).FirstOrDefault() ?? "n/a",
                LicenseKey = x.IssuedLicense != null ? x.IssuedLicense.LicenseKey : null
            })
            .ToListAsync(cancellationToken);
    }

    public sealed class OrderItem
    {
        public Guid OrderId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string IntentId { get; set; } = string.Empty;
        public string? LicenseKey { get; set; }
    }
}