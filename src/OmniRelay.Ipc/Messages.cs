using System.Text.Json;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Policy;
using OmniRelay.Core.Status;

namespace OmniRelay.Ipc;

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string GetStatus = "get_status";
    public const string GetAppStatus = "get_app_status";
    public const string ListRelays = "list_relays";
    public const string GetRelay = "get_relay";
    public const string UpsertRelay = "upsert_relay";
    public const string DeleteRelay = "delete_relay";
    public const string SetRelayEnabled = "set_relay_enabled";
    public const string SetConfig = "set_config";
    public const string UpdateWhitelist = "update_whitelist";
    public const string GetPolicyList = "get_policy_list";
    public const string BeginPolicyUpdate = "begin_policy_update";
    public const string AppendPolicyEntries = "append_policy_entries";
    public const string CommitPolicyUpdate = "commit_policy_update";
    public const string CancelPolicyUpdate = "cancel_policy_update";
    public const string ListRelayPolicyLists = "list_relay_policy_lists";
    public const string GetRelayPolicyList = "get_relay_policy_list";
    public const string CreateRelayPolicyList = "create_relay_policy_list";
    public const string UpdateRelayPolicyListMeta = "update_relay_policy_list_meta";
    public const string ReorderRelayPolicyLists = "reorder_relay_policy_lists";
    public const string ReplaceRelayPolicyListEntries = "replace_relay_policy_list_entries";
    public const string DeleteRelayPolicyList = "delete_relay_policy_list";
    public const string StartProxy = "start_proxy";
    public const string StopProxy = "stop_proxy";
    public const string VerifyLicense = "verify_license";
    public const string RequestLicenseTransfer = "request_license_transfer";
    public const string SetLicenseKey = "set_license_key";
    public const string GetCapabilities = "get_capabilities";
    public const string TestTunnelConnection = "test_tunnel_connection";
    public const string ApplyLocalGatewayConfig = "apply_local_gateway_config";
    public const string StartLocalGateway = "start_local_gateway";
    public const string StopLocalGateway = "stop_local_gateway";
    public const string RestartLocalGateway = "restart_local_gateway";
    public const string GetLocalGatewayClients = "get_local_gateway_clients";
    public const string AddLocalGatewayClient = "add_local_gateway_client";
    public const string UpdateLocalGatewayClient = "update_local_gateway_client";
    public const string DeleteLocalGatewayClient = "delete_local_gateway_client";
    public const string BuildLocalGatewayClientConfig = "build_local_gateway_client_config";
}

public sealed record IpcRequest(string Command, string? JsonPayload = null);

public sealed record IpcResponse(bool Success, string? Error = null, string? JsonPayload = null);

public sealed record SetConfigRequest(ServiceConfig Config);

public sealed record RelayIdRequest(string RelayId);
public sealed record UpsertRelayRequest(RelayConfig Relay);
public sealed record DeleteRelayRequest(string RelayId);
public sealed record SetRelayEnabledRequest(string RelayId, bool Enabled);
public sealed record RelaysResponse(IReadOnlyList<RelayConfig> Relays);
public sealed record RelayResponse(RelayConfig Relay);

public sealed record UpdateWhitelistRequest(IReadOnlyList<string> Entries, string? RelayId = null);
public sealed record GetPolicyListRequest(string ListType, string? RelayId = null);
public sealed record GetPolicyListResponse(
    string ListType,
    IReadOnlyList<string> Entries,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);
public sealed record BeginPolicyUpdateRequest(string ListType, string Mode, string? RelayId = null);
public sealed record BeginPolicyUpdateResponse(string SessionId, string ListType, string Mode, string? RelayId = null);
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

public sealed record RelayPolicyListDto(
    string ListId,
    string RelayId,
    string Label,
    string ListType,
    int Priority,
    int EntryCount);

public sealed record ListRelayPolicyListsRequest(string? RelayId = null);
public sealed record ListRelayPolicyListsResponse(
    string RelayId,
    IReadOnlyList<RelayPolicyListDto> Lists,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    int TotalListCount,
    int TotalEntryCount,
    int WhitelistListCount,
    int BlacklistListCount);

public sealed record GetRelayPolicyListRequest(string ListId, string? RelayId = null);
public sealed record GetRelayPolicyListResponse(
    string ListId,
    string RelayId,
    string Label,
    string ListType,
    int Priority,
    IReadOnlyList<string> Entries,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateRelayPolicyListRequest(string Label, string ListType, string? RelayId = null, int? Priority = null);
public sealed record UpdateRelayPolicyListMetaRequest(string ListId, string Label, string ListType, string? RelayId = null);
public sealed record ReorderRelayPolicyListsRequest(IReadOnlyList<string> OrderedListIds, string? RelayId = null);
public sealed record ReplaceRelayPolicyListEntriesRequest(string ListId, IReadOnlyList<string> Entries, string? RelayId = null);
public sealed record DeleteRelayPolicyListRequest(string ListId, string? RelayId = null);
public sealed record RelayPolicyListMutationResponse(
    string ListId,
    string RelayId,
    string Label,
    string ListType,
    int Priority,
    int EntryCount,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record ReplaceRelayPolicyListEntriesResponse(
    string ListId,
    int AppliedCount,
    int DuplicateDroppedCount,
    int InvalidCount,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record StatusResponse(GatewayStatus Status);
public sealed record AppStatusResponse(AppStatus Status);

public sealed record VerifyLicenseResponse(
    bool IsValid,
    DateTimeOffset? ExpiresAtUtc,
    bool FromCache,
    string? Error,
    string Source = "unknown",
    string? Reason = null,
    bool TransferRequired = false,
    int TransferLimitPerRollingYear = 0,
    int TransfersUsedInWindow = 0,
    int TransfersRemainingInWindow = 0,
    DateTimeOffset? TransferWindowStartAt = null,
    string? ActiveDeviceIdHint = null);

public sealed record SetLicenseKeyRequest(string LicenseKey);

public sealed record CapabilitiesResponse(string ServiceVersion, IReadOnlyList<string> Capabilities);

public sealed record TestTunnelConnectionRequest(ServiceConfig Config, RelayConfig? Relay = null);

public sealed record ApplyLocalGatewayConfigRequest(LocalGatewayConfig Config, string? RelayId = null);

public sealed record LocalGatewayClientRecord(
    string Id,
    string Email,
    bool Enabled,
    string Remark,
    string Protocol,
    string Username,
    string Secret,
    double TotalGB,
    long ExpiryTime,
    int SpeedLimitKbps,
    long LastSeenAtUnixMs,
    int ActiveConnections,
    DateTimeOffset CreatedAtUtc);

public sealed record LocalGatewayClientsResponse(
    string Protocol,
    int Port,
    IReadOnlyList<LocalGatewayClientRecord> Clients);

public sealed record AddLocalGatewayClientRequest(string Email, string RelayId, string? Remark = null);
public sealed record UpdateLocalGatewayClientRequest(LocalGatewayClientRecord Client, string RelayId);
public sealed record DeleteLocalGatewayClientRequest(string ClientId, string RelayId);
public sealed record BuildLocalGatewayClientConfigRequest(string ClientId, string RelayId);
public sealed record LocalGatewayClientConfigResponse(
    string Mode,
    string Uri,
    string Title,
    string Username,
    string Password,
    string OvpnFileName,
    string OvpnContent);

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
