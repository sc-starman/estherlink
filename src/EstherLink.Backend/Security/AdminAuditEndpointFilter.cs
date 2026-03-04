using System.Text;
using EstherLink.Backend.Data;
using EstherLink.Backend.Data.Entities;
using System.Text.Json;

namespace EstherLink.Backend.Security;

public sealed class AdminAuditEndpointFilter : IEndpointFilter
{
    private readonly AppDbContext _dbContext;

    public AdminAuditEndpointFilter(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var requestId = http.TraceIdentifier;
        var actor = http.Items.TryGetValue("AdminActor", out var item) ? item?.ToString() ?? "admin:unknown" : "admin:unknown";
        var payloadHash = ComputePayloadHash(context.Arguments);
        var now = DateTimeOffset.UtcNow;

        try
        {
            var result = await next(context);
            var statusCode = result is IStatusCodeHttpResult status ? status.StatusCode ?? 200 : 200;

            _dbContext.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                Actor = actor,
                Method = http.Request.Method,
                Path = http.Request.Path.ToString(),
                PayloadHash = payloadHash,
                StatusCode = statusCode,
                RequestId = requestId,
                CreatedAt = now
            });
            await _dbContext.SaveChangesAsync(http.RequestAborted);

            return result;
        }
        catch
        {
            _dbContext.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                Actor = actor,
                Method = http.Request.Method,
                Path = http.Request.Path.ToString(),
                PayloadHash = payloadHash,
                StatusCode = 500,
                RequestId = requestId,
                CreatedAt = now
            });
            await _dbContext.SaveChangesAsync(http.RequestAborted);
            throw;
        }
    }

    private static string ComputePayloadHash(IList<object?> arguments)
    {
        try
        {
            var filtered = arguments
                .Where(x => IsPayloadArgument(x))
                .Select(x => JsonSerializer.Serialize(x, x!.GetType()));

            var payload = string.Join('|', filtered);
            var hashBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hashBytes);
        }
        catch
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("payload_unavailable")));
        }
    }

    private static bool IsPayloadArgument(object? value)
    {
        if (value is null || value is CancellationToken)
        {
            return false;
        }

        var type = value.GetType();
        if (type == typeof(string) || type.IsPrimitive || type == typeof(Guid) || type == typeof(DateTimeOffset))
        {
            return true;
        }

        return type.Namespace?.StartsWith("EstherLink.Backend.Contracts", StringComparison.Ordinal) == true;
    }
}
