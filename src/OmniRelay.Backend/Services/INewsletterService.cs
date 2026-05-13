using OmniRelay.Backend.Data.Enums;

namespace OmniRelay.Backend.Services;

public sealed record NewsletterCampaignView(
    Guid Id,
    string Version,
    NewsletterState State,
    int TotalUsers,
    int TotalVisited,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? LastError);

public sealed record NewsletterCreateResult(bool Success, string Message);

public interface INewsletterService
{
    Task<NewsletterCreateResult> CreateLatestCampaignAsync(CancellationToken cancellationToken);
    Task<NewsletterCreateResult> CreateLatestCampaignForEmailAsync(string email, CancellationToken cancellationToken);
    Task<IReadOnlyList<NewsletterCampaignView>> ListCampaignsAsync(CancellationToken cancellationToken);
}
