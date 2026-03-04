using System.Security.Cryptography;
using System.Text;
using EstherLink.Backend.Configuration;
using EstherLink.Backend.Contracts.Licensing;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Security;

public sealed class LicenseResponseSigner
{
    private readonly IOptions<LicensingOptions> _options;

    public LicenseResponseSigner(IOptions<LicensingOptions> options)
    {
        _options = options;
    }

    public string Sign(LicenseVerifyResponse response, string nonce)
    {
        var secret = _options.Value.SigningSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Licensing:SigningSecret is required.");
        }

        var payload =
            $"valid={response.Valid};" +
            $"reason={response.Reason};" +
            $"plan={response.Plan ?? string.Empty};" +
            $"licenseExpiresAt={Format(response.LicenseExpiresAt)};" +
            $"cacheExpiresAt={Format(response.CacheExpiresAt)};" +
            $"serverTime={Format(response.ServerTime)};" +
            $"nonce={nonce}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(signatureBytes);
    }

    private static string Format(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString("O") ?? string.Empty;
    }
}
