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
    Task<IpcResponse?> VerifyLicenseAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StartProxyAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StopProxyAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> TestTunnelConnectionAsync(ServiceConfig config, CancellationToken cancellationToken = default);
}
