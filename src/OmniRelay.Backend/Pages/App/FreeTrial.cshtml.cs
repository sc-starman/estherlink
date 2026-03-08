using OmniRelay.Backend.Data;
using OmniRelay.Backend.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OmniRelay.Backend.Pages.App;

[Authorize]
public sealed class FreeTrialModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public FreeTrialModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public bool HasIssuedTrial { get; private set; }
    public string? TrialLicenseKey { get; private set; }
    public DateTimeOffset? TrialExpiresAt { get; private set; }
    public DateTimeOffset? TrialCreatedAt { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return;
        }

        var trial = await _dbContext.UserLicenses
            .AsNoTracking()
            .Include(x => x.License)
            .Where(x => x.UserId == userId && x.Source == "trial")
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.CreatedAt,
                LicenseKey = x.License.LicenseKey,
                x.License.ExpiresAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        HasIssuedTrial = trial is not null;
        TrialCreatedAt = trial?.CreatedAt;
        TrialLicenseKey = trial?.LicenseKey;
        TrialExpiresAt = trial?.ExpiresAt;
    }
}
