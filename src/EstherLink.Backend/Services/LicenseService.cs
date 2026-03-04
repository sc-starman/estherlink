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
        var licenseKeyHash = Sha256Util.HashHex(request.LicenseKey);
        var response = new LicenseVerifyResponse
        {
            Valid = false,
            Reason = "INVALID_KEY",
            CacheExpiresAt = now.AddHours(_options.Value.OfflineCacheTtlHours),
            ServerTime = now,
            SignatureAlg = SigningKeyService.SignatureAlgorithm,
            RequestId = requestId
        };

        var license = await _dbContext.Licenses
            .Include(x => x.Activations)
            .FirstOrDefaultAsync(x => x.LicenseKey == request.LicenseKey, cancellationToken);

        if (license is null)
        {
            await SignResponseAsync(response, request.Nonce, cancellationToken);
            return response;
        }

        response.Plan = license.Plan;
        response.LicenseExpiresAt = license.ExpiresAt?.ToUniversalTime();

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

        if (activation is not null)
        {
            activation.LastSeenAt = now;
            activation.MetaJson = JsonSerializer.Serialize(new
            {
                request.AppVersion,
                observedAt = now
            });

            if (activation.IsBlocked)
            {
                response.Reason = "DEVICE_LIMIT";
                await SignResponseAsync(response, request.Nonce, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return response;
            }
        }
        else
        {
            var activeDeviceCount = license.Activations.Count(x => !x.IsBlocked);
            if (activeDeviceCount >= license.MaxDevices)
            {
                response.Reason = "DEVICE_LIMIT";
                await SignResponseAsync(response, request.Nonce, cancellationToken);
                return response;
            }

            var newActivation = new LicenseActivationEntity
            {
                Id = Guid.NewGuid(),
                LicenseId = license.Id,
                FingerprintHash = fingerprintHash,
                FirstSeenAt = now,
                LastSeenAt = now,
                IsBlocked = false,
                MetaJson = JsonSerializer.Serialize(new
                {
                    request.AppVersion,
                    observedAt = now
                })
            };
            _dbContext.LicenseActivations.Add(newActivation);
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
        _logger.LogInformation(
            "License verify requestId={RequestId} licenseKeyHash={LicenseKeyHash} reason={Reason} valid={Valid} plan={Plan}",
            requestId,
            licenseKeyHash,
            response.Reason,
            response.Valid,
            response.Plan);
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
}
