using EstherLink.Core.Configuration;
using EstherLink.Ipc;

namespace EstherLink.UI.Services;

public sealed class GatewayClientService : IGatewayClientService
{
    private readonly NamedPipeJsonClient _client = new(PipeNames.Control);

    public Task<IpcResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetStatus, null, cancellationToken);
    }

    public Task<IpcResponse?> SetConfigAsync(ServiceConfig config, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.SetConfig, new SetConfigRequest(config), cancellationToken);
    }

    public Task<IpcResponse?> UpdateWhitelistAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.UpdateWhitelist, new UpdateWhitelistRequest(entries), cancellationToken);
    }

    public Task<IpcResponse?> VerifyLicenseAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.VerifyLicense, null, cancellationToken);
    }

    public Task<IpcResponse?> StartProxyAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.StartProxy, null, cancellationToken);
    }

    public Task<IpcResponse?> StopProxyAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.StopProxy, null, cancellationToken);
    }

    private async Task<IpcResponse?> SendAsync(string command, object? payload, CancellationToken cancellationToken)
    {
        try
        {
            var request = new IpcRequest(command, payload is null ? null : IpcJson.Serialize(payload));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(15));
            return await _client.SendAsync(request);
        }
        catch
        {
            return null;
        }
    }
}
