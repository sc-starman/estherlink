using EstherLink.Backend.Configuration;
using EstherLink.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Security;

public sealed class AdminApiKeyEndpointFilter : IEndpointFilter
{
    private const string HeaderName = "X-ADMIN-API-KEY";
    private readonly IOptions<AdminSecurityOptions> _options;
    private readonly AppDbContext _dbContext;

    public AdminApiKeyEndpointFilter(IOptions<AdminSecurityOptions> options, AppDbContext dbContext)
    {
        _options = options;
        _dbContext = dbContext;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var headerValue))
        {
            return Results.Unauthorized();
        }

        var incoming = headerValue.ToString().Trim();
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return Results.Unauthorized();
        }

        var hash = AdminApiKeyHasher.Hash(incoming, _options.Value.ApiKeyPepper);
        var now = DateTimeOffset.UtcNow;
        var isValid = await _dbContext.AdminApiKeys.AnyAsync(
            x => x.KeyHash == hash && x.RevokedAt == null && (!x.ExpiresAt.HasValue || x.ExpiresAt > now),
            context.HttpContext.RequestAborted);

        if (!isValid)
        {
            return Results.Unauthorized();
        }

        context.HttpContext.Items["AdminActor"] = $"admin:{hash[..12]}";
        return await next(context);
    }
}
