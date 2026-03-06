using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chaos.NaCl;
using EstherLink.Backend.Contracts.Licensing;
using EstherLink.Core.Configuration;
using EstherLink.Core.Licensing;
using Microsoft.Win32;

namespace EstherLink.Service.Runtime;

public sealed class LicenseValidator
{
    private const string VerifyUrl = "https://omnirelay.net/api/license/verify";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EstherLink.LicenseCache.v2");
    private static readonly HttpClient DirectHttpClient = CreateDirectHttpClient();
    private static readonly HttpClient ProxyHttpClient = CreateProxyAwareHttpClient();
    private static readonly SemaphoreSlim ValidationLock = new(1, 1);
    private static int _diagnosticLogged;
    private readonly ILogger<LicenseValidator> _logger;
    private readonly FileLogWriter _fileLog;

    public LicenseValidator(
        ILogger<LicenseValidator> logger,
        FileLogWriter fileLog)
    {
        _logger = logger;
        _fileLog = fileLog;
    }

    public async Task<LicenseValidationResult> ValidateAsync(
        ServiceConfig config,
        bool forceOnline,
        bool transferRequested,
        CancellationToken cancellationToken)
    {
        await ValidationLock.WaitAsync(cancellationToken);
        try
        {
            if (Interlocked.Exchange(ref _diagnosticLogged, 1) == 0)
            {
                _fileLog.Info("License validator HTTP mode: direct+proxy fallback, per-attempt timeout=45s, connect-timeout=30s.");
            }

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
                    cache.TransferRequired,
                    cache.TransferLimitPerRollingYear,
                    cache.TransfersUsedInWindow,
                    cache.TransfersRemainingInWindow,
                    cache.TransferWindowStartAt,
                    cache.ActiveDeviceIdHint,
                    cache.Reason,
                    cache.RequestId,
                    cache.KeyId);
            }

            if (string.IsNullOrWhiteSpace(config.LicenseKey))
            {
                return BuildCacheFallback(cache, publicKeys, licenseKeyHash, now, "License key is missing.");
            }

            try
            {
                var verifyUrl = VerifyUrl;
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

                using var fingerprintDoc = JsonDocument.Parse(BuildDeviceFingerprintPayload());

                var verifyRequest = new LicenseVerifyRequest
                {
                    LicenseKey = config.LicenseKey,
                    AppVersion = GetAppVersion(),
                    Nonce = nonce,
                    Fingerprint = fingerprintDoc.RootElement.Clone(),
                    TransferRequested = transferRequested
                };

                var verifyRequestJson = JsonSerializer.Serialize(verifyRequest, JsonOptions);

                var verifyAttempt = await SendVerifyWithFallbackAsync(verifyUrl, verifyRequestJson, cancellationToken);
                if (!verifyAttempt.Success)
                {
                    return BuildCacheFallback(cache, publicKeys, licenseKeyHash, now, verifyAttempt.Error ?? "License verification failed.");
                }

                var response = verifyAttempt.Response!;
                var responseBody = verifyAttempt.ResponseBody ?? string.Empty;
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

                var keysResponse = await FetchPublicKeysWithFallbackAsync(verifyUrl, cancellationToken);
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
                    parsed.TransferRequired,
                    parsed.TransferLimitPerRollingYear,
                    parsed.TransfersUsedInWindow,
                    parsed.TransfersRemainingInWindow,
                    parsed.TransferWindowStartAt?.ToUniversalTime(),
                    parsed.ActiveDeviceIdHint,
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
                        parsed.TransferRequired,
                        parsed.TransferLimitPerRollingYear,
                        parsed.TransfersUsedInWindow,
                        parsed.TransfersRemainingInWindow,
                        parsed.TransferWindowStartAt?.ToUniversalTime(),
                        parsed.ActiveDeviceIdHint,
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
                    parsed.TransferRequired,
                    parsed.TransferLimitPerRollingYear,
                    parsed.TransfersUsedInWindow,
                    parsed.TransfersRemainingInWindow,
                    parsed.TransferWindowStartAt?.ToUniversalTime(),
                    parsed.ActiveDeviceIdHint,
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
        finally
        {
            ValidationLock.Release();
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
                cache.TransferRequired,
                cache.TransferLimitPerRollingYear,
                cache.TransfersUsedInWindow,
                cache.TransfersRemainingInWindow,
                cache.TransferWindowStartAt,
                cache.ActiveDeviceIdHint,
                cache.Reason,
                cache.RequestId,
                cache.KeyId);
        }

