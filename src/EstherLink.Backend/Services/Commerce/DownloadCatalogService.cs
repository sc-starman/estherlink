using EstherLink.Backend.Data;
using Microsoft.EntityFrameworkCore;
using NuGet.Versioning;

namespace EstherLink.Backend.Services.Commerce;

public sealed class DownloadCatalogService : IDownloadCatalogService
{
    private const string DefaultDownloadChannel = "stable";
    private readonly AppDbContext _dbContext;

    public DownloadCatalogService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DownloadCatalogItem?> GetLatestAsync(string? channel, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(channel)
            ? DefaultDownloadChannel
            : channel;

        normalized = normalized.Trim().ToLowerInvariant();

        var release = await _dbContext.AppReleases
            .AsNoTracking()
            .Where(x => x.Channel == normalized)
            .ToListAsync(cancellationToken);

        if (release.Count == 0)
        {
            return null;
        }

        var latest = release
            .Select(x => new
            {
                Release = x,
                ParsedVersion = NuGetVersion.TryParse(x.Version, out var parsed) ? parsed : null
            })
            .OrderByDescending(x => x.ParsedVersion is not null)
            .ThenByDescending(x => x.ParsedVersion)
            .ThenByDescending(x => x.Release.PublishedAt)
            .First()
            .Release;

        return new DownloadCatalogItem(
            latest.Version,
            latest.Channel,
            latest.PublishedAt,
            latest.DownloadUrl,
            latest.Sha256,
            latest.Notes);
    }
}
