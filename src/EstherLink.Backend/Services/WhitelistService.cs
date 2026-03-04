using EstherLink.Backend.Contracts.Whitelist;
using EstherLink.Backend.Data;
using EstherLink.Backend.Data.Entities;
using EstherLink.Backend.Utilities;
using Microsoft.EntityFrameworkCore;

namespace EstherLink.Backend.Services;

public sealed class WhitelistService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<WhitelistService> _logger;

    public WhitelistService(AppDbContext dbContext, ILogger<WhitelistService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WhitelistSetSummaryResponse>> GetLatestSummariesAsync(
        string? countryCode,
        string? category,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.WhitelistSets.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            var normalized = countryCode.Trim().ToUpperInvariant();
            query = query.Where(x => x.CountryCode == normalized);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var normalized = category.Trim();
            query = query.Where(x => x.Category == normalized);
        }

        var rows = await query
            .OrderByDescending(x => x.Version)
            .ToListAsync(cancellationToken);

        var latestRows = rows
            .GroupBy(x => x.SetGroupId)
            .Select(x => x.OrderByDescending(v => v.Version).First())
            .OrderBy(x => x.CountryCode)
            .ThenBy(x => x.Name)
            .ToList();

        var latestIds = latestRows.Select(x => x.Id).ToArray();
        var entryCounts = await _dbContext.WhitelistEntries
            .Where(x => latestIds.Contains(x.WhitelistSetId))
            .GroupBy(x => x.WhitelistSetId)
            .Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return latestRows.Select(x => new WhitelistSetSummaryResponse
        {
            SetId = x.SetGroupId,
            CountryCode = x.CountryCode,
            Name = x.Name,
            Category = x.Category,
            LatestVersion = x.Version,
            Sha256 = x.Sha256,
            EntryCount = entryCounts.GetValueOrDefault(x.Id),
            UpdatedAt = x.CreatedAt
        }).ToList();
    }

    public async Task<WhitelistLatestResponse?> GetLatestAsync(Guid setId, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.WhitelistSets.AsNoTracking()
            .Where(x => x.SetGroupId == setId)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            return null;
        }

        var entries = await _dbContext.WhitelistEntries.AsNoTracking()
            .Where(x => x.WhitelistSetId == latest.Id)
            .OrderBy(x => x.Cidr)
            .Select(x => x.Cidr)
            .ToListAsync(cancellationToken);

        return new WhitelistLatestResponse
        {
            SetId = latest.SetGroupId,
            CountryCode = latest.CountryCode,
            Name = latest.Name,
            Category = latest.Category,
            Version = latest.Version,
            Sha256 = latest.Sha256,
            Entries = entries,
            UpdatedAt = latest.CreatedAt
        };
    }

    public async Task<WhitelistDiffResponse?> GetDiffAsync(Guid setId, int fromVersion, CancellationToken cancellationToken)
    {
        var from = await _dbContext.WhitelistSets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SetGroupId == setId && x.Version == fromVersion, cancellationToken);

        var latest = await _dbContext.WhitelistSets.AsNoTracking()
            .Where(x => x.SetGroupId == setId)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (from is null || latest is null)
        {
            return null;
        }

        var fromSet = (await _dbContext.WhitelistEntries.AsNoTracking()
                .Where(x => x.WhitelistSetId == from.Id)
                .Select(x => x.Cidr)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toSet = (await _dbContext.WhitelistEntries.AsNoTracking()
                .Where(x => x.WhitelistSetId == latest.Id)
                .Select(x => x.Cidr)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new WhitelistDiffResponse
        {
            SetId = setId,
            FromVersion = from.Version,
            ToVersion = latest.Version,
            Added = toSet.Except(fromSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(),
            Removed = fromSet.Except(toSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray()
        };
    }

    public async Task<WhitelistLatestResponse?> PublishAsync(
        Guid setId,
        AdminPublishWhitelistRequest request,
        CancellationToken cancellationToken)
    {
        var latest = await _dbContext.WhitelistSets
            .Where(x => x.SetGroupId == setId)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var normalized = NormalizeEntries(request.Entries);
        var hash = Sha256Util.HashHex(string.Join('\n', normalized));

        var newVersion = new WhitelistSetEntity
        {
            Id = Guid.NewGuid(),
            SetGroupId = latest.SetGroupId,
            CountryCode = latest.CountryCode,
            Name = latest.Name,
            Category = latest.Category,
            Version = latest.Version + 1,
            Sha256 = hash,
            CreatedAt = now
        };

        _dbContext.WhitelistSets.Add(newVersion);
        _dbContext.WhitelistEntries.AddRange(normalized.Select(cidr => new WhitelistEntryEntity
        {
            Id = Guid.NewGuid(),
            WhitelistSetId = newVersion.Id,
            Cidr = cidr,
            Note = request.Note
        }));

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Whitelist published setId={SetId} version={Version} entries={EntryCount}",
            setId,
            newVersion.Version,
            normalized.Count);
        return await GetLatestAsync(setId, cancellationToken);
    }

    public async Task<WhitelistLatestResponse> CreateSetAsync(
        AdminCreateWhitelistSetRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedCountry = request.CountryCode.Trim().ToUpperInvariant();
        var setId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var normalizedEntries = NormalizeEntries(request.Entries);
        var hash = Sha256Util.HashHex(string.Join('\n', normalizedEntries));

        var set = new WhitelistSetEntity
        {
            Id = Guid.NewGuid(),
            SetGroupId = setId,
            CountryCode = normalizedCountry,
            Name = request.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            Version = 1,
            Sha256 = hash,
            CreatedAt = now
        };

        _dbContext.WhitelistSets.Add(set);
        _dbContext.WhitelistEntries.AddRange(normalizedEntries.Select(cidr => new WhitelistEntryEntity
        {
            Id = Guid.NewGuid(),
            WhitelistSetId = set.Id,
            Cidr = cidr
        }));

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Whitelist set created setId={SetId} version=1 entries={EntryCount}",
            setId,
            normalizedEntries.Count);

        return new WhitelistLatestResponse
        {
            SetId = setId,
            CountryCode = set.CountryCode,
            Name = set.Name,
            Category = set.Category,
            Version = set.Version,
            Sha256 = set.Sha256,
            Entries = normalizedEntries,
            UpdatedAt = set.CreatedAt
        };
    }

    private static IReadOnlyList<string> NormalizeEntries(IReadOnlyList<string> entries)
    {
        var normalized = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (CidrNormalizer.TryNormalize(raw, out var cidr, out var error))
            {
                normalized.Add(cidr);
            }
            else
            {
                errors.Add(error ?? $"Invalid entry: {raw}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", errors));
        }

        return normalized.ToList();
    }
}
