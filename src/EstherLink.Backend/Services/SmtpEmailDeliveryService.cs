using System.Net;
using System.Net.Mail;
using EstherLink.Backend.Configuration;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Services;

public sealed class SmtpEmailDeliveryService : IEmailDeliveryService
{
    private readonly IOptions<SmtpOptions> _smtpOptions;

    public SmtpEmailDeliveryService(IOptions<SmtpOptions> smtpOptions)
    {
        _smtpOptions = smtpOptions;
    }

    public async Task SendAsync(EmailDeliveryMessage message, CancellationToken cancellationToken)
    {
        var options = _smtpOptions.Value;
        Validate(options, message);

        using var mail = new MailMessage
        {
            From = new MailAddress(options.FromEmail, options.FromName),
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = false
        };

        mail.To.Add(new MailAddress(message.ToEmail, message.ToName));

        if (!string.IsNullOrWhiteSpace(message.ReplyToEmail))
        {
            mail.ReplyToList.Add(new MailAddress(message.ReplyToEmail, message.ReplyToName));
        }

        using var smtp = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };

        if (options.RequireAuthentication)
        {
            smtp.Credentials = new NetworkCredential(options.Username, options.Password);
        }
        else
        {
            smtp.Credentials = CredentialCache.DefaultNetworkCredentials;
        }

        await smtp.SendMailAsync(mail, cancellationToken);
    }

    private static void Validate(SmtpOptions options, EmailDeliveryMessage message)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new InvalidOperationException("SMTP host is not configured.");
        }

        if (options.Port <= 0 || options.Port > 65535)
        {
            throw new InvalidOperationException("SMTP port is invalid.");
        }

        if (string.IsNullOrWhiteSpace(options.FromEmail))
        {
            throw new InvalidOperationException("SMTP from-email is not configured.");
        }

        if (options.RequireAuthentication &&
            (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password)))
        {
            throw new InvalidOperationException("SMTP credentials are not configured.");
        }

        if (string.IsNullOrWhiteSpace(message.ToEmail))
        {
            throw new InvalidOperationException("Recipient email is required.");
        }
    }
}
