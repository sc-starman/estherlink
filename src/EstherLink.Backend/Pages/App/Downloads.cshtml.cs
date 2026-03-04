using EstherLink.Backend.Configuration;
using EstherLink.Backend.Services.Commerce;
using EstherLink.Backend.Data;
using EstherLink.Backend.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Pages.App;

[Authorize]
public sealed class DownloadsModel : PageModel
{
    private readonly IDownloadCatalogService _downloadCatalogService;
    private readonly AppDbContext _dbContext;
    private readonly IOptions<WebOptions> _webOptions;

    public DownloadsModel(IDownloadCatalogService downloadCatalogService, AppDbContext dbContext, IOptions<WebOptions> webOptions)
    {
        _downloadCatalogService = downloadCatalogService;
        _dbContext = dbContext;
        _webOptions = webOptions;
    }

    public DownloadCatalogItem? Latest { get; private set; }
    public string DocumentationUrl { get; private set; } = string.Empty;
    public List<EntitlementItem> Entitlements { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        DocumentationUrl = _webOptions.Value.DocumentationUrl;
        Latest = await _downloadCatalogService.GetLatestAsync(null, cancellationToken);

        var userId = User.GetUserId();
        if (userId is null)
        {
            return;
        }

        Entitlements = await _dbContext.UserLicenses
            .AsNoTracking()
            .Include(x => x.License)
            .Where(x => x.UserId == userId && x.Source == "purchase")
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new EntitlementItem
            {
                LicenseKey = x.License.LicenseKey,
                UpdatesEntitledUntil = x.UpdatesEntitledUntil
            })
            .ToListAsync(cancellationToken);
    }

    public sealed class EntitlementItem
    {
        public string LicenseKey { get; set; } = string.Empty;
        public DateTimeOffset? UpdatesEntitledUntil { get; set; }
    }
}