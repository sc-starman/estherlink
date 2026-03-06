using System.Text.Json;
using EstherLink.Backend.Configuration;
using EstherLink.Backend.Contracts.Licensing;
using EstherLink.Backend.Data;
using EstherLink.Backend.Data.Entities;
using EstherLink.Backend.Data.Enums;
using EstherLink.Backend.Security;
using EstherLink.Backend.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Services;

public sealed class LicenseService
{
    private const int TransferLimitPerRollingYear = 3;
    private const int TransferWindowDays = 365;

    private readonly AppDbContext _dbContext;
    private readonly LicenseResponseSigner _signer;
    private readonly IOptions<LicensingOptions> _options;
    private readonly ILogger<LicenseService> _logger;

    public LicenseService(
        AppDbContext dbContext,
        LicenseResponseSigner signer,
        IOptions<LicensingOptions> options,
        ILogger<LicenseService> logger)
    {
        _dbContext = dbContext;
        _signer = signer;
        _options = options;
        _logger = logger;
    }

    public async Task<LicenseVerifyResponse> VerifyAsync(
        LicenseVerifyRequest request,
        string requestId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var transferWindowStart = now.AddDays(-TransferWindowDays);
        var licenseKeyHash = Sha256Util.HashHex(request.LicenseKey);

        var response = new LicenseVerifyResponse
        {
            Valid = false,
            Reason = "INVALID_KEY",
            TransferRequired = false,
            TransferLimitPerRollingYear = TransferLimitPerRollingYear,
            TransfersUsedInWindow = 0,
            TransfersRemainingInWindow = TransferLimitPerRollingYear,
            TransferWindowStartAt = transferWindowStart,
            CacheExpiresAt = now.AddHours(_options.Value.OfflineCacheTtlHours),
            ServerTime = now,
            SignatureAlg = SigningKeyService.SignatureAlgorithm,
            RequestId = requestId
        };

        var license = await _dbContext.Licenses
            .Include(x => x.Activations)
            .Include(x => x.Transfers)
            .FirstOrDefaultAsync(x => x.LicenseKey == request.LicenseKey, cancellationToken);

        if (license is null)
        {
            await SignResponseAsync(response, request.Nonce, cancellationToken);
            return response;
        }

        response.Plan = license.Plan;
        response.LicenseExpiresAt = license.ExpiresAt?.ToUniversalTime();

        var transfersUsedInWindow = license.Transfers.Count(x => x.CreatedAt >= transferWindowStart);
        response.TransfersUsedInWindow = transfersUsedInWindow;
        response.TransfersRemainingInWindow = Math.Max(0, TransferLimitPerRollingYear - transfersUsedInWindow);

        if (license.Status == LicenseStatus.Revoked)
        {
            response.Reason = "REVOKED";
            await SignResponseAsync(response, request.Nonce, cancellationToken);
            return response;
        }

        if (license.Status == LicenseStatus.Suspended)
        {
            response.Reason = "SUSPENDED";
            await SignResponseAsync(response, request.Nonce, cancellationToken);
            return response;
        }

        if (license.ExpiresAt.HasValue && license.ExpiresAt.Value < now)
        {
            response.Reason = "EXPIRED";
            await SignResponseAsync(response, request.Nonce, cancellationToken);
            return response;
        }

        var fingerprintHash = FingerprintHasher.ComputeHash(request.Fingerprint);
        var activation = license.Activations.FirstOrDefault(x => x.FingerprintHash == fingerprintHash);
        var activeActivations = license.Activations.Where(x => !x.IsBlocked).ToList();
        var currentlyActiveElsewhere = activeActivations.FirstOrDefault(x => x.FingerprintHash != fingerprintHash);
        response.ActiveDeviceIdHint = ToDeviceHint(currentlyActiveElsewhere?.FingerprintHash);

        if (activation is not null && !activation.IsBlocked)
        {
            TouchActivation(activation, request.AppVersion, now);
            response.Valid = true;
            response.Reason = "OK";
            response.Features = new Dictionary<string, string>
            {
                ["plan"] = license.Plan
            };
            license.UpdatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await SignResponseAsync(response, request.Nonce, cancellationToken);
            LogVerify(requestId, licenseKeyHash, response, license.Plan);
            return response;
        }

        var hasOtherActiveDevice = activeActivations.Any(x => x.FingerprintHash != fingerprintHash);
        if (!hasOtherActiveDevice && activeActivations.Count == 0)
        {
            if (activation is null)
            {
                activation = new LicenseActivationEntity
                {
                    Id = Guid.NewGuid(),
                    LicenseId = license.Id,
                    FingerprintHash = fingerprintHash,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    IsBlocked = false,
                    MetaJson = BuildActivationMeta(request.AppVersion, now)
                };
                _dbContext.LicenseActivations.Add(activation);
            }
            else
            {
                activation.IsBlocked = false;
                TouchActivation(activation, request.AppVersion, now);
            }

            response.Valid = true;
            response.Reason = "OK";
            response.Features = new Dictionary<string, string>
            {
                ["plan"] = license.Plan
            };
            license.UpdatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await SignResponseAsync(response, request.Nonce, cancellationToken);
            LogVerify(requestId, licenseKeyHash, response, license.Plan);
            return response;
        }

        if (!request.TransferRequested)
        {
            response.Reason = "TRANSFER_REQUIRED";
            response.TransferRequired = true;
            await SignResponseAsync(response, request.Nonce, cancellationToken);
            LogVerify(requestId, licenseKeyHash, response, license.Plan);
            return response;
        }

        if (response.TransfersRemainingInWindow <= 0)
        {
            response.Reason = "TRANSFER_LIMIT_EXCEEDED";
            response.TransferRequired = true;
            await SignResponseAsync(response, request.Nonce, cancellationToken);
            LogVerify(requestId, licenseKeyHash, response, license.Plan);
            return response;
        }

        var fromFingerprintHash = activeActivations
            .Where(x => x.FingerprintHash != fingerprintHash)
            .OrderByDescending(x => x.LastSeenAt)
            .Select(x => x.FingerprintHash)
            .FirstOrDefault();

        foreach (var item in activeActivations.Where(x => x.FingerprintHash != fingerprintHash))
        {
            item.IsBlocked = true;
        }

        if (activation is null)
        {
            activation = new LicenseActivationEntity
            {
                Id = Guid.NewGuid(),
                LicenseId = license.Id,
                FingerprintHash = fingerprintHash,
                FirstSeenAt = now,
                LastSeenAt = now,
                IsBlocked = false,
                MetaJson = BuildActivationMeta(request.AppVersion, now)
            };
            _dbContext.LicenseActivations.Add(activation);
        }
        else
        {
            activation.IsBlocked = false;
            TouchActivation(activation, request.AppVersion, now);
        }

        _dbContext.LicenseTransfers.Add(new LicenseTransferEntity
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            FromFingerprintHash = fromFingerprintHash,
            ToFingerprintHash = fingerprintHash,
            RequestId = requestId,
            AppVersion = request.AppVersion?.Trim() ?? string.Empty,
            CreatedAt = now,
            MetaJson = JsonSerializer.Serialize(new
            {
                transferRequested = request.TransferRequested,
                observedAt = now
            })
        });

