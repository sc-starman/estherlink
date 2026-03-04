using EstherLink.Core.Configuration;
using EstherLink.Ipc;

namespace EstherLink.UI.Services;

public interface IGatewayClientService
{
    Task<IpcResponse?> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> SetConfigAsync(ServiceConfig config, CancellationToken cancellationToken = default);
    Task<IpcResponse?> UpdateWhitelistAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default);
    Task<IpcResponse?> VerifyLicenseAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StartProxyAsync(CancellationToken cancellationToken = default);
    Task<IpcResponse?> StopProxyAsync(CancellationToken cancellationToken = default);
}
