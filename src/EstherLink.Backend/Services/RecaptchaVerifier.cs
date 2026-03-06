using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EstherLink.Backend.Configuration;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Services;

public sealed class RecaptchaVerifier : IRecaptchaVerifier
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<SpamProtectionOptions> _spamOptions;
    private readonly ILogger<RecaptchaVerifier> _logger;

    public RecaptchaVerifier(
        HttpClient httpClient,
        IOptions<SpamProtectionOptions> spamOptions,
        ILogger<RecaptchaVerifier> logger)
    {
        _httpClient = httpClient;
        _spamOptions = spamOptions;
        _logger = logger;
    }

    public async Task<RecaptchaVerificationResult> VerifyAsync(
        string token,
        string? remoteIp,
        CancellationToken cancellationToken,
        string? expectedAction = null)
    {
        var options = _spamOptions.Value;
        if (!options.EnableRecaptcha)
        {
            return new RecaptchaVerificationResult(true);
        }

        if (string.IsNullOrWhiteSpace(options.RecaptchaSecretKey))
        {
            return new RecaptchaVerificationResult(false, "reCAPTCHA secret key is missing.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return new RecaptchaVerificationResult(false, "reCAPTCHA token is required.");
        }

        var payload = new Dictionary<string, string>
        {
            ["secret"] = options.RecaptchaSecretKey,
            ["response"] = token.Trim()
        };

        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            payload["remoteip"] = remoteIp;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, options.RecaptchaVerifyUrl)
        {
            Content = new FormUrlEncodedContent(payload)
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new RecaptchaVerificationResult(false, $"reCAPTCHA verify failed with status {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await JsonSerializer.DeserializeAsync<RecaptchaResponse>(stream, cancellationToken: cancellationToken);
        if (parsed is null || !parsed.Success)
        {
            var error = parsed?.ErrorCodes?.Length > 0
                ? string.Join(", ", parsed.ErrorCodes)
                : "reCAPTCHA validation failed.";
            return new RecaptchaVerificationResult(false, error);
        }

        var expected = string.IsNullOrWhiteSpace(expectedAction)
            ? options.RecaptchaExpectedAction
            : expectedAction;

        if (!string.IsNullOrWhiteSpace(expected) &&
            !string.Equals(expected, parsed.Action, StringComparison.Ordinal))
        {
            return new RecaptchaVerificationResult(false, "reCAPTCHA action mismatch.");
        }

        if (parsed.Score is double score && score < options.RecaptchaMinimumScore)
        {
            return new RecaptchaVerificationResult(false, $"reCAPTCHA score too low ({score.ToString("0.00", CultureInfo.InvariantCulture)}).");
        }

        _logger.LogDebug("reCAPTCHA verification succeeded. action={Action} score={Score}", parsed.Action, parsed.Score);
        return new RecaptchaVerificationResult(true);
    }

    private sealed class RecaptchaResponse
    {
        public bool Success { get; set; }
        public double? Score { get; set; }
        public string Action { get; set; } = string.Empty;
        [JsonPropertyName("error-codes")]
        public string[]? ErrorCodes { get; set; }
    }
}