        response.Valid = true;
        response.Reason = "OK";
        response.TransferRequired = false;
        response.ActiveDeviceIdHint = null;
        response.TransfersUsedInWindow++;
        response.TransfersRemainingInWindow = Math.Max(0, TransferLimitPerRollingYear - response.TransfersUsedInWindow);
        response.Features = new Dictionary<string, string>
        {
            ["plan"] = license.Plan
        };

        license.MaxDevices = 1;
        license.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await SignResponseAsync(response, request.Nonce, cancellationToken);
        LogVerify(requestId, licenseKeyHash, response, license.Plan);
        return response;
    }

    private async Task SignResponseAsync(
        LicenseVerifyResponse response,
        string nonce,
        CancellationToken cancellationToken)
    {
        var signed = await _signer.SignAsync(response, nonce, cancellationToken);
        response.KeyId = signed.KeyId;
        response.Signature = signed.Signature;
    }

    private void LogVerify(string requestId, string licenseKeyHash, LicenseVerifyResponse response, string? plan)
    {
        _logger.LogInformation(
            "License verify requestId={RequestId} licenseKeyHash={LicenseKeyHash} reason={Reason} valid={Valid} transferRequired={TransferRequired} remaining={Remaining} plan={Plan}",
            requestId,
            licenseKeyHash,
            response.Reason,
            response.Valid,
            response.TransferRequired,
            response.TransfersRemainingInWindow,
            plan);
    }

    private static string BuildActivationMeta(string appVersion, DateTimeOffset observedAtUtc)
    {
        return JsonSerializer.Serialize(new
        {
            appVersion,
            observedAt = observedAtUtc
        });
    }

    private static void TouchActivation(LicenseActivationEntity activation, string appVersion, DateTimeOffset observedAtUtc)
    {
        activation.LastSeenAt = observedAtUtc;
        activation.MetaJson = BuildActivationMeta(appVersion, observedAtUtc);
    }

    private static string? ToDeviceHint(string? fingerprintHash)
    {
        if (string.IsNullOrWhiteSpace(fingerprintHash))
        {
            return null;
        }

        var value = fingerprintHash.Trim();
        if (value.Length <= 10)
        {
            return value;
        }

        return $"{value[..6]}...{value[^4..]}";
    }
}
