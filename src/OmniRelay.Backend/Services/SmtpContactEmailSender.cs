namespace OmniRelay.Backend.Services;

public sealed class SmtpContactEmailSender : IContactEmailSender
{
    private readonly IEmailDeliveryService _emailDeliveryService;

    public SmtpContactEmailSender(IEmailDeliveryService emailDeliveryService)
    {
        _emailDeliveryService = emailDeliveryService;
    }

    public async Task SendAsync(ContactEmailMessage message, CancellationToken cancellationToken)
    {
        Validate(message);
        await _emailDeliveryService.SendAsync(
            new EmailDeliveryMessage(
                message.RecipientEmail,
                $"[OmniRelay Contact] {message.Subject}",
                BuildBody(message),
                ReplyToEmail: message.SenderEmail,
                ReplyToName: message.SenderName),
            cancellationToken);
    }

    private static string BuildBody(ContactEmailMessage message)
    {
        return
            $"Name: {message.SenderName}\n" +
            $"Email: {message.SenderEmail}\n" +
            $"SubmittedAtUtc: {DateTimeOffset.UtcNow:O}\n\n" +
            $"{message.Message}";
    }

    private static void Validate(ContactEmailMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.RecipientEmail))
        {
            throw new InvalidOperationException("Support email recipient is not configured.");
        }
    }
}
