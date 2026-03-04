using EstherLink.Backend.Configuration;
using EstherLink.Backend.Data;
using EstherLink.Backend.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Pages.App;

[Authorize]
public sealed class LicensesModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IOptions<WebOptions> _webOptions;

    public LicensesModel(AppDbContext dbContext, IOptions<WebOptions> webOptions)
    {
        _dbContext = dbContext;
        _webOptions = webOptions;
    }

    public List<LicenseItem> Items { get; private set; } = [];
    public string DocumentationUrl { get; private set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        DocumentationUrl = _webOptions.Value.DocumentationUrl;

        var userId = User.GetUserId();
        if (userId is null)
        {
            return;
        }

        Items = await _dbContext.UserLicenses
            .AsNoTracking()
            .Include(x => x.License)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new LicenseItem
            {
                Source = x.Source,
                LicenseKey = x.License.LicenseKey,
                Plan = x.License.Plan,
                Status = x.License.Status.ToString().ToLowerInvariant(),
                MaxDevices = x.License.MaxDevices,
                ExpiresAt = x.License.ExpiresAt,
                UpdatesEntitledUntil = x.UpdatesEntitledUntil
            })
            .ToListAsync(cancellationToken);
    }

    public sealed class LicenseItem
    {
        public string Source { get; set; } = string.Empty;
        public string LicenseKey { get; set; } = string.Empty;
        public string Plan { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int MaxDevices { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? UpdatesEntitledUntil { get; set; }
    }
}