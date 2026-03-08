namespace OmniRelay.Backend.Services;

public interface IContactEmailSender
{
    Task SendAsync(ContactEmailMessage message, CancellationToken cancellationToken);
}

public sealed record ContactEmailMessage(
    string SenderName,
    string SenderEmail,
    string Subject,
    string Message,
    string RecipientEmail);
