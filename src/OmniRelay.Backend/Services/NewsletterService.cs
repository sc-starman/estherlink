using System.Text;
using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace OmniRelay.Backend.Services;

public sealed class NewsletterService : INewsletterService
{
    private const string TemplateFileName = "newsletterTemplate.md";
    private readonly AppDbContext _dbContext;
    private readonly INewsletterContentProvider _contentProvider;
    private readonly string _trackingBaseUrl;
    private readonly ILogger<NewsletterService> _logger;
    private readonly string _templatePath;

    public NewsletterService(
        AppDbContext dbContext,
        INewsletterContentProvider contentProvider,
        IConfiguration configuration,
        ILogger<NewsletterService> logger)
    {
        _dbContext = dbContext;
        _contentProvider = contentProvider;
        _logger = logger;
        var configuredDomain = configuration["OMNIRELAY_DOMAIN"]?.Trim();
        _trackingBaseUrl = string.IsNullOrWhiteSpace(configuredDomain)
            ? "https://omnirelay.net"
            : (configuredDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               configuredDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ? configuredDomain.TrimEnd('/')
                : $"https://{configuredDomain.TrimEnd('/')}";
        _templatePath = Path.Combine(AppContext.BaseDirectory, TemplateFileName);
    }

    public async Task<NewsletterCreateResult> CreateLatestCampaignAsync(CancellationToken cancellationToken)
    {
        var runningGuard = await EnsureNoRunningCampaignAsync(cancellationToken);
        if (!runningGuard.Success)
        {
            return runningGuard;
        }

        var snapshot = _contentProvider.GetLatest();
        var now = DateTimeOffset.UtcNow;
        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(x => x.EmailConfirmed && x.Email != null && x.Email != string.Empty)
            .Select(x => new { x.Id, x.Email })
            .ToListAsync(cancellationToken);

        var campaign = new NewsletterEntity
        {
            Id = Guid.NewGuid(),
            Version = snapshot.Version,
            State = NewsletterState.Pending,
            TotalUsers = users.Count,
            TotalVisited = 0,
            CreatedAt = now
        };

        _dbContext.Newsletters.Add(campaign);

        foreach (var user in users)
        {
            _dbContext.NewsletterClients.Add(new NewsletterClientEntity
            {
                Id = Guid.NewGuid(),
                NewsletterId = campaign.Id,
                UserId = user.Id,
                Email = user.Email!,
                State = NewsletterClientState.Pending,
                SendAttempts = 0
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new NewsletterCreateResult(true, $"Newsletter queued for {users.Count} users.");
    }

    public async Task<NewsletterCreateResult> CreateLatestCampaignForEmailAsync(string email, CancellationToken cancellationToken)
    {
        var runningGuard = await EnsureNoRunningCampaignAsync(cancellationToken);
        if (!runningGuard.Success)
        {
            return runningGuard;
        }

        var normalizedEmail = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return new NewsletterCreateResult(false, "Email is required.");
        }

        var user = await _dbContext.Users
            .AsNoTracking()
            .Where(x => x.EmailConfirmed && x.Email != null)
            .Select(x => new { x.Id, x.Email })
            .FirstOrDefaultAsync(x => x.Email == normalizedEmail, cancellationToken);

        if (user is null)
        {
            return new NewsletterCreateResult(false, "Confirmed user with this email was not found.");
        }

        var snapshot = _contentProvider.GetLatest();
        var now = DateTimeOffset.UtcNow;
        var campaign = new NewsletterEntity
        {
            Id = Guid.NewGuid(),
            Version = snapshot.Version,
            State = NewsletterState.Pending,
            TotalUsers = 1,
            TotalVisited = 0,
            CreatedAt = now
        };

        _dbContext.Newsletters.Add(campaign);
        _dbContext.NewsletterClients.Add(new NewsletterClientEntity
        {
            Id = Guid.NewGuid(),
            NewsletterId = campaign.Id,
            UserId = user.Id,
            Email = user.Email!,
            State = NewsletterClientState.Pending,
            SendAttempts = 0
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new NewsletterCreateResult(true, $"Newsletter queued for {normalizedEmail}.");
    }

    public async Task<IReadOnlyList<NewsletterCampaignView>> ListCampaignsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Newsletters
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new NewsletterCampaignView(
                x.Id,
                x.Version,
                x.State,
                x.TotalUsers,
                x.TotalVisited,
                x.CreatedAt,
                x.StartedAt,
                x.CompletedAt,
                x.LastError))
            .ToListAsync(cancellationToken);
    }

    public static string BuildSubject(string version)
        => $"OmniRelay {version} is live - Multi-Relay & Multi-Protocol";

    public string BuildBody(string recipientEmail, Guid newsletterClientId)
    {
        try
        {
            var template = File.ReadAllText(_templatePath);
            return template
                .Replace("{{customer_email}}", recipientEmail, StringComparison.Ordinal)
                .Replace("{{newsletterClientId}}", newsletterClientId.ToString("D"), StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to read newsletter template from {TemplatePath}", _templatePath);
            return $"Hi {recipientEmail},{Environment.NewLine}{Environment.NewLine}https://omnirelay.net/nl/{newsletterClientId:D}";
        }
    }

    public string BuildTrackedLink(Guid newsletterClientId)
    {
        return $"{_trackingBaseUrl}/nl/{newsletterClientId:D}";
    }

    private async Task<NewsletterCreateResult> EnsureNoRunningCampaignAsync(CancellationToken cancellationToken)
    {
        var hasRunning = await _dbContext.Newsletters
            .AsNoTracking()
            .AnyAsync(x => x.State == NewsletterState.Running, cancellationToken);
        if (hasRunning)
        {
            return new NewsletterCreateResult(false, "A newsletter campaign is currently running.");
        }

        return new NewsletterCreateResult(true, string.Empty);
    }
}
