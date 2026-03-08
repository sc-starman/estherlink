using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using OmniRelay.Backend.Configuration;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.Services;

public sealed class SmtpEmailDeliveryService : IEmailDeliveryService
{
    private readonly IOptions<SmtpOptions> _smtpOptions;
    private readonly ILogger<SmtpEmailDeliveryService> _logger;

    public SmtpEmailDeliveryService(
        IOptions<SmtpOptions> smtpOptions,
        ILogger<SmtpEmailDeliveryService> logger)
    {
        _smtpOptions = smtpOptions;
        _logger = logger;
    }

    public async Task SendAsync(EmailDeliveryMessage message, CancellationToken cancellationToken)
    {
        var options = _smtpOptions.Value;
        Validate(options, message);

        var maxAttempts = Math.Clamp(options.RetryCount + 1, 1, 5);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await SendOnceAsync(options, message, cancellationToken);
                _logger.LogInformation(
                    "SMTP email sent successfully. host={Host} port={Port} to={ToEmail} attempt={Attempt}/{MaxAttempts}",
                    options.Host,
                    options.Port,
                    message.ToEmail,
                    attempt,
                    maxAttempts);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                lastError = ex;
                _logger.LogWarning(
                    ex,
                    "SMTP send transient failure on attempt {Attempt}/{MaxAttempts}. host={Host} port={Port} to={ToEmail}",
                    attempt,
                    maxAttempts,
                    options.Host,
                    options.Port,
                    message.ToEmail);
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        throw new InvalidOperationException(
            $"SMTP delivery failed for recipient '{message.ToEmail}' via {options.Host}:{options.Port}. {lastError?.Message}",
            lastError);
    }

    private static async Task SendOnceAsync(SmtpOptions options, EmailDeliveryMessage message, CancellationToken callerToken)
    {
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

        using var smtp = BuildClient(options);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, options.SendTimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeoutCts.Token);

        try
        {
            await smtp.SendMailAsync(mail, linkedCts.Token);
        }
        catch (TaskCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"SMTP send timed out after {options.SendTimeoutSeconds}s via {options.Host}:{options.Port}.",
                ex);
        }
    }

    private static SmtpClient BuildClient(SmtpOptions options)
    {
        var smtp = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Timeout = Math.Max(5000, options.SendTimeoutSeconds * 1000)
        };

        if (options.RequireAuthentication)
        {
            smtp.Credentials = new NetworkCredential(options.Username, options.Password);
        }
        else
        {
            smtp.Credentials = CredentialCache.DefaultNetworkCredentials;
        }

        return smtp;
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException or TaskCanceledException)
        {
            return true;
        }

        if (ex is SmtpException smtpEx && smtpEx.InnerException is SocketException)
        {
            return true;
        }

        return false;
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

        if (options.SendTimeoutSeconds < 5 || options.SendTimeoutSeconds > 300)
        {
            throw new InvalidOperationException("SMTP send timeout must be between 5 and 300 seconds.");
        }

        if (options.RetryCount < 0 || options.RetryCount > 4)
        {
            throw new InvalidOperationException("SMTP retry count must be between 0 and 4.");
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
