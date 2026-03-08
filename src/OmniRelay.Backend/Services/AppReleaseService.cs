using OmniRelay.Backend.Contracts.App;
using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NuGet.Versioning;

namespace OmniRelay.Backend.Services;

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

    public async Task<AppReleaseEntity> UpsertReleaseAsync(
        string channel,
        string version,
        string minSupportedVersion,
        string downloadUrl,
        string sha256,
        string notes,
        DateTimeOffset? publishedAt,
        CancellationToken cancellationToken)
    {
        var normalizedChannel = (channel ?? "stable").Trim().ToLowerInvariant();
        var normalizedVersion = version.Trim();
        var normalizedMinSupportedVersion = minSupportedVersion.Trim();
        var normalizedDownloadUrl = downloadUrl.Trim();
        var normalizedSha256 = sha256.Trim().ToLowerInvariant();
        var normalizedNotes = notes?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedChannel))
        {
            throw new InvalidOperationException("Channel is required.");
        }

        if (ParseVersionSafe(normalizedVersion) is null)
        {
            throw new InvalidOperationException("Version must be valid semver.");
        }

        if (ParseVersionSafe(normalizedMinSupportedVersion) is null)
        {
            throw new InvalidOperationException("MinSupportedVersion must be valid semver.");
        }

        if (string.IsNullOrWhiteSpace(normalizedDownloadUrl))
        {
            throw new InvalidOperationException("DownloadUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedSha256))
        {
            throw new InvalidOperationException("Sha256 is required.");
        }

        var release = await _dbContext.AppReleases
            .FirstOrDefaultAsync(
                x => x.Channel == normalizedChannel && x.Version == normalizedVersion,
                cancellationToken);

        if (release is null)
        {
            release = new AppReleaseEntity
            {
                Id = Guid.NewGuid(),
                Channel = normalizedChannel,
                Version = normalizedVersion,
                MinSupportedVersion = normalizedMinSupportedVersion,
                DownloadUrl = normalizedDownloadUrl,
                Sha256 = normalizedSha256,
                Notes = normalizedNotes,
                PublishedAt = publishedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow
            };

            _dbContext.AppReleases.Add(release);
            _logger.LogInformation(
                "App release created via upsert channel={Channel} version={Version}",
                release.Channel,
                release.Version);
        }
        else
        {
            release.MinSupportedVersion = normalizedMinSupportedVersion;
            release.DownloadUrl = normalizedDownloadUrl;
            release.Sha256 = normalizedSha256;
            release.Notes = normalizedNotes;
            release.PublishedAt = publishedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "App release updated via upsert channel={Channel} version={Version}",
                release.Channel,
                release.Version);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
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
