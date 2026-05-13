using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OmniRelay.Backend.Pages.Admin;

public sealed class PaidLicensesModel : PageModel
{
    private const int DefaultPageSize = 50;
    private readonly AppDbContext _dbContext;

    public PaidLicensesModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public int PageSize { get; } = DefaultPageSize;
    public int TotalItems { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));
    public List<AdminLicenseItem> Items { get; private set; } = [];
    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (PageNumber < 1)
        {
            PageNumber = 1;
        }

        var query = _dbContext.UserLicenses
            .AsNoTracking()
            .Where(x => x.Source == "purchase")
            .OrderByDescending(x => x.CreatedAt);

        TotalItems = await query.CountAsync(cancellationToken);

        if (PageNumber > TotalPages)
        {
            PageNumber = TotalPages;
        }

        var skip = (PageNumber - 1) * PageSize;
        var transferWindowStart = DateTimeOffset.UtcNow.AddDays(-365);

        Items = await query
            .Skip(skip)
            .Take(PageSize)
            .Select(x => new AdminLicenseItem
            {
                UserId = x.UserId,
                UserEmail = x.User.Email ?? string.Empty,
                LicenseId = x.LicenseId,
                LicenseKey = x.License.LicenseKey,
                Plan = x.License.Plan,
                Status = x.License.Status,
                Source = x.Source,
                CreatedAt = x.CreatedAt,
                ExpiresAt = x.License.ExpiresAt,
                UpdatesEntitledUntil = x.UpdatesEntitledUntil,
                TransfersUsedInWindow = _dbContext.LicenseTransfers.Count(t =>
                    t.LicenseId == x.LicenseId &&
                    t.CreatedAt >= transferWindowStart)
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostResetTransfersAsync(Guid licenseId, int pageNumber, CancellationToken cancellationToken)
    {
        var isPaidLicense = await _dbContext.UserLicenses
            .AsNoTracking()
            .AnyAsync(x => x.LicenseId == licenseId && x.Source == "purchase", cancellationToken);

        if (!isPaidLicense)
        {
            ErrorMessage = "Paid license was not found.";
            return RedirectToPage(new { pageNumber = Math.Max(1, pageNumber) });
        }

        var transfers = await _dbContext.LicenseTransfers
            .Where(x => x.LicenseId == licenseId)
            .ToListAsync(cancellationToken);

        if (transfers.Count > 0)
        {
            _dbContext.LicenseTransfers.RemoveRange(transfers);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        StatusMessage = "Transfer usage was reset. Remaining transfers are now back to 3.";
        return RedirectToPage(new { pageNumber = Math.Max(1, pageNumber) });
    }

    public sealed class AdminLicenseItem
    {
        public Guid UserId { get; set; }
        public string UserEmail { get; set; } = string.Empty;
        public Guid LicenseId { get; set; }
        public string LicenseKey { get; set; } = string.Empty;
        public string Plan { get; set; } = string.Empty;
        public LicenseStatus Status { get; set; }
        public string Source { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? UpdatesEntitledUntil { get; set; }
        public int TransfersUsedInWindow { get; set; }
    }
}
