using System.Security.Cryptography;
using Chaos.NaCl;
using EstherLink.Backend.Configuration;
using EstherLink.Backend.Contracts.Licensing;
using EstherLink.Backend.Data;
using EstherLink.Backend.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Services;

public sealed class SigningKeyService
{
    public const string SignatureAlgorithm = "Ed25519";

    private readonly AppDbContext _dbContext;
    private readonly IOptions<LicensingOptions> _options;

    public SigningKeyService(AppDbContext dbContext, IOptions<LicensingOptions> options)
    {
        _dbContext = dbContext;
        _options = options;
    }

    public async Task EnsureActiveSigningKeyAsync(CancellationToken cancellationToken)
    {
        var active = await GetActiveSigningKeyAsync(cancellationToken);
        if (active is not null)
        {
            return;
        }

        var seed = RandomNumberGenerator.GetBytes(32);
        Ed25519.KeyPairFromSeed(out var publicKey, out var expandedPrivateKey, seed);
        var now = DateTimeOffset.UtcNow;

        var key = new SigningKeyEntity
        {
            Id = Guid.NewGuid(),
            KeyId = $"k-{now:yyyyMMddHHmmss}",
            PublicKey = Convert.ToBase64String(publicKey),
            PrivateKey = Convert.ToBase64String(expandedPrivateKey),
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.Value.SigningKeyRotationDays)
        };

        _dbContext.SigningKeys.Add(key);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SigningKeyEntity?> GetActiveSigningKeyAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await _dbContext.SigningKeys
            .Where(x => x.RevokedAt == null && (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LicensePublicKeyItem>> GetPublicKeysAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var keys = await _dbContext.SigningKeys
            .Where(x => x.RevokedAt == null && (!x.ExpiresAt.HasValue || x.ExpiresAt > now.AddDays(-7)))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return keys.Select(x => new LicensePublicKeyItem
        {
            KeyId = x.KeyId,
            SignatureAlg = SignatureAlgorithm,
            PublicKey = x.PublicKey,
            CreatedAt = x.CreatedAt,
            ExpiresAt = x.ExpiresAt
        }).ToList();
    }

    public string Sign(string payload, SigningKeyEntity key)
    {
        var privateKey = Convert.FromBase64String(key.PrivateKey);
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        return Convert.ToBase64String(Ed25519.Sign(bytes, privateKey));
    }
}
