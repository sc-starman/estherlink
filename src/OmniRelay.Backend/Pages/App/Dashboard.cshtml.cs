using OmniRelay.Backend.Data;
using OmniRelay.Backend.Services.Commerce;
using OmniRelay.Backend.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OmniRelay.Backend.Pages.App;

[Authorize]
public sealed class DashboardModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IDownloadCatalogService _downloadCatalogService;

    public DashboardModel(AppDbContext dbContext, IDownloadCatalogService downloadCatalogService)
    {
        _dbContext = dbContext;
        _downloadCatalogService = downloadCatalogService;
    }

    public string Email { get; private set; } = string.Empty;
    public int LicenseCount { get; private set; }
    public bool HasTrial { get; private set; }
    public bool HasPaid { get; private set; }
    public string LatestVersion { get; private set; } = "n/a";
    public string DocumentationUrl { get; private set; } = "/docs";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return;
        }

        Email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "unknown";

        var userLicenses = await _dbContext.UserLicenses
            .AsNoTracking()
            .Include(x => x.License)
            .Where(x => x.UserId == userId.Value)
            .ToListAsync(cancellationToken);

        LicenseCount = userLicenses.Count;
        HasTrial = userLicenses.Any(x => x.Source == "trial");
        HasPaid = userLicenses.Any(x => x.Source == "purchase");

        var latest = await _downloadCatalogService.GetLatestAsync(null, cancellationToken);
        LatestVersion = latest?.Version ?? "n/a";
    }
}
