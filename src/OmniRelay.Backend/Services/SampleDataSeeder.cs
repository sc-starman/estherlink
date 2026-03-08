using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Data.Enums;
using OmniRelay.Backend.Utilities;
using Microsoft.EntityFrameworkCore;

namespace OmniRelay.Backend.Services;

public sealed class SampleDataSeeder
{
    private readonly AppDbContext _dbContext;

    public SampleDataSeeder(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SampleSeedResult> SeedAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var created = new List<string>();
        var skipped = new List<string>();

        var demoLicense = await _dbContext.Licenses.FirstOrDefaultAsync(
            x => x.LicenseKey == "DEMO-KEY-001",
            cancellationToken);

        if (demoLicense is null)
        {
            demoLicense = new LicenseEntity
            {
                Id = Guid.NewGuid(),
                LicenseKey = "DEMO-KEY-001",
                Status = LicenseStatus.Active,
                Plan = "pro",
                ExpiresAt = now.AddYears(1),
                MaxDevices = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Licenses.Add(demoLicense);
            created.Add("license:DEMO-KEY-001");
        }
        else
        {
            skipped.Add("license:DEMO-KEY-001");
        }

        var setGroupId = await _dbContext.WhitelistSets
            .Where(x => x.CountryCode == "IR" && x.Name == "IR Core" && x.Category == "core")
            .Select(x => x.SetGroupId)
            .FirstOrDefaultAsync(cancellationToken);

        if (setGroupId == Guid.Empty)
        {
            setGroupId = Guid.NewGuid();
            await CreateWhitelistVersionAsync(
                setGroupId,
                "IR",
                "IR Core",
                "core",
                1,
                ["5.106.0.0/16", "37.32.0.0/12", "2.176.0.0/12"],
                now,
                cancellationToken);

            await CreateWhitelistVersionAsync(
                setGroupId,
                "IR",
                "IR Core",
                "core",
                2,
                ["37.32.0.0/12", "2.176.0.0/12", "185.4.0.0/16"],
                now.AddMinutes(1),
                cancellationToken);

            created.Add("whitelist:IR Core v1+v2");
        }
        else
        {
            skipped.Add("whitelist:IR Core");
        }

        var stable130 = await _dbContext.AppReleases.FirstOrDefaultAsync(
            x => x.Channel == "stable" && x.Version == "1.3.0",
            cancellationToken);

        if (stable130 is null)
        {
            _dbContext.AppReleases.AddRange(
                new AppReleaseEntity
                {
                    Id = Guid.NewGuid(),
                    Channel = "stable",
                    Version = "1.2.0",
                    PublishedAt = now.AddDays(-10),
                    Notes = "Initial stable release.",
                    DownloadUrl = "/download/windows",
                    Sha256 = Sha256Util.HashHex("OmniRelay-1.2.0"),
                    MinSupportedVersion = "1.0.0"
                },
                new AppReleaseEntity
                {
                    Id = Guid.NewGuid(),
                    Channel = "stable",
                    Version = "1.3.0",
                    PublishedAt = now.AddDays(-1),
                    Notes = "Improved routing policy and stability fixes.",
                    DownloadUrl = "/download/windows",
                    Sha256 = Sha256Util.HashHex("OmniRelay-1.3.0"),
                    MinSupportedVersion = "1.1.0"
                });
            created.Add("app_release:stable 1.2.0+1.3.0");
        }
        else
        {
            skipped.Add("app_release:stable 1.3.0");
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new SampleSeedResult(created, skipped);
    }

    private async Task CreateWhitelistVersionAsync(
        Guid setGroupId,
        string countryCode,
        string name,
        string category,
        int version,
        IReadOnlyList<string> entries,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var normalized = entries
            .Select(x =>
            {
                if (!CidrNormalizer.TryNormalize(x, out var cidr, out var error))
                {
                    throw new InvalidOperationException(error);
                }

                return cidr;
            })
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var setEntity = new WhitelistSetEntity
        {
            Id = Guid.NewGuid(),
            SetGroupId = setGroupId,
            CountryCode = countryCode,
            Name = name,
            Category = category,
            Version = version,
            Sha256 = Sha256Util.HashHex(string.Join('\n', normalized)),
            CreatedAt = createdAt
        };

        _dbContext.WhitelistSets.Add(setEntity);
        _dbContext.WhitelistEntries.AddRange(normalized.Select(entry => new WhitelistEntryEntity
        {
            Id = Guid.NewGuid(),
            WhitelistSetId = setEntity.Id,
            Cidr = entry
        }));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}

public sealed record SampleSeedResult(IReadOnlyList<string> Created, IReadOnlyList<string> Skipped);
