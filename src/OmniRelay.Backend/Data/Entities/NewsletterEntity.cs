using OmniRelay.Backend.Data.Enums;

namespace OmniRelay.Backend.Data.Entities;

public sealed class NewsletterEntity
{
    public Guid Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public NewsletterState State { get; set; } = NewsletterState.Pending;
    public int TotalUsers { get; set; }
    public int TotalVisited { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }

    public ICollection<NewsletterClientEntity> Clients { get; set; } = [];
}
