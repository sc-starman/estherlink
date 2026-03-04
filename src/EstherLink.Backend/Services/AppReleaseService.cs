using EstherLink.Backend.Contracts.App;
using EstherLink.Backend.Data;
using EstherLink.Backend.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NuGet.Versioning;

namespace EstherLink.Backend.Services;

public sealed class AppReleaseService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<AppReleaseService> _logger;

    public AppReleaseService(AppDbContext dbContext, ILogger<AppReleaseService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<AppLatestResponse?> GetLatestAsync(string channel, string? currentVersion, CancellationToken cancellationToken)
    {
        var normalizedChannel = (channel ?? "stable").Trim().ToLowerInvariant();
        var releases = await _dbContext.AppReleases.AsNoTracking()
            .Where(x => x.Channel == normalizedChannel)
            .ToListAsync(cancellationToken);

        if (releases.Count == 0)
        {
            return null;
        }

        var latest = releases
            .Select(x => new
            {
                Release = x,
                Parsed = ParseVersionSafe(x.Version)
            })
            .Where(x => x.Parsed is not null)
            .OrderByDescending(x => x.Parsed)
            .ThenByDescending(x => x.Release.PublishedAt)
            .First()
            .Release;

        var latestParsed = ParseVersionSafe(latest.Version);
        var currentParsed = ParseVersionSafe(currentVersion);
        var updateAvailable = latestParsed is not null && (currentParsed is null || latestParsed > currentParsed);

        return new AppLatestResponse
        {
            UpdateAvailable = updateAvailable,
            LatestVersion = latest.Version,
            MinSupportedVersion = latest.MinSupportedVersion,
            DownloadUrl = latest.DownloadUrl,
            Sha256 = latest.Sha256,
            Notes = latest.Notes,
            PublishedAt = latest.PublishedAt
        };
    }

    public async Task<AppReleaseEntity> CreateReleaseAsync(AdminCreateReleaseRequest request, CancellationToken cancellationToken)
    {
        if (ParseVersionSafe(request.Version) is null)
        {
            throw new InvalidOperationException("Version must be valid semver.");
        }

        if (ParseVersionSafe(request.MinSupportedVersion) is null)
        {
            throw new InvalidOperationException("MinSupportedVersion must be valid semver.");
        }

        var release = new AppReleaseEntity
        {
            Id = Guid.NewGuid(),
            Channel = request.Channel.Trim().ToLowerInvariant(),
            Version = request.Version.Trim(),
            PublishedAt = request.PublishedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow,
            Notes = request.Notes.Trim(),
            DownloadUrl = request.DownloadUrl.Trim(),
            Sha256 = request.Sha256.Trim().ToLowerInvariant(),
            MinSupportedVersion = request.MinSupportedVersion.Trim()
        };

        _dbContext.AppReleases.Add(release);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "App release created channel={Channel} version={Version}",
            release.Channel,
            release.Version);
        return release;
    }

    private static NuGetVersion? ParseVersionSafe(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return NuGetVersion.TryParse(version, out var parsed) ? parsed : null;
    }
}
