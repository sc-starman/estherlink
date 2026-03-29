using OmniRelay.Core.Configuration;
using OmniRelay.Ipc;

namespace OmniRelay.UI.Services;

public interface IGatewayClientService
{
    Task<IpcResponse?> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> SetLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default);
    Task<IpcResponse?> RequestLicenseTransferAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> SetConfigAsync(ServiceConfig config, CancellationToken cancellationToken = default);
    Task<IpcResponse?> UpdateWhitelistAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default);
    Task<IpcResponse?> GetPolicyListAsync(string listType, CancellationToken cancellationToken = default);
    Task<IpcResponse?> BeginPolicyUpdateAsync(string listType, string mode, CancellationToken cancellationToken = default);
    Task<IpcResponse?> AppendPolicyEntriesAsync(string sessionId, IReadOnlyList<string> entries, CancellationToken cancellationToken = default);
    Task<IpcResponse?> CommitPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> CancelPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> VerifyLicenseAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StartProxyAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StopProxyAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> TestTunnelConnectionAsync(ServiceConfig config, CancellationToken cancellationToken = default);
    Task<IpcResponse?> ApplyLocalGatewayConfigAsync(LocalGatewayConfig config, CancellationToken cancellationToken = default);
    Task<IpcResponse?> StartLocalGatewayAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StopLocalGatewayAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> RestartLocalGatewayAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> GetLocalGatewayClientsAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> AddLocalGatewayClientAsync(string email, string? remark, CancellationToken cancellationToken = default);
    Task<IpcResponse?> UpdateLocalGatewayClientAsync(LocalGatewayClientRecord client, CancellationToken cancellationToken = default);
    Task<IpcResponse?> DeleteLocalGatewayClientAsync(string clientId, CancellationToken cancellationToken = default);
    Task<IpcResponse?> BuildLocalGatewayClientConfigAsync(string clientId, CancellationToken cancellationToken = default);
}
