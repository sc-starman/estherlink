using OmniRelay.Core.Configuration;
using OmniRelay.Ipc;

namespace OmniRelay.UI.Services;

public interface IGatewayClientService
{
    Task<IpcResponse?> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> GetAppStatusAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> ListRelaysAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> UpsertRelayAsync(RelayConfig relay, CancellationToken cancellationToken = default);
    Task<IpcResponse?> DeleteRelayAsync(string relayId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> SetRelayEnabledAsync(string relayId, bool enabled, CancellationToken cancellationToken = default);
    Task<IpcResponse?> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> SetLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default);
    Task<IpcResponse?> RequestLicenseTransferAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> SetConfigAsync(ServiceConfig config, CancellationToken cancellationToken = default);
    Task<IpcResponse?> UpdateWhitelistAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default);
    Task<IpcResponse?> GetPolicyListAsync(string listType, string? relayId = null, CancellationToken cancellationToken = default);
    Task<IpcResponse?> BeginPolicyUpdateAsync(string listType, string mode, string? relayId = null, CancellationToken cancellationToken = default);
    Task<IpcResponse?> AppendPolicyEntriesAsync(string sessionId, IReadOnlyList<string> entries, CancellationToken cancellationToken = default);
    Task<IpcResponse?> CommitPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> CancelPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> ListRelayPolicyListsAsync(string? relayId = null, CancellationToken cancellationToken = default);
    Task<IpcResponse?> GetRelayPolicyListAsync(string listId, string? relayId = null, CancellationToken cancellationToken = default);
    Task<IpcResponse?> CreateRelayPolicyListAsync(string label, string listType, string? relayId = null, int? priority = null, CancellationToken cancellationToken = default);
    Task<IpcResponse?> UpdateRelayPolicyListMetaAsync(string listId, string label, string listType, string? relayId = null, CancellationToken cancellationToken = default);
    Task<IpcResponse?> ReorderRelayPolicyListsAsync(IReadOnlyList<string> orderedListIds, string? relayId = null, CancellationToken cancellationToken = default);
    Task<IpcResponse?> ReplaceRelayPolicyListEntriesAsync(string listId, IReadOnlyList<string> entries, string? relayId = null, CancellationToken cancellationToken = default);
    Task<IpcResponse?> DeleteRelayPolicyListAsync(string listId, string? relayId = null, CancellationToken cancellationToken = default);
    Task<IpcResponse?> VerifyLicenseAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StartProxyAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StopProxyAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> TestTunnelConnectionAsync(ServiceConfig config, CancellationToken cancellationToken = default);
    Task<IpcResponse?> ApplyLocalGatewayConfigAsync(LocalGatewayConfig config, CancellationToken cancellationToken = default);
    Task<IpcResponse?> StartLocalGatewayAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StopLocalGatewayAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> RestartLocalGatewayAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> GetLocalGatewayClientsAsync(string relayId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> AddLocalGatewayClientAsync(string email, string relayId, string? remark, CancellationToken cancellationToken = default);
    Task<IpcResponse?> UpdateLocalGatewayClientAsync(LocalGatewayClientRecord client, string relayId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> DeleteLocalGatewayClientAsync(string clientId, string relayId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> BuildLocalGatewayClientConfigAsync(string clientId, string relayId, CancellationToken cancellationToken = default);
}
