using EstherLink.Backend.Configuration;
using EstherLink.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Services.Commerce;

public sealed class DownloadCatalogService : IDownloadCatalogService
{
    private readonly AppDbContext _dbContext;
    private readonly IOptions<WebOptions> _options;

    public DownloadCatalogService(AppDbContext dbContext, IOptions<WebOptions> options)
    {
        _dbContext = dbContext;
        _options = options;
    }

    public async Task<DownloadCatalogItem?> GetLatestAsync(string? channel, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(channel)
            ? _options.Value.DownloadChannel
            : channel;

        normalized = normalized.Trim().ToLowerInvariant();

        var release = await _dbContext.AppReleases
            .AsNoTracking()
            .Where(x => x.Channel == normalized)
            .OrderByDescending(x => x.PublishedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (release is null)
        {
            return null;
        }

        return new DownloadCatalogItem(
            release.Version,
            release.Channel,
            release.PublishedAt,
            release.DownloadUrl,
            release.Sha256,
            release.Notes);
    }
}