using System.Security.Cryptography;
using System.Text;
using EstherLink.Backend.Configuration;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Security;

public sealed class AdminApiKeyEndpointFilter : IEndpointFilter
{
    private const string HeaderName = "X-ADMIN-API-KEY";
    private readonly IOptions<AdminSecurityOptions> _options;

    public AdminApiKeyEndpointFilter(IOptions<AdminSecurityOptions> options)
    {
        _options = options;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuredKeys = _options.Value.ApiKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToArray();

        if (configuredKeys.Length == 0)
        {
            return Results.Problem(
                title: "Admin security is not configured.",
                detail: "Set Admin:ApiKeys with at least one key.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var headerValue))
        {
            return Results.Unauthorized();
        }

        var incoming = headerValue.ToString().Trim();
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return Results.Unauthorized();
        }

        if (!configuredKeys.Any(x => FixedTimeEquals(x, incoming)))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
