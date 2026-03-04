using EstherLink.Backend.Configuration;
using EstherLink.Backend.Data;
using EstherLink.Backend.Data.Entities;
using EstherLink.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Services;

public sealed class SecurityBootstrapper
{
    private readonly AppDbContext _dbContext;
    private readonly IOptions<AdminSecurityOptions> _adminOptions;
    private readonly SigningKeyService _signingKeyService;
    private readonly ILogger<SecurityBootstrapper> _logger;

    public SecurityBootstrapper(
        AppDbContext dbContext,
        IOptions<AdminSecurityOptions> adminOptions,
        SigningKeyService signingKeyService,
        ILogger<SecurityBootstrapper> logger)
    {
        _dbContext = dbContext;
        _adminOptions = adminOptions;
        _signingKeyService = signingKeyService;
        _logger = logger;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await _signingKeyService.EnsureActiveSigningKeyAsync(cancellationToken);
        await EnsureAdminApiKeysAsync(cancellationToken);
    }

    private async Task EnsureAdminApiKeysAsync(CancellationToken cancellationToken)
    {
        var configured = _adminOptions.Value.ApiKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (configured.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var key in configured)
        {
            var hash = AdminApiKeyHasher.Hash(key, _adminOptions.Value.ApiKeyPepper);
            var exists = await _dbContext.AdminApiKeys.AnyAsync(x => x.KeyHash == hash, cancellationToken);
            if (exists)
            {
                continue;
            }

            _dbContext.AdminApiKeys.Add(new AdminApiKeyEntity
            {
                Id = Guid.NewGuid(),
                Name = "bootstrap",
                KeyHash = hash,
                CreatedAt = now
            });

            _logger.LogWarning("Inserted bootstrap admin API key hash from configuration.");
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
