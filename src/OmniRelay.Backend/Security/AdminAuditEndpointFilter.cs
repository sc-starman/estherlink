using System.Text;
using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Entities;
using System.Text.Json;

namespace OmniRelay.Backend.Security;

public sealed class AdminAuditEndpointFilter : IEndpointFilter
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<AdminAuditEndpointFilter> _logger;

    public AdminAuditEndpointFilter(AppDbContext dbContext, ILogger<AdminAuditEndpointFilter> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var requestId = http.TraceIdentifier;
        var actor = http.Items.TryGetValue("AdminActor", out var item) ? item?.ToString() ?? "admin:unknown" : "admin:unknown";
        var payloadHash = ComputePayloadHash(context.Arguments);
        var now = DateTimeOffset.UtcNow;

        object? result;
        try
        {
            result = await next(context);
        }
        catch
        {
            await TryWriteAuditAsync(http, actor, payloadHash, 500, requestId, now);
            throw;
        }

        var statusCode = result is IStatusCodeHttpResult status ? status.StatusCode ?? 200 : 200;
        await TryWriteAuditAsync(http, actor, payloadHash, statusCode, requestId, now);
        return result;
    }

    private async Task TryWriteAuditAsync(
        HttpContext http,
        string actor,
        string payloadHash,
        int statusCode,
        string requestId,
        DateTimeOffset createdAt)
    {
        try
        {
            _dbContext.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                Actor = actor,
                Method = http.Request.Method,
                Path = http.Request.Path.ToString(),
                PayloadHash = payloadHash,
                StatusCode = statusCode,
                RequestId = requestId,
                CreatedAt = createdAt
            });
            await _dbContext.SaveChangesAsync(http.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Admin audit write failed for {Method} {Path}; endpoint response is preserved.",
                http.Request.Method,
                http.Request.Path);
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

        return type.Namespace?.StartsWith("OmniRelay.Backend.Contracts", StringComparison.Ordinal) == true;
    }
}
