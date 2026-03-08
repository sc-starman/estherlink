namespace OmniRelay.Backend.Services;

public interface IEmailDeliveryService
{
    Task SendAsync(EmailDeliveryMessage message, CancellationToken cancellationToken);
}

public sealed record EmailDeliveryMessage(
    string ToEmail,
    string Subject,
    string Body,
    string? ToName = null,
    string? ReplyToEmail = null,
    string? ReplyToName = null);
