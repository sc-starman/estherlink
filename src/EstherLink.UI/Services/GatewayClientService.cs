using EstherLink.Core.Configuration;
using EstherLink.Ipc;

namespace EstherLink.UI.Services;

public sealed class GatewayClientService : IGatewayClientService
{
    private readonly NamedPipeJsonClient _client = new(PipeNames.Control);

    public Task<IpcResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetStatus, null, cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetCapabilities, null, cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> SetLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.SetLicenseKey, new SetLicenseKeyRequest(licenseKey ?? string.Empty), cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> SetConfigAsync(ServiceConfig config, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.SetConfig, new SetConfigRequest(config), cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> UpdateWhitelistAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.UpdateWhitelist, new UpdateWhitelistRequest(entries), cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> VerifyLicenseAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.VerifyLicense, null, cancellationToken, TimeSpan.FromSeconds(130));
    }

    public Task<IpcResponse?> StartProxyAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.StartProxy, null, cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> StopProxyAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.StopProxy, null, cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> TestTunnelConnectionAsync(ServiceConfig config, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.TestTunnelConnection, new TestTunnelConnectionRequest(config), cancellationToken, TimeSpan.FromSeconds(20));
    }

    private async Task<IpcResponse?> SendAsync(
        string command,
        object? payload,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        try
        {
            var request = new IpcRequest(command, payload is null ? null : IpcJson.Serialize(payload));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            return await _client.SendAsync(request, linked.Token);
        }
        catch (OperationCanceledException)
        {
            return new IpcResponse(false, "IPC timeout while waiting for service response.");
        }
        catch (Exception ex)
        {
            return new IpcResponse(false, $"IPC error: {ex.Message}");
        }
    }
}