        return new LicenseValidationResult(
            false,
            false,
            now,
            cache?.ExpiresAtUtc,
            error,
            Reason: "OFFLINE_FALLBACK_FAILED");
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

    private async Task<VerifyAttempt> SendVerifyWithFallbackAsync(string verifyUrl, string requestJson, CancellationToken cancellationToken)
    {
        var direct = await SendVerifyAsync(DirectHttpClient, verifyUrl, requestJson, cancellationToken);
        if (direct.Success)
        {
            return direct;
        }

        _fileLog.Warn($"License direct verification attempt failed: {direct.Error}");

        var proxy = await SendVerifyAsync(ProxyHttpClient, verifyUrl, requestJson, cancellationToken);
        if (proxy.Success)
        {
            return proxy;
        }

        _fileLog.Warn($"License proxy verification attempt failed: {proxy.Error}");
        return new VerifyAttempt(false, null, null, $"Direct+Proxy verify failed. direct={direct.Error}; proxy={proxy.Error}");
    }

    private static async Task<VerifyAttempt> SendVerifyAsync(
        HttpClient client,
        string verifyUrl,
        string requestJson,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, verifyUrl)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var clone = new HttpResponseMessage(response.StatusCode)
            {
                ReasonPhrase = response.ReasonPhrase
            };
            return new VerifyAttempt(true, clone, body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            var elapsedMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            var tokenState = cancellationToken.IsCancellationRequested ? "caller-canceled" : "local-cancel-or-timeout";
            var detail = $"{ex.GetType().Name} after {elapsedMs}ms ({tokenState}): {ex.Message}";
            return new VerifyAttempt(false, null, null, detail);
        }
    }

    private async Task<LicensePublicKeysResponse?> FetchPublicKeysWithFallbackAsync(
        string verifyUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var direct = await FetchPublicKeysAsync(DirectHttpClient, verifyUrl, cancellationToken);
            if (direct is not null)
            {
                return direct;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _fileLog.Warn($"Public keys direct fetch failed: {ex.Message}");
        }

        try
        {
            return await FetchPublicKeysAsync(ProxyHttpClient, verifyUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _fileLog.Warn($"Public keys proxy fetch failed: {ex.Message}");
            return null;
        }
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
            response.TransferRequired,
            response.ActiveDeviceIdHint,
            response.TransferLimitPerRollingYear,
            response.TransfersUsedInWindow,
            response.TransfersRemainingInWindow,
            response.TransferWindowStartAt?.ToUniversalTime(),
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
            cache.TransferRequired,
            cache.ActiveDeviceIdHint,
            cache.TransferLimitPerRollingYear,
            cache.TransfersUsedInWindow,
            cache.TransfersRemainingInWindow,
            cache.TransferWindowStartAt?.ToUniversalTime(),
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
        bool transferRequired,
        string? activeDeviceIdHint,
        int transferLimitPerRollingYear,
        int transfersUsedInWindow,
        int transfersRemainingInWindow,
        DateTimeOffset? transferWindowStartAt,
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
            $"transferRequired={transferRequired};" +
            $"activeDeviceIdHint={activeDeviceIdHint ?? string.Empty};" +
            $"transferLimitPerRollingYear={transferLimitPerRollingYear};" +
            $"transfersUsedInWindow={transfersUsedInWindow};" +
            $"transfersRemainingInWindow={transfersRemainingInWindow};" +
            $"transferWindowStartAt={Format(transferWindowStartAt)};" +
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

    private static string BuildDeviceFingerprintPayload()
    {
        var machineGuid = TryReadMachineGuid();
        var payload = new
        {
            machineName = Environment.MachineName,
            osVersion = Environment.OSVersion.VersionString,
            machineGuid,
            domainName = Environment.UserDomainName,
            processorCount = Environment.ProcessorCount
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string? TryReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString();
        }
        catch
        {
            return null;
        }
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

    private static HttpClient CreateDirectHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = ConnectWithIpv4PreferenceAsync
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    private static HttpClient CreateProxyAwareHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = WebRequest.DefaultWebProxy,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = ConnectWithIpv4PreferenceAsync
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    private sealed record VerifyAttempt(bool Success, HttpResponseMessage? Response, string? ResponseBody, string? Error);

    private static async ValueTask<Stream> ConnectWithIpv4PreferenceAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"DNS lookup returned no addresses for {host}.");
        }

        Exception? lastError = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(address, port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                lastError = ex;
                socket.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException($"Unable to connect to {host}:{port}.", lastError);
    }
}
