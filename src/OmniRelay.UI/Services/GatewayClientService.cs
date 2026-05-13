using OmniRelay.Core.Configuration;
using OmniRelay.Ipc;

namespace OmniRelay.UI.Services;

public sealed class GatewayClientService : IGatewayClientService
{
    private readonly NamedPipeJsonClient _client = new(PipeNames.Control);

    public Task<IpcResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetStatus, null, cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> GetAppStatusAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetAppStatus, null, cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> ListRelaysAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.ListRelays, null, cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetRelay, new RelayIdRequest(relayId ?? string.Empty), cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> UpsertRelayAsync(RelayConfig relay, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.UpsertRelay, new UpsertRelayRequest(relay), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> DeleteRelayAsync(string relayId, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.DeleteRelay, new DeleteRelayRequest(relayId ?? string.Empty), cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> SetRelayEnabledAsync(string relayId, bool enabled, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.SetRelayEnabled, new SetRelayEnabledRequest(relayId ?? string.Empty, enabled), cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetCapabilities, null, cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> SetLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.SetLicenseKey, new SetLicenseKeyRequest(licenseKey ?? string.Empty), cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> RequestLicenseTransferAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.RequestLicenseTransfer, null, cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> SetConfigAsync(ServiceConfig config, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.SetConfig, new SetConfigRequest(config), cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> UpdateWhitelistAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.UpdateWhitelist, new UpdateWhitelistRequest(entries), cancellationToken, TimeSpan.FromSeconds(15));
    }

    public Task<IpcResponse?> GetPolicyListAsync(string listType, string? relayId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetPolicyList, new GetPolicyListRequest(listType, relayId), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> BeginPolicyUpdateAsync(string listType, string mode, string? relayId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.BeginPolicyUpdate, new BeginPolicyUpdateRequest(listType, mode, relayId), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> AppendPolicyEntriesAsync(string sessionId, IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.AppendPolicyEntries, new AppendPolicyEntriesRequest(sessionId, entries), cancellationToken, TimeSpan.FromSeconds(60));
    }

    public Task<IpcResponse?> CommitPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.CommitPolicyUpdate, new CommitPolicyUpdateRequest(sessionId), cancellationToken, TimeSpan.FromSeconds(60));
    }

    public Task<IpcResponse?> CancelPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.CancelPolicyUpdate, new CancelPolicyUpdateRequest(sessionId), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> ListRelayPolicyListsAsync(string? relayId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.ListRelayPolicyLists, new ListRelayPolicyListsRequest(relayId), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> GetRelayPolicyListAsync(string listId, string? relayId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetRelayPolicyList, new GetRelayPolicyListRequest(listId, relayId), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> CreateRelayPolicyListAsync(string label, string listType, string? relayId = null, int? priority = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.CreateRelayPolicyList, new CreateRelayPolicyListRequest(label, listType, relayId, priority), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> UpdateRelayPolicyListMetaAsync(string listId, string label, string listType, string? relayId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.UpdateRelayPolicyListMeta, new UpdateRelayPolicyListMetaRequest(listId, label, listType, relayId), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> ReorderRelayPolicyListsAsync(IReadOnlyList<string> orderedListIds, string? relayId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.ReorderRelayPolicyLists, new ReorderRelayPolicyListsRequest(orderedListIds, relayId), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> ReplaceRelayPolicyListEntriesAsync(string listId, IReadOnlyList<string> entries, string? relayId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.ReplaceRelayPolicyListEntries, new ReplaceRelayPolicyListEntriesRequest(listId, entries, relayId), cancellationToken, TimeSpan.FromSeconds(60));
    }

    public Task<IpcResponse?> DeleteRelayPolicyListAsync(string listId, string? relayId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.DeleteRelayPolicyList, new DeleteRelayPolicyListRequest(listId, relayId), cancellationToken, TimeSpan.FromSeconds(20));
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

    public Task<IpcResponse?> ApplyLocalGatewayConfigAsync(LocalGatewayConfig config, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.ApplyLocalGatewayConfig, new ApplyLocalGatewayConfigRequest(config), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> StartLocalGatewayAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.StartLocalGateway, null, cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> StopLocalGatewayAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.StopLocalGateway, null, cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> RestartLocalGatewayAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.RestartLocalGateway, null, cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> GetLocalGatewayClientsAsync(string relayId, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.GetLocalGatewayClients, new RelayIdRequest(relayId ?? string.Empty), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> AddLocalGatewayClientAsync(string email, string relayId, string? remark, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.AddLocalGatewayClient, new AddLocalGatewayClientRequest(email ?? string.Empty, relayId ?? string.Empty, remark), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> UpdateLocalGatewayClientAsync(LocalGatewayClientRecord client, string relayId, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.UpdateLocalGatewayClient, new UpdateLocalGatewayClientRequest(client, relayId ?? string.Empty), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> DeleteLocalGatewayClientAsync(string clientId, string relayId, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.DeleteLocalGatewayClient, new DeleteLocalGatewayClientRequest(clientId ?? string.Empty, relayId ?? string.Empty), cancellationToken, TimeSpan.FromSeconds(20));
    }

    public Task<IpcResponse?> BuildLocalGatewayClientConfigAsync(string clientId, string relayId, CancellationToken cancellationToken = default)
    {
        return SendAsync(IpcCommands.BuildLocalGatewayClientConfig, new BuildLocalGatewayClientConfigRequest(clientId ?? string.Empty, relayId ?? string.Empty), cancellationToken, TimeSpan.FromSeconds(20));
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
