using EstherLink.Backend.Data;
using EstherLink.Backend.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace EstherLink.Backend.Pages.App;

[Authorize]
public sealed class LicensesModel : PageModel
{
    private const int TransferLimitPerRollingYear = 3;
    private const int TransferWindowDays = 365;

    private readonly AppDbContext _dbContext;

    public LicensesModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public List<LicenseItem> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var transferWindowStart = now.AddDays(-TransferWindowDays);

        var userLicenses = await _dbContext.UserLicenses
            .AsNoTracking()
            .Include(x => x.License)
                .ThenInclude(x => x.Activations)
            .Include(x => x.License)
                .ThenInclude(x => x.Transfers)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        Items = userLicenses.Select(x =>
        {
            var activeActivation = x.License.Activations
                .Where(a => !a.IsBlocked)
                .OrderByDescending(a => a.LastSeenAt)
                .FirstOrDefault();

            var transfersUsed = x.License.Transfers.Count(t => t.CreatedAt >= transferWindowStart);
            var transfersRemaining = Math.Max(0, TransferLimitPerRollingYear - transfersUsed);

            return new LicenseItem
            {
                LicenseId = x.License.Id,
                Source = x.Source,
                LicenseKey = x.License.LicenseKey,
                Plan = x.License.Plan,
                Status = x.License.Status.ToString().ToLowerInvariant(),
                Term = x.License.ExpiresAt.HasValue
                    ? $"Until {x.License.ExpiresAt.Value:yyyy-MM-dd}"
                    : "Perpetual",
                CreatedAt = x.CreatedAt,
                CurrentDeviceId = ToDeviceHint(activeActivation?.FingerprintHash),
                LastSeenAt = activeActivation?.LastSeenAt,
                TransfersUsedInWindow = transfersUsed,
                TransfersRemainingInWindow = transfersRemaining,
                ExpiresAt = x.License.ExpiresAt,
                UpdatesEntitledUntil = x.UpdatesEntitledUntil
            };
        }).ToList();
    }

    private static string ToDeviceHint(string? fingerprintHash)
    {
        if (string.IsNullOrWhiteSpace(fingerprintHash))
        {
            return "none";
        }

        var value = fingerprintHash.Trim();
        if (value.Length <= 12)
        {
            return value;
        }

        return $"{value[..8]}...{value[^4..]}";
    }

    public sealed class LicenseItem
    {
        public Guid LicenseId { get; set; }
        public string Source { get; set; } = string.Empty;
        public string LicenseKey { get; set; } = string.Empty;
        public string Plan { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Term { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public string CurrentDeviceId { get; set; } = "none";
        public DateTimeOffset? LastSeenAt { get; set; }
        public int TransfersUsedInWindow { get; set; }
        public int TransfersRemainingInWindow { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? UpdatesEntitledUntil { get; set; }
    }
}
