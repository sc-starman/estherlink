using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chaos.NaCl;
using OmniRelay.Backend.Contracts.Licensing;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Licensing;
using OmniRelay.Core.Networking;
using Microsoft.Win32;

namespace OmniRelay.Service.Runtime;

public sealed class LicenseValidator
{
    private const string VerifyUrl = "https://omnirelay.net/api/license/verify";
    private const string OfflineCertificateKeyId = "offline-cert-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OmniRelay.LicenseCache.v2");
    private static readonly byte[] OfflineCertificateRootSeed = SHA256.HashData(Encoding.UTF8.GetBytes("OmniRelay.LicenseCertificate.Root.v1"));
    private static readonly byte[] OfflineCertificatePublicKey;
    private static readonly TimeSpan HttpConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly SemaphoreSlim ValidationLock = new(1, 1);
    private static int _diagnosticLogged;
    private readonly ILogger<LicenseValidator> _logger;
    private readonly FileLogWriter _fileLog;

    static LicenseValidator()
    {
        Ed25519.KeyPairFromSeed(out OfflineCertificatePublicKey, out _, OfflineCertificateRootSeed);
    }

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
                _fileLog.Info("License validator HTTP mode: parallel direct/proxy-aware all-adapter race, per-attempt timeout=45s, connect-timeout=30s.");
            }

            var now = DateTimeOffset.UtcNow;
            var licenseKeyHash = Hash(config.LicenseKey);
            var cache = await ReadCacheAsync(cancellationToken);
            var publicKeys = await ReadPublicKeysCacheAsync(cancellationToken);
            var certificateCache = await ReadCertificateCacheAsync(cancellationToken);
            var fingerprintPayload = BuildDeviceFingerprintPayload();
            var fingerprintHash = HashFingerprintPayload(fingerprintPayload);

            if (!forceOnline &&
                certificateCache is not null &&
                certificateCache.LicenseKeyHash == licenseKeyHash &&
                VerifyOfflineCertificate(certificateCache.Certificate, fingerprintHash))
            {
                return new LicenseValidationResult(
                    true,
                    false,
                    now,
                    null,
                    null,
                    Source: "certificate");
            }

            if (!forceOnline &&
                cache is not null &&
                cache.IsValid &&
                cache.LicenseKeyHash == licenseKeyHash &&
                cache.ExpiresAtUtc > now &&
                !IsLicenseExpired(cache.LicenseExpiresAtUtc, now) &&
                VerifyCachedEntrySignature(cache, publicKeys))
            {
                return new LicenseValidationResult(
                    true,
                    true,
                    cache.CheckedAtUtc,
                    cache.LicenseExpiresAtUtc,
                    null,
                    cache.TransferRequired,
                    cache.TransferLimitPerRollingYear,
                    cache.TransfersUsedInWindow,
                    cache.TransfersRemainingInWindow,
                    cache.TransferWindowStartAt,
                    cache.ActiveDeviceIdHint,
                    cache.Reason,
                    cache.RequestId,
                    cache.KeyId,
                    "legacy_cache");
            }

            if (string.IsNullOrWhiteSpace(config.LicenseKey))
            {
                return BuildCacheFallback(cache, publicKeys, licenseKeyHash, now, "License key is missing.");
            }

            try
            {
                var verifyUrl = VerifyUrl;
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

                using var fingerprintDoc = JsonDocument.Parse(fingerprintPayload);

                var verifyRequest = new LicenseVerifyRequest
                {
                    LicenseKey = config.LicenseKey,
                    AppVersion = GetAppVersion(),
                    Nonce = nonce,
                    Fingerprint = fingerprintDoc.RootElement.Clone(),
                    TransferRequested = transferRequested
                };

                var verifyRequestJson = JsonSerializer.Serialize(verifyRequest, JsonOptions);

                var verifyAttempt = await SendVerifyWithFallbackAsync(config, verifyUrl, verifyRequestJson, cancellationToken);
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

                var keysResponse = await FetchPublicKeysWithFallbackAsync(config, verifyUrl, cancellationToken);
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
                var licenseExpiresAt = parsed.LicenseExpiresAt?.ToUniversalTime();
                var isLicenseExpired = IsLicenseExpired(licenseExpiresAt, now);
                var newCache = new LicenseCacheEntry(
                    parsed.Valid && !isLicenseExpired,
                    licenseKeyHash,
                    now,
                    cacheExpiresAt,
                    nonce,
                    parsed.SignatureAlg,
                    parsed.KeyId,
                    parsed.Signature,
                    isLicenseExpired ? "EXPIRED" : parsed.Reason,
                    parsed.TransferRequired,
                    parsed.TransferLimitPerRollingYear,
                    parsed.TransfersUsedInWindow,
                    parsed.TransfersRemainingInWindow,
                    parsed.TransferWindowStartAt?.ToUniversalTime(),
                    parsed.ActiveDeviceIdHint,
                    parsed.Plan,
                    licenseExpiresAt,
                    parsed.ServerTime.ToUniversalTime(),
                    parsed.RequestId);

                await WriteCacheAsync(newCache, cancellationToken);

                if (parsed.Valid && parsed.LicenseCertificate is not null)
                {
                    if (VerifyOfflineCertificate(parsed.LicenseCertificate, fingerprintHash))
                    {
                        await WriteCertificateCacheAsync(
                            new LicenseCertificateCacheEntry(licenseKeyHash, now, parsed.LicenseCertificate),
                            cancellationToken);
                    }
                    else
                    {
                        _fileLog.Warn("Server returned invalid offline certificate payload/signature; skipped local certificate cache update.");
                    }
                }

                if (!newCache.IsValid || cacheExpiresAt <= now)
                {
                    return new LicenseValidationResult(
                        false,
                        false,
                        now,
                        licenseExpiresAt,
                        newCache.Reason,
                        parsed.TransferRequired,
                        parsed.TransferLimitPerRollingYear,
                        parsed.TransfersUsedInWindow,
                        parsed.TransfersRemainingInWindow,
                        parsed.TransferWindowStartAt?.ToUniversalTime(),
                        parsed.ActiveDeviceIdHint,
                        newCache.Reason,
                        parsed.RequestId,
                        parsed.KeyId,
                        "online");
                }

                _fileLog.Info($"License verified online. keyId={parsed.KeyId} requestId={parsed.RequestId} cacheExpiresAt={cacheExpiresAt:O}");
                return new LicenseValidationResult(
                    true,
                    false,
                    now,
                    licenseExpiresAt,
                    null,
                    parsed.TransferRequired,
                    parsed.TransferLimitPerRollingYear,
                    parsed.TransfersUsedInWindow,
                    parsed.TransfersRemainingInWindow,
                    parsed.TransferWindowStartAt?.ToUniversalTime(),
                    parsed.ActiveDeviceIdHint,
                    parsed.Reason,
                    parsed.RequestId,
                    parsed.KeyId,
                    "online");
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
            VerifyCachedEntrySignature(cache, keys) &&
            IsLicenseExpired(cache.LicenseExpiresAtUtc, now))
        {
            return new LicenseValidationResult(
                false,
                true,
                cache.CheckedAtUtc,
                cache.LicenseExpiresAtUtc,
                "License has expired.",
                cache.TransferRequired,
                cache.TransferLimitPerRollingYear,
                cache.TransfersUsedInWindow,
                cache.TransfersRemainingInWindow,
                cache.TransferWindowStartAt,
                cache.ActiveDeviceIdHint,
                "EXPIRED",
                cache.RequestId,
                cache.KeyId,
                "legacy_cache");
        }

        if (cache is not null &&
            cache.IsValid &&
            cache.LicenseKeyHash == licenseKeyHash &&
            cache.ExpiresAtUtc > now &&
            !IsLicenseExpired(cache.LicenseExpiresAtUtc, now) &&
            VerifyCachedEntrySignature(cache, keys))
        {
            return new LicenseValidationResult(
                true,
                true,
                cache.CheckedAtUtc,
                cache.LicenseExpiresAtUtc,
                null,
                cache.TransferRequired,
                cache.TransferLimitPerRollingYear,
                cache.TransfersUsedInWindow,
                cache.TransfersRemainingInWindow,
                cache.TransferWindowStartAt,
                cache.ActiveDeviceIdHint,
                cache.Reason,
                cache.RequestId,
                cache.KeyId,
                "legacy_cache");
        }

        return new LicenseValidationResult(
            false,
            false,
            now,
            cache?.LicenseExpiresAtUtc,
            error,
            Reason: "OFFLINE_FALLBACK_FAILED",
            Source: "offline_fallback_failed");
    }

    private static string BuildPublicKeysUrl(string verifyUrl)
    {
        return verifyUrl.EndsWith("/verify", StringComparison.OrdinalIgnoreCase)
            ? $"{verifyUrl[..^"/verify".Length]}/public-keys"
            : $"{verifyUrl.TrimEnd('/')}/public-keys";
    }

    private static async Task<LicensePublicKeysResponse?> FetchPublicKeysAsync(
        NetworkAttempt attempt,
        string verifyUrl,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(attempt.UseProxy, attempt.BindIp);
        var keysUrl = BuildPublicKeysUrl(verifyUrl);
        using var response = await client.GetAsync(keysUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<LicensePublicKeysResponse>(json, JsonOptions);
    }

    private async Task<VerifyAttempt> SendVerifyWithFallbackAsync(
        ServiceConfig config,
        string verifyUrl,
        string requestJson,
        CancellationToken cancellationToken)
    {
        var attempts = BuildAttempts(config);
        if (attempts.Count == 0)
        {
            return new VerifyAttempt(false, null, null, "No usable IPv4 source adapter found for license verification.");
        }

        var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = attempts
            .Select(attempt => SendVerifyRaceAttemptAsync(attempt, verifyUrl, requestJson, raceCts.Token))
            .ToList();
        var allTasks = pending.ToArray();
        var errors = new List<string>();
        VerifyAttempt? firstNonSuccessHttpResponse = null;

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var outcome = await completed;

            var result = outcome.Result;
            if (result.Success && result.Response?.IsSuccessStatusCode == true)
            {
                raceCts.Cancel();
                _ = Task.WhenAll(allTasks).ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    raceCts,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                _fileLog.Info($"License verification succeeded via {outcome.Attempt.DisplayName}; canceled remaining parallel attempts.");
                return result;
            }

            if (result.Success && result.Response is not null)
            {
                firstNonSuccessHttpResponse ??= result;
                var statusCode = (int)result.Response.StatusCode;
                var reason = result.Response.ReasonPhrase ?? "unknown";
                var detail = $"HTTP {statusCode}: {reason}";
                errors.Add($"{outcome.Attempt.DisplayName}: {detail}");
                _fileLog.Warn($"License {outcome.Attempt.DisplayName} verification attempt returned non-success status: {detail}");
                continue;
            }

            var error = result.Error ?? "unknown error";
            errors.Add($"{outcome.Attempt.DisplayName}: {error}");
            _fileLog.Warn($"License {outcome.Attempt.DisplayName} verification attempt failed: {error}");
        }

        raceCts.Dispose();
        return firstNonSuccessHttpResponse
            ?? new VerifyAttempt(false, null, null, $"All {attempts.Count} license verify paths failed. {string.Join(" | ", errors)}");
    }

    private static async Task<VerifyRaceOutcome> SendVerifyRaceAttemptAsync(
        NetworkAttempt attempt,
        string verifyUrl,
        string requestJson,
        CancellationToken cancellationToken)
    {
        var result = await SendVerifyAsync(attempt, verifyUrl, requestJson, cancellationToken);
        return new VerifyRaceOutcome(attempt, result);
    }

    private static async Task<VerifyAttempt> SendVerifyAsync(
        NetworkAttempt attempt,
        string verifyUrl,
        string requestJson,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            using var client = CreateHttpClient(attempt.UseProxy, attempt.BindIp);
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
        ServiceConfig config,
        string verifyUrl,
        CancellationToken cancellationToken)
    {
        var attempts = BuildAttempts(config);
        if (attempts.Count == 0)
        {
            _fileLog.Warn("Public keys fetch skipped: no usable IPv4 source adapter found.");
            return null;
        }

        var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = attempts
            .Select(attempt => FetchPublicKeysRaceAttemptAsync(attempt, verifyUrl, raceCts.Token))
            .ToList();
        var allTasks = pending.ToArray();

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var outcome = await completed;

            if (outcome.Response is not null)
            {
                raceCts.Cancel();
                _ = Task.WhenAll(allTasks).ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    raceCts,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                _fileLog.Info($"Public keys fetch succeeded via {outcome.Attempt.DisplayName}; canceled remaining parallel attempts.");
                return outcome.Response;
            }

            _fileLog.Warn($"Public keys fetch failed via {outcome.Attempt.DisplayName}: {outcome.Error ?? "HTTP non-success status."}");
        }

        raceCts.Dispose();
        return null;
    }

    private static async Task<PublicKeysRaceOutcome> FetchPublicKeysRaceAttemptAsync(
        NetworkAttempt attempt,
        string verifyUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await FetchPublicKeysAsync(attempt, verifyUrl, cancellationToken);
            return response is not null
                ? new PublicKeysRaceOutcome(attempt, response, null)
                : new PublicKeysRaceOutcome(attempt, null, "HTTP non-success status.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PublicKeysRaceOutcome(attempt, null, ex.Message);
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

    private static bool VerifyOfflineCertificate(LicenseCertificate certificate, string fingerprintHash)
    {
        if (certificate is null ||
            !certificate.IsPerpetual ||
            !string.Equals(certificate.SignatureAlg, "Ed25519", StringComparison.Ordinal) ||
            !string.Equals(certificate.KeyId, OfflineCertificateKeyId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(certificate.Signature) ||
            !string.Equals(certificate.DeviceFingerprintHash, fingerprintHash, StringComparison.Ordinal))
        {
            return false;
        }

        var payload =
            $"licenseId={certificate.LicenseId};" +
            $"plan={certificate.Plan};" +
            $"deviceFingerprintHash={certificate.DeviceFingerprintHash};" +
            $"issuedAt={certificate.IssuedAt.ToUniversalTime():O};" +
            $"isPerpetual={certificate.IsPerpetual};" +
            $"updateEntitlementUntil={Format(certificate.UpdateEntitlementUntil)};" +
            $"signatureAlg={certificate.SignatureAlg};" +
            $"keyId={certificate.KeyId}";

        try
        {
            var signature = Convert.FromBase64String(certificate.Signature);
            return Ed25519.Verify(signature, Encoding.UTF8.GetBytes(payload), OfflineCertificatePublicKey);
        }
        catch
        {
            return false;
        }
    }

    private static string HashFingerprintPayload(string payload)
    {
        return Hash(payload);
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

    private static bool IsLicenseExpired(DateTimeOffset? licenseExpiresAtUtc, DateTimeOffset nowUtc)
    {
        return licenseExpiresAtUtc.HasValue && licenseExpiresAtUtc.Value <= nowUtc;
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

    private static async Task<LicenseCertificateCacheEntry?> ReadCertificateCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(ServicePaths.LicenseCertificatePath))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(ServicePaths.LicenseCertificatePath, cancellationToken);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<LicenseCertificateCacheEntry>(bytes, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteCertificateCacheAsync(LicenseCertificateCacheEntry entry, CancellationToken cancellationToken)
    {
        ServicePaths.EnsureDirectories();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        await File.WriteAllBytesAsync(ServicePaths.LicenseCertificatePath, protectedBytes, cancellationToken);
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

    private static HttpClient CreateHttpClient(bool useProxy, IPAddress bindIp)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            Proxy = useProxy ? WebRequest.DefaultWebProxy : null,
            ConnectTimeout = HttpConnectTimeout,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = (context, cancellationToken) => ConnectWithIpv4PreferenceAsync(context, bindIp, cancellationToken)
        };

        return new HttpClient(handler)
        {
            Timeout = HttpRequestTimeout
        };
    }

    private sealed record VerifyAttempt(bool Success, HttpResponseMessage? Response, string? ResponseBody, string? Error);
    private sealed record VerifyRaceOutcome(NetworkAttempt Attempt, VerifyAttempt Result);
    private sealed record PublicKeysRaceOutcome(NetworkAttempt Attempt, LicensePublicKeysResponse? Response, string? Error);

    private static async ValueTask<Stream> ConnectWithIpv4PreferenceAsync(
        SocketsHttpConnectionContext context,
        IPAddress bindIp,
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
            if (address.AddressFamily != bindIp.AddressFamily)
            {
                continue;
            }

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                socket.Bind(new IPEndPoint(bindIp, 0));
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

        throw new HttpRequestException($"Unable to connect to {host}:{port} from source {bindIp}.", lastError);
    }

    private static List<NetworkAttempt> BuildAttempts(ServiceConfig config)
    {
        var adapters = BuildAllAdapterBindTargets();

        var attempts = new List<NetworkAttempt>(capacity: adapters.Count * 2);
        foreach (var adapter in adapters)
        {
            attempts.Add(new NetworkAttempt("Direct", UseProxy: false, adapter.BindIp, $"{adapter.Label} ifIndex={adapter.IfIndex} source={adapter.BindIp}"));
        }

        foreach (var adapter in adapters)
        {
            attempts.Add(new NetworkAttempt("Proxy-aware", UseProxy: true, adapter.BindIp, $"{adapter.Label} ifIndex={adapter.IfIndex} source={adapter.BindIp}"));
        }

        return attempts;
    }

    private static List<AdapterBindTarget> BuildAllAdapterBindTargets()
    {
        var adapters = new List<AdapterBindTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in NetworkAdapterCatalog.ListIpv4Adapters())
        {
            foreach (var addressText in adapter.IPv4Addresses)
            {
                if (!IPAddress.TryParse(addressText, out var bindIp))
                {
                    continue;
                }

                var key = $"{adapter.IfIndex}/{bindIp}";
                if (!seen.Add(key))
                {
                    continue;
                }

                var label = adapter.HasDefaultGateway ? "Adapter(default-gw)" : "Adapter";
                adapters.Add(new AdapterBindTarget(label, adapter.IfIndex, bindIp));
            }
        }

        return adapters;
    }

    private sealed record AdapterBindTarget(string Label, int IfIndex, IPAddress BindIp);
    private sealed record NetworkAttempt(string Mode, bool UseProxy, IPAddress BindIp, string AdapterLabel)
    {
        public string DisplayName => $"{Mode} {AdapterLabel}";
    }
}
