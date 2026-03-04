using EstherLink.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EstherLink.Backend.Health;

public sealed class DbReadyHealthCheck : IHealthCheck
{
    private readonly AppDbContext _dbContext;

    public DbReadyHealthCheck(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
        return canConnect
            ? HealthCheckResult.Healthy("Database connection is ready.")
            : HealthCheckResult.Unhealthy("Database is not reachable.");
    }
}
