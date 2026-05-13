using OmniRelay.Backend.Data.Enums;

namespace OmniRelay.Backend.Data.Entities;

public sealed class NewsletterClientEntity
{
    public Guid Id { get; set; }
    public Guid NewsletterId { get; set; }
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public NewsletterClientState State { get; set; } = NewsletterClientState.Pending;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? VisitedAt { get; set; }
    public string? Error { get; set; }
    public int SendAttempts { get; set; }

    public NewsletterEntity Newsletter { get; set; } = null!;
    public Models.ApplicationUser User { get; set; } = null!;
}
