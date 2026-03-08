using System.Net;
using System.Net.Http.Json;
using OmniRelay.Backend.Configuration;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.Services;

public sealed class MailServiceEmailDeliveryService : IEmailDeliveryService
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<MailServiceOptions> _mailServiceOptions;
    private readonly ILogger<MailServiceEmailDeliveryService> _logger;

    public MailServiceEmailDeliveryService(
        HttpClient httpClient,
        IOptions<MailServiceOptions> mailServiceOptions,
        ILogger<MailServiceEmailDeliveryService> logger)
    {
        _httpClient = httpClient;
        _mailServiceOptions = mailServiceOptions;
        _logger = logger;
    }

    public async Task SendAsync(EmailDeliveryMessage message, CancellationToken cancellationToken)
    {
        var options = _mailServiceOptions.Value;
        Validate(options, message);

        var maxAttempts = Math.Clamp(options.RetryCount + 1, 1, 5);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await SendOnceAsync(options, message, cancellationToken);
                _logger.LogInformation(
                    "Mail-service email sent successfully. baseUrl={BaseUrl} to={ToEmail} attempt={Attempt}/{MaxAttempts}",
                    options.BaseUrl,
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
                    "Mail-service send transient failure on attempt {Attempt}/{MaxAttempts}. baseUrl={BaseUrl} to={ToEmail}",
                    attempt,
                    maxAttempts,
                    options.BaseUrl,
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
            $"Mail-service delivery failed for recipient '{message.ToEmail}' via {options.BaseUrl}{options.SendPath}. {lastError?.Message}",
            lastError);
    }

    private async Task SendOnceAsync(MailServiceOptions options, EmailDeliveryMessage message, CancellationToken callerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(options));
        request.Headers.TryAddWithoutValidation(options.ApiKeyHeader, options.ApiKey);
        request.Content = JsonContent.Create(new MailServiceSendRequest(
            message.ToEmail,
            message.Subject,
            BuildHtmlBody(message.Body)));

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeoutCts.Token);

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            linkedCts.Token);

        try
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var errorBody = await ReadResponseBodySafeAsync(response, linkedCts.Token);
            var excerpt = TruncateForLog(errorBody);
            throw new HttpRequestException(
                $"Mail-service responded {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {excerpt}",
                null,
                response.StatusCode);
        }
        catch (TaskCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Mail-service send timed out after {options.TimeoutSeconds}s via {options.BaseUrl}{options.SendPath}.",
                ex);
        }
    }

    private static string BuildRequestUri(MailServiceOptions options)
    {
        var baseUri = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var relativePath = options.SendPath.TrimStart('/');
        return new Uri(baseUri, relativePath).ToString();
    }

    private static string BuildHtmlBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<p></p>";
        }

        var encoded = WebUtility.HtmlEncode(body.Trim());
        return $"<p>{encoded.Replace("\r\n", "<br/>").Replace("\n", "<br/>")}</p>";
    }

    private static async Task<string> ReadResponseBodySafeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static string TruncateForLog(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "<empty>";
        }

        const int maxLength = 512;
        return input.Length <= maxLength ? input : input[..maxLength] + "...";
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException or TaskCanceledException)
        {
            return true;
        }

        if (ex is HttpRequestException httpEx)
        {
            return httpEx.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
        }

        return false;
    }

    private static void Validate(MailServiceOptions options, EmailDeliveryMessage message)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException("MailService base URL is not configured.");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("MailService base URL must be a valid absolute http/https URL.");
        }

        if (string.IsNullOrWhiteSpace(options.SendPath))
        {
            throw new InvalidOperationException("MailService send path is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKeyHeader))
        {
            throw new InvalidOperationException("MailService API key header is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("MailService API key is not configured.");
        }

        if (options.TimeoutSeconds < 5 || options.TimeoutSeconds > 300)
        {
            throw new InvalidOperationException("MailService timeout must be between 5 and 300 seconds.");
        }

        if (options.RetryCount < 0 || options.RetryCount > 4)
        {
            throw new InvalidOperationException("MailService retry count must be between 0 and 4.");
        }

        if (string.IsNullOrWhiteSpace(message.ToEmail))
        {
            throw new InvalidOperationException("Recipient email is required.");
        }

        if (string.IsNullOrWhiteSpace(message.Subject))
        {
            throw new InvalidOperationException("Message subject is required.");
        }
    }

    private sealed record MailServiceSendRequest(string To, string Subject, string Html);
}
