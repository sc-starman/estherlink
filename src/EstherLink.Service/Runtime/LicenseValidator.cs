using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EstherLink.Core.Configuration;
using EstherLink.Core.Licensing;

namespace EstherLink.Service.Runtime;

public sealed class LicenseValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EstherLink.LicenseCache.v1");
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

        if (!forceOnline &&
            cache is not null &&
            cache.IsValid &&
            cache.LicenseKeyHash == licenseKeyHash &&
            cache.ExpiresAtUtc > now)
        {
            return new LicenseValidationResult(true, true, cache.CheckedAtUtc, cache.ExpiresAtUtc, null);
        }

        if (string.IsNullOrWhiteSpace(config.LicenseServerUrl) || string.IsNullOrWhiteSpace(config.LicenseKey))
        {
            return BuildCacheFallback(cache, licenseKeyHash, now, "License key or server endpoint is missing.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(LicenseValidator));
            client.Timeout = TimeSpan.FromSeconds(10);

            var payload = new LicenseRequest
            {
                LicenseKey = config.LicenseKey,
                MachineName = Environment.MachineName,
                Product = "EstherLink"
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, config.LicenseServerUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return BuildCacheFallback(
                    cache,
                    licenseKeyHash,
                    now,
                    $"License server HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            var parsed = JsonSerializer.Deserialize<LicenseResponse>(responseBody, JsonOptions);
            if (parsed is null)
            {
                return BuildCacheFallback(cache, licenseKeyHash, now, "License server returned invalid JSON.");
            }

            var expiresAt = parsed.ExpiresAtUtc ?? now.AddHours(24);
            if (!parsed.IsValid || expiresAt <= now)
            {
                await WriteCacheAsync(new LicenseCacheEntry(false, licenseKeyHash, now, now), cancellationToken);
                return new LicenseValidationResult(false, false, now, expiresAt, parsed.Error ?? "License is invalid.");
            }

            var fresh = new LicenseCacheEntry(true, licenseKeyHash, now, expiresAt);
            await WriteCacheAsync(fresh, cancellationToken);
            _fileLog.Info($"License verified online. Expires at {expiresAt:O}.");
            return new LicenseValidationResult(true, false, now, expiresAt, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "License verification call failed.");
            _fileLog.Warn($"License online verification failed: {ex.Message}");
            return BuildCacheFallback(cache, licenseKeyHash, now, ex.Message);
        }
    }

    private static LicenseValidationResult BuildCacheFallback(
        LicenseCacheEntry? cache,
        string licenseKeyHash,
        DateTimeOffset now,
        string error)
    {
        if (cache is not null &&
            cache.IsValid &&
            cache.LicenseKeyHash == licenseKeyHash &&
            cache.ExpiresAtUtc > now)
        {
            return new LicenseValidationResult(true, true, cache.CheckedAtUtc, cache.ExpiresAtUtc, null);
        }

        return new LicenseValidationResult(false, false, now, cache?.ExpiresAtUtc, error);
    }

    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
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

    private sealed class LicenseRequest
    {
        public string LicenseKey { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string Product { get; set; } = string.Empty;
    }

    private sealed class LicenseResponse
    {
        public bool IsValid { get; set; }
        public DateTimeOffset? ExpiresAtUtc { get; set; }
        public string? Error { get; set; }
    }
}
