using System.Text.Json;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Policy;
using OmniRelay.Core.Status;

namespace OmniRelay.Ipc;

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string GetStatus = "get_status";
    public const string SetConfig = "set_config";
    public const string UpdateWhitelist = "update_whitelist";
    public const string GetPolicyList = "get_policy_list";
    public const string BeginPolicyUpdate = "begin_policy_update";
    public const string AppendPolicyEntries = "append_policy_entries";
    public const string CommitPolicyUpdate = "commit_policy_update";
    public const string CancelPolicyUpdate = "cancel_policy_update";
    public const string StartProxy = "start_proxy";
    public const string StopProxy = "stop_proxy";
    public const string VerifyLicense = "verify_license";
    public const string RequestLicenseTransfer = "request_license_transfer";
    public const string SetLicenseKey = "set_license_key";
    public const string GetCapabilities = "get_capabilities";
    public const string TestTunnelConnection = "test_tunnel_connection";
}

public sealed record IpcRequest(string Command, string? JsonPayload = null);

public sealed record IpcResponse(bool Success, string? Error = null, string? JsonPayload = null);

public sealed record SetConfigRequest(ServiceConfig Config);

public sealed record UpdateWhitelistRequest(IReadOnlyList<string> Entries);
public sealed record GetPolicyListRequest(string ListType);
public sealed record GetPolicyListResponse(
    string ListType,
    IReadOnlyList<string> Entries,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record BeginPolicyUpdateRequest(string ListType, string Mode);
public sealed record BeginPolicyUpdateResponse(string SessionId, string ListType, string Mode);
public sealed record AppendPolicyEntriesRequest(string SessionId, IReadOnlyList<string> Entries);
public sealed record CommitPolicyUpdateRequest(string SessionId);
public sealed record CancelPolicyUpdateRequest(string SessionId);
public sealed record CommitPolicyUpdateResponse(
    string ListType,
    string Mode,
    int AppliedCount,
    int DuplicateDroppedCount,
    int InvalidCount,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record StatusResponse(GatewayStatus Status);

public sealed record VerifyLicenseResponse(
    bool IsValid,
    DateTimeOffset? ExpiresAtUtc,
    bool FromCache,
    string? Error,
    string? Reason = null,
    bool TransferRequired = false,
    int TransferLimitPerRollingYear = 0,
    int TransfersUsedInWindow = 0,
    int TransfersRemainingInWindow = 0,
    DateTimeOffset? TransferWindowStartAt = null,
    string? ActiveDeviceIdHint = null);

public sealed record SetLicenseKeyRequest(string LicenseKey);

public sealed record CapabilitiesResponse(string ServiceVersion, IReadOnlyList<string> Capabilities);

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
