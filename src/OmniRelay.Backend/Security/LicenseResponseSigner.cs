using System.Text;
using OmniRelay.Backend.Contracts.Licensing;
using OmniRelay.Backend.Services;

namespace OmniRelay.Backend.Security;

public sealed class LicenseResponseSigner
{
    private readonly SigningKeyService _signingKeyService;

    public LicenseResponseSigner(SigningKeyService signingKeyService)
    {
        _signingKeyService = signingKeyService;
    }

    public async Task<(string Signature, string KeyId)> SignAsync(
        LicenseVerifyResponse response,
        string nonce,
        CancellationToken cancellationToken)
    {
        var key = await _signingKeyService.GetActiveSigningKeyAsync(cancellationToken);
        if (key is null)
        {
            throw new InvalidOperationException("No active signing key is available.");
        }

        response.KeyId = key.KeyId;
        var payload = BuildPayload(response, nonce);
        var signature = _signingKeyService.Sign(payload, key);
        return (signature, key.KeyId);
    }

    public static string BuildPayload(LicenseVerifyResponse response, string nonce)
    {
        return
            $"valid={response.Valid};" +
            $"reason={response.Reason};" +
            $"transferRequired={response.TransferRequired};" +
            $"activeDeviceIdHint={response.ActiveDeviceIdHint ?? string.Empty};" +
            $"transferLimitPerRollingYear={response.TransferLimitPerRollingYear};" +
            $"transfersUsedInWindow={response.TransfersUsedInWindow};" +
            $"transfersRemainingInWindow={response.TransfersRemainingInWindow};" +
            $"transferWindowStartAt={Format(response.TransferWindowStartAt)};" +
            $"plan={response.Plan ?? string.Empty};" +
            $"licenseExpiresAt={Format(response.LicenseExpiresAt)};" +
            $"cacheExpiresAt={Format(response.CacheExpiresAt)};" +
            $"serverTime={Format(response.ServerTime)};" +
            $"requestId={response.RequestId};" +
            $"signatureAlg={response.SignatureAlg};" +
            $"keyId={response.KeyId};" +
            $"nonce={nonce}";
    }

    private static string Format(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString("O") ?? string.Empty;
    }
}
