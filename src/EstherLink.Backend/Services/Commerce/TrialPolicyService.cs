using EstherLink.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace EstherLink.Backend.Services.Commerce;

public sealed class TrialPolicyService : ITrialPolicyService
{
    private readonly AppDbContext _dbContext;

    public TrialPolicyService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> HasActiveOrIssuedTrialAsync(Guid userId, CancellationToken cancellationToken)
    {
        return _dbContext.UserLicenses
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.Source == "trial", cancellationToken);
    }
}