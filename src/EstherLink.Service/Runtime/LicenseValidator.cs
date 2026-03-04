using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.NaCl;
using EstherLink.Backend.Contracts.Licensing;
using EstherLink.Core.Configuration;
using EstherLink.Core.Licensing;

namespace EstherLink.Service.Runtime;

public sealed class LicenseValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EstherLink.LicenseCache.v2");
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LicenseValidator> _logger;
    private readonly FileLogWriter _fileLog;

    public LicenseValidator(
        IHttpClientFactory httpClientFactory,
        ILogger<LicenseValidator> logger,
        FileLogWriter fileLog)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _fileLog = fileLog;
    }

    public async Task<LicenseValidationResult> ValidateAsync(
        ServiceConfig config,
        bool forceOnline,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var licenseKeyHash = Hash(config.LicenseKey);
        var cache = await ReadCacheAsync(cancellationToken);
        var publicKeys = await ReadPublicKeysCacheAsync(cancellationToken);

        if (!forceOnline &&
            cache is not null &&
            cache.IsValid &&
            cache.LicenseKeyHash == licenseKeyHash &&
            cache.ExpiresAtUtc > now &&
            VerifyCachedEntrySignature(cache, publicKeys))
        {
            return new LicenseValidationResult(
                true,
                true,
                cache.CheckedAtUtc,
                cache.ExpiresAtUtc,
                null,
                cache.Reason,
                cache.RequestId,
                cache.KeyId);
        }

        if (string.IsNullOrWhiteSpace(config.LicenseServerUrl) || string.IsNullOrWhiteSpace(config.LicenseKey))
        {
            return BuildCacheFallback(cache, publicKeys, licenseKeyHash, now, "License key or server endpoint is missing.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(LicenseValidator));
            client.Timeout = TimeSpan.FromSeconds(10);

            var verifyUrl = BuildVerifyUrl(config.LicenseServerUrl);
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

            using var fingerprintDoc = JsonDocument.Parse(
                JsonSerializer.Serialize(new
                {
                    machineName = Environment.MachineName,
                    osVersion = Environment.OSVersion.VersionString
                }));

            var verifyRequest = new LicenseVerifyRequest
            {
                LicenseKey = config.LicenseKey,
                AppVersion = GetAppVersion(),
                Nonce = nonce,
                Fingerprint = fingerprintDoc.RootElement.Clone()
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, verifyUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(verifyRequest, JsonOptions), Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return BuildCacheFallback(
                    cache,
                    publicKeys,
                    licenseKeyHash,
                    now,
                    $"License server HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            var parsed = JsonSerializer.Deserialize<LicenseVerifyResponse>(responseBody, JsonOptions);
            if (parsed is null)
            {
                return BuildCacheFallback(cache, publicKeys, licenseKeyHash, now, "License server returned invalid JSON.");
            }

            if (!string.Equals(parsed.SignatureAlg, "Ed25519", StringComparison.Ordinal))
            {
                return BuildCacheFallback(cache, publicKeys, licenseKeyHash, now, "Unsupported license signature algorithm.");
            }

            var keysResponse = await FetchPublicKeysAsync(client, verifyUrl, cancellationToken);
            if (keysResponse is not null)
            {
                publicKeys = keysResponse;
                await WritePublicKeysCacheAsync(publicKeys, cancellationToken);
            }

            if (!VerifyResponseSignature(parsed, nonce, publicKeys))
            {
                return BuildCacheFallback(cache, publicKeys, licenseKeyHash, now, "License signature verification failed.");
            }

            var cacheExpiresAt = parsed.CacheExpiresAt.ToUniversalTime();
            var newCache = new LicenseCacheEntry(
                parsed.Valid,
                licenseKeyHash,
                now,
                cacheExpiresAt,
                nonce,
                parsed.SignatureAlg,
                parsed.KeyId,
                parsed.Signature,
                parsed.Reason,
                parsed.Plan,
                parsed.LicenseExpiresAt?.ToUniversalTime(),
                parsed.ServerTime.ToUniversalTime(),
                parsed.RequestId);

            await WriteCacheAsync(newCache, cancellationToken);

            if (!parsed.Valid || cacheExpiresAt <= now)
            {
                return new LicenseValidationResult(
                    false,
                    false,
                    now,
                    cacheExpiresAt,
                    parsed.Reason,
                    parsed.Reason,
                    parsed.RequestId,
                    parsed.KeyId);
            }

            _fileLog.Info($"License verified online. keyId={parsed.KeyId} requestId={parsed.RequestId} cacheExpiresAt={cacheExpiresAt:O}");
            return new LicenseValidationResult(
                true,
                false,
                now,
                cacheExpiresAt,
                null,
                parsed.Reason,
                parsed.RequestId,
                parsed.KeyId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "License verification call failed.");
            _fileLog.Warn($"License online verification failed: {ex.Message}");
            return BuildCacheFallback(cache, publicKeys, licenseKeyHash, now, ex.Message);
        }
    }

    private static LicenseValidationResult BuildCacheFallback(
        LicenseCacheEntry? cache,
        LicensePublicKeysResponse? keys,
        string licenseKeyHash,
        DateTimeOffset now,
        string error)
    {
        if (cache is not null &&
            cache.IsValid &&
            cache.LicenseKeyHash == licenseKeyHash &&
            cache.ExpiresAtUtc > now &&
            VerifyCachedEntrySignature(cache, keys))
        {
            return new LicenseValidationResult(
                true,
                true,
                cache.CheckedAtUtc,
                cache.ExpiresAtUtc,
                null,
                cache.Reason,
                cache.RequestId,
                cache.KeyId);
        }

        return new LicenseValidationResult(false, false, now, cache?.ExpiresAtUtc, error, "OFFLINE_FALLBACK_FAILED");
    }

    private static string BuildVerifyUrl(string configuredUrl)
    {
        var trimmed = (configuredUrl ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api/license/verify", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/api/license", StringComparison.OrdinalIgnoreCase))
        {
            return $"{trimmed}/verify";
        }

        return $"{trimmed}/api/license/verify";
    }

    private static string BuildPublicKeysUrl(string verifyUrl)
    {
        return verifyUrl.EndsWith("/verify", StringComparison.OrdinalIgnoreCase)
            ? $"{verifyUrl[..^"/verify".Length]}/public-keys"
            : $"{verifyUrl.TrimEnd('/')}/public-keys";
    }

    private static async Task<LicensePublicKeysResponse?> FetchPublicKeysAsync(
        HttpClient client,
        string verifyUrl,
        CancellationToken cancellationToken)
    {
        var keysUrl = BuildPublicKeysUrl(verifyUrl);
        using var response = await client.GetAsync(keysUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<LicensePublicKeysResponse>(json, JsonOptions);
    }

    private static bool VerifyResponseSignature(
        LicenseVerifyResponse response,
        string nonce,
        LicensePublicKeysResponse? keys)
    {
        if (keys is null || keys.Keys.Count == 0)
        {
            return false;
        }

        var key = keys.Keys.FirstOrDefault(x => string.Equals(x.KeyId, response.KeyId, StringComparison.Ordinal));
        if (key is null)
        {
            return false;
        }

        return VerifySignature(
            response.Valid,
            response.Reason,
            response.Plan,
            response.LicenseExpiresAt?.ToUniversalTime(),
            response.CacheExpiresAt.ToUniversalTime(),
            response.ServerTime.ToUniversalTime(),
            response.RequestId,
            response.SignatureAlg,
            response.KeyId,
            nonce,
            response.Signature,
            key.PublicKey);
    }

    private static bool VerifyCachedEntrySignature(LicenseCacheEntry cache, LicensePublicKeysResponse? keys)
    {
        if (keys is null || keys.Keys.Count == 0)
        {
            return false;
        }

        var key = keys.Keys.FirstOrDefault(x => string.Equals(x.KeyId, cache.KeyId, StringComparison.Ordinal));
        if (key is null)
        {
            return false;
        }

        return VerifySignature(
            cache.IsValid,
            cache.Reason,
            cache.Plan,
            cache.LicenseExpiresAtUtc,
            cache.ExpiresAtUtc,
            cache.ServerTimeUtc,
            cache.RequestId,
            cache.SignatureAlg,
            cache.KeyId,
            cache.Nonce,
            cache.Signature,
            key.PublicKey);
    }

    private static bool VerifySignature(
        bool valid,
        string reason,
        string? plan,
        DateTimeOffset? licenseExpiresAt,
        DateTimeOffset cacheExpiresAt,
        DateTimeOffset serverTime,
        string requestId,
        string signatureAlg,
        string keyId,
        string nonce,
        string signatureBase64,
        string publicKeyBase64)
    {
        if (!string.Equals(signatureAlg, "Ed25519", StringComparison.Ordinal))
        {
            return false;
        }

        var payload =
            $"valid={valid};" +
            $"reason={reason};" +
            $"plan={plan ?? string.Empty};" +
            $"licenseExpiresAt={Format(licenseExpiresAt)};" +
            $"cacheExpiresAt={Format(cacheExpiresAt)};" +
            $"serverTime={Format(serverTime)};" +
            $"requestId={requestId};" +
            $"signatureAlg={signatureAlg};" +
            $"keyId={keyId};" +
            $"nonce={nonce}";

        try
        {
            var signature = Convert.FromBase64String(signatureBase64);
            var publicKey = Convert.FromBase64String(publicKeyBase64);
            var message = Encoding.UTF8.GetBytes(payload);
            return Ed25519.Verify(signature, message, publicKey);
        }
        catch
        {
            return false;
        }
    }

    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    }

    private static string Format(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString("O") ?? string.Empty;
    }

    private static string GetAppVersion()
    {
        return typeof(LicenseValidator).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static async Task<LicenseCacheEntry?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(ServicePaths.LicenseCachePath))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(ServicePaths.LicenseCachePath, cancellationToken);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<LicenseCacheEntry>(bytes, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteCacheAsync(LicenseCacheEntry entry, CancellationToken cancellationToken)
    {
        ServicePaths.EnsureDirectories();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        await File.WriteAllBytesAsync(ServicePaths.LicenseCachePath, protectedBytes, cancellationToken);
    }

    private static async Task<LicensePublicKeysResponse?> ReadPublicKeysCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(ServicePaths.LicensePublicKeysCachePath))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(ServicePaths.LicensePublicKeysCachePath, cancellationToken);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<LicensePublicKeysResponse>(bytes, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WritePublicKeysCacheAsync(LicensePublicKeysResponse response, CancellationToken cancellationToken)
    {
        ServicePaths.EnsureDirectories();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        await File.WriteAllBytesAsync(ServicePaths.LicensePublicKeysCachePath, protectedBytes, cancellationToken);
    }
}
