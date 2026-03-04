using System.Text.Json;
using EstherLink.Core.Configuration;
using EstherLink.Core.Status;

namespace EstherLink.Ipc;

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string GetStatus = "get_status";
    public const string SetConfig = "set_config";
    public const string UpdateWhitelist = "update_whitelist";
    public const string StartProxy = "start_proxy";
    public const string StopProxy = "stop_proxy";
    public const string VerifyLicense = "verify_license";
    public const string TestTunnelConnection = "test_tunnel_connection";
}

public sealed record IpcRequest(string Command, string? JsonPayload = null);

public sealed record IpcResponse(bool Success, string? Error = null, string? JsonPayload = null);

public sealed record SetConfigRequest(ServiceConfig Config);

public sealed record UpdateWhitelistRequest(IReadOnlyList<string> Entries);

public sealed record StatusResponse(GatewayStatus Status);

public sealed record VerifyLicenseResponse(bool IsValid, DateTimeOffset? ExpiresAtUtc, bool FromCache, string? Error);

public sealed record TestTunnelConnectionRequest(ServiceConfig Config);

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, Options);
    }
}
