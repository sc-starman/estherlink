using OmniRelay.Backend.Configuration;
using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.Services.Commerce;

public sealed class LicenseIssuanceService : ILicenseIssuanceService
{
    private readonly AppDbContext _dbContext;
    private readonly IOptions<CommerceOptions> _options;

    public LicenseIssuanceService(AppDbContext dbContext, IOptions<CommerceOptions> options)
    {
        _dbContext = dbContext;
        _options = options;
    }

    public async Task<LicenseEntity> IssuePaidLicenseAsync(Guid userId, Guid orderId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var order = await _dbContext.CommerceOrders.FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken)
            ?? throw new InvalidOperationException("Order was not found.");

        if (order.IssuedLicenseId.HasValue)
        {
            var existing = await _dbContext.Licenses.FirstAsync(x => x.Id == order.IssuedLicenseId.Value, cancellationToken);
            return existing;
        }

        var key = LicenseKeyGenerator.Generate();
        while (await _dbContext.Licenses.AnyAsync(x => x.LicenseKey == key, cancellationToken))
        {
            key = LicenseKeyGenerator.Generate();
        }

        var license = new LicenseEntity
        {
            Id = Guid.NewGuid(),
            LicenseKey = key,
            Status = LicenseStatus.Active,
            Plan = _options.Value.PaidLicensePlan,
            ExpiresAt = null,
            MaxDevices = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Licenses.Add(license);

        _dbContext.UserLicenses.Add(new UserLicenseEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            LicenseId = license.Id,
            Source = "purchase",
            CreatedAt = now,
            UpdatesEntitledUntil = now.AddMonths(_options.Value.UpdateEntitlementMonths)
        });

        order.IssuedLicenseId = license.Id;
        order.Status = "paid";
        order.UpdatedAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return license;
    }

    private static class LicenseKeyGenerator
    {
        public static string Generate()
        {
            Span<byte> buffer = stackalloc byte[10];
            Random.Shared.NextBytes(buffer);
            var hex = Convert.ToHexString(buffer);
            return $"OMNI-{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
        }
    }
}
