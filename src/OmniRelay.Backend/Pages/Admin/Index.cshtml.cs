using OmniRelay.Backend.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OmniRelay.Backend.Pages.Admin;

public sealed class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public int TrialLicenses { get; private set; }
    public int PaidLicenses { get; private set; }
    public int BillingOrders { get; private set; }
    public int DiscountCoupons { get; private set; }
    public int ActiveDiscountCoupons { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        TrialLicenses = await _dbContext.UserLicenses
            .AsNoTracking()
            .CountAsync(x => x.Source == "trial", cancellationToken);

        PaidLicenses = await _dbContext.UserLicenses
            .AsNoTracking()
            .CountAsync(x => x.Source == "purchase", cancellationToken);

        BillingOrders = await _dbContext.CommerceOrders
            .AsNoTracking()
            .CountAsync(cancellationToken);

        DiscountCoupons = await _dbContext.DiscountCoupons
            .AsNoTracking()
            .CountAsync(cancellationToken);

        ActiveDiscountCoupons = await _dbContext.DiscountCoupons
            .AsNoTracking()
            .CountAsync(x => x.IsActive && x.DisabledAt == null, cancellationToken);
    }
}
