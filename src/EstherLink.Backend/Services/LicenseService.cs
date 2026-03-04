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

    public LicenseService(
        AppDbContext dbContext,
        LicenseResponseSigner signer,
        IOptions<LicensingOptions> options)
    {
        _dbContext = dbContext;
        _signer = signer;
        _options = options;
    }

    public async Task<LicenseVerifyResponse> VerifyAsync(LicenseVerifyRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var response = new LicenseVerifyResponse
        {
            Valid = false,
            Reason = "INVALID_KEY",
            CacheExpiresAt = now.AddHours(_options.Value.OfflineCacheTtlHours),
            ServerTime = now
        };

        var license = await _dbContext.Licenses
            .Include(x => x.Activations)
            .FirstOrDefaultAsync(x => x.LicenseKey == request.LicenseKey, cancellationToken);

        if (license is null)
        {
            response.Signature = _signer.Sign(response, request.Nonce);
            return response;
        }

        response.Plan = license.Plan;
        response.LicenseExpiresAt = license.ExpiresAt?.ToUniversalTime();

        if (license.Status == LicenseStatus.Revoked)
        {
            response.Reason = "REVOKED";
            response.Signature = _signer.Sign(response, request.Nonce);
            return response;
        }

        if (license.Status == LicenseStatus.Suspended)
        {
            response.Reason = "SUSPENDED";
            response.Signature = _signer.Sign(response, request.Nonce);
            return response;
        }

        if (license.ExpiresAt.HasValue && license.ExpiresAt.Value < now)
        {
            response.Reason = "EXPIRED";
            response.Signature = _signer.Sign(response, request.Nonce);
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
                response.Signature = _signer.Sign(response, request.Nonce);
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
                response.Signature = _signer.Sign(response, request.Nonce);
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
        response.Signature = _signer.Sign(response, request.Nonce);
        return response;
    }
}
