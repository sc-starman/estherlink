using OmniRelay.Ipc;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Policy;
using OmniRelay.Service.Runtime;

namespace OmniRelay.Service.Ipc;

public sealed class IpcCommandHandler
{
    private readonly GatewayRuntime _runtime;
    private readonly LicenseValidator _licenseValidator;
    private readonly TunnelConnectionTester _tunnelConnectionTester;
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<IpcCommandHandler> _logger;

    public IpcCommandHandler(
        GatewayRuntime runtime,
        LicenseValidator licenseValidator,
        TunnelConnectionTester tunnelConnectionTester,
        FileLogWriter fileLog,
        ILogger<IpcCommandHandler> logger)
    {
        _runtime = runtime;
        _licenseValidator = licenseValidator;
        _tunnelConnectionTester = tunnelConnectionTester;
        _fileLog = fileLog;
        _logger = logger;
    }

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Command switch
            {
                IpcCommands.Ping => new IpcResponse(true),
                IpcCommands.GetStatus => HandleGetStatus(),
                IpcCommands.GetAppStatus => HandleGetAppStatus(),
                IpcCommands.ListRelays => HandleListRelays(),
                IpcCommands.GetRelay => HandleGetRelay(request.JsonPayload),
                IpcCommands.UpsertRelay => HandleUpsertRelay(request.JsonPayload),
                IpcCommands.DeleteRelay => HandleDeleteRelay(request.JsonPayload),
                IpcCommands.SetRelayEnabled => HandleSetRelayEnabled(request.JsonPayload),
                IpcCommands.SetConfig => HandleSetConfig(request.JsonPayload),
                IpcCommands.SetLicenseKey => HandleSetLicenseKey(request.JsonPayload),
                IpcCommands.RequestLicenseTransfer => HandleRequestLicenseTransfer(),
                IpcCommands.GetCapabilities => HandleGetCapabilities(),
                IpcCommands.ListRelayPolicyLists => HandleListRelayPolicyLists(request.JsonPayload),
                IpcCommands.GetRelayPolicyList => HandleGetRelayPolicyList(request.JsonPayload),
                IpcCommands.CreateRelayPolicyList => HandleCreateRelayPolicyList(request.JsonPayload),
                IpcCommands.UpdateRelayPolicyListMeta => HandleUpdateRelayPolicyListMeta(request.JsonPayload),
                IpcCommands.ReorderRelayPolicyLists => HandleReorderRelayPolicyLists(request.JsonPayload),
                IpcCommands.ReplaceRelayPolicyListEntries => HandleReplaceRelayPolicyListEntries(request.JsonPayload),
                IpcCommands.DeleteRelayPolicyList => HandleDeleteRelayPolicyList(request.JsonPayload),
                IpcCommands.StartProxy => HandleStartProxy(),
                IpcCommands.StopProxy => HandleStopProxy(),
                IpcCommands.VerifyLicense => await HandleVerifyLicenseAsync(cancellationToken),
                IpcCommands.TestTunnelConnection => await HandleTestTunnelConnectionAsync(request.JsonPayload, cancellationToken),
                IpcCommands.ApplyLocalGatewayConfig => HandleApplyLocalGatewayConfig(request.JsonPayload),
                IpcCommands.StartLocalGateway => HandleStartLocalGateway(),
                IpcCommands.StopLocalGateway => HandleStopLocalGateway(),
                IpcCommands.RestartLocalGateway => HandleRestartLocalGateway(),
                IpcCommands.GetLocalGatewayClients => HandleGetLocalGatewayClients(request.JsonPayload),
                IpcCommands.AddLocalGatewayClient => HandleAddLocalGatewayClient(request.JsonPayload),
                IpcCommands.UpdateLocalGatewayClient => HandleUpdateLocalGatewayClient(request.JsonPayload),
                IpcCommands.DeleteLocalGatewayClient => HandleDeleteLocalGatewayClient(request.JsonPayload),
                IpcCommands.BuildLocalGatewayClientConfig => HandleBuildLocalGatewayClientConfig(request.JsonPayload),
                _ => new IpcResponse(false, $"Unknown command '{request.Command}'.")
            };
        }
        catch (Exception ex)
        {
            _runtime.SetError(ex.Message);
            _logger.LogError(ex, "IPC command {Command} failed.", request.Command);
            _fileLog.Error($"IPC command {request.Command} failed.", ex);
            return new IpcResponse(false, ex.Message);
        }
    }

    private IpcResponse HandleGetStatus()
    {
        var status = _runtime.GetStatusSnapshot();
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(new StatusResponse(status)));
    }

    private IpcResponse HandleGetAppStatus()
    {
        var status = _runtime.GetAppStatusSnapshot();
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(new AppStatusResponse(status)));
    }

    private IpcResponse HandleListRelays()
    {
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(new RelaysResponse(_runtime.ListRelays())));
    }

    private IpcResponse HandleGetRelay(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<RelayIdRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RelayId))
        {
            return new IpcResponse(false, "Relay id is required.");
        }

        return _runtime.TryGetRelay(payload.RelayId, out var relay)
            ? new IpcResponse(true, JsonPayload: IpcJson.Serialize(new RelayResponse(relay)))
            : new IpcResponse(false, "Relay not found.");
    }

    private IpcResponse HandleUpsertRelay(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<UpsertRelayRequest>(jsonPayload);
        if (payload?.Relay is null)
        {
            return new IpcResponse(false, "Invalid relay payload.");
        }

        var relay = _runtime.UpsertRelay(payload.Relay);
        _fileLog.Info($"Relay upserted via IPC. relayId={relay.Id} name={relay.Name}");
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(new RelayResponse(relay)));
    }

    private IpcResponse HandleDeleteRelay(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<DeleteRelayRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RelayId))
        {
            return new IpcResponse(false, "Relay id is required.");
        }

        return _runtime.DeleteRelay(payload.RelayId)
            ? new IpcResponse(true)
            : new IpcResponse(false, "Relay not found.");
    }

    private IpcResponse HandleSetRelayEnabled(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<SetRelayEnabledRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RelayId))
        {
            return new IpcResponse(false, "Relay id is required.");
        }

        return _runtime.SetRelayEnabled(payload.RelayId, payload.Enabled)
            ? new IpcResponse(true)
            : new IpcResponse(false, "Relay not found.");
    }

    private IpcResponse HandleGetCapabilities()
    {
        var capabilities = new[]
        {
            IpcCommands.GetStatus,
            IpcCommands.GetAppStatus,
            IpcCommands.ListRelays,
            IpcCommands.GetRelay,
            IpcCommands.UpsertRelay,
            IpcCommands.DeleteRelay,
            IpcCommands.SetRelayEnabled,
            IpcCommands.SetConfig,
            IpcCommands.SetLicenseKey,
            IpcCommands.RequestLicenseTransfer,
            IpcCommands.ListRelayPolicyLists,
            IpcCommands.GetRelayPolicyList,
            IpcCommands.CreateRelayPolicyList,
            IpcCommands.UpdateRelayPolicyListMeta,
            IpcCommands.ReorderRelayPolicyLists,
            IpcCommands.ReplaceRelayPolicyListEntries,
            IpcCommands.DeleteRelayPolicyList,
            IpcCommands.StartProxy,
            IpcCommands.StopProxy,
            IpcCommands.VerifyLicense,
            IpcCommands.TestTunnelConnection,
            IpcCommands.ApplyLocalGatewayConfig,
            IpcCommands.StartLocalGateway,
            IpcCommands.StopLocalGateway,
            IpcCommands.RestartLocalGateway,
            IpcCommands.GetLocalGatewayClients,
            IpcCommands.AddLocalGatewayClient,
            IpcCommands.UpdateLocalGatewayClient,
            IpcCommands.DeleteLocalGatewayClient,
            IpcCommands.BuildLocalGatewayClientConfig
        };

        var serviceVersion = typeof(IpcCommandHandler).Assembly.GetName().Version?.ToString() ?? "unknown";
        var payload = new CapabilitiesResponse(serviceVersion, capabilities);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(payload));
    }

    private IpcResponse HandleSetConfig(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<SetConfigRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid set-config payload.");
        }

        _runtime.SetConfig(payload.Config);
        _fileLog.Info("Configuration updated via IPC.");
        return new IpcResponse(true);
    }

    private IpcResponse HandleListRelayPolicyLists(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<ListRelayPolicyListsRequest>(jsonPayload);
        var relayId = payload?.RelayId ?? string.Empty;
        var snapshot = _runtime.GetRelayPolicySetSnapshot(relayId);
        var response = new ListRelayPolicyListsResponse(
            snapshot.RelayId,
            snapshot.Lists
                .Select(x => new RelayPolicyListDto(x.ListId, x.RelayId, x.Label, x.ListType, x.Priority, x.EntryCount))
                .ToArray(),
            snapshot.Revision,
            snapshot.UpdatedAtUtc,
            snapshot.TotalListCount,
            snapshot.TotalEntryCount,
            snapshot.WhitelistListCount,
            snapshot.BlacklistListCount);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleGetRelayPolicyList(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<GetRelayPolicyListRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.ListId))
        {
            return new IpcResponse(false, "Policy list id is required.");
        }

        var relayId = payload.RelayId ?? string.Empty;
        var list = _runtime.GetRelayPolicyListSnapshot(relayId, payload.ListId);
        if (list is null)
        {
            return new IpcResponse(false, "Policy list was not found.");
        }

        var set = _runtime.GetRelayPolicySetSnapshot(relayId);
        var response = new GetRelayPolicyListResponse(
            list.ListId,
            list.RelayId,
            list.Label,
            list.ListType,
            list.Priority,
            list.Entries,
            list.EntryCount,
            set.Revision,
            set.UpdatedAtUtc);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleCreateRelayPolicyList(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<CreateRelayPolicyListRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid create-relay-policy-list payload.");
        }

        var summary = _runtime.CreateRelayPolicyList(payload.RelayId ?? string.Empty, payload.Label, payload.ListType, payload.Priority);
        var set = _runtime.GetRelayPolicySetSnapshot(payload.RelayId ?? string.Empty);
        var response = new RelayPolicyListMutationResponse(
            summary.ListId,
            summary.RelayId,
            summary.Label,
            summary.ListType,
            summary.Priority,
            summary.EntryCount,
            set.Revision,
            set.UpdatedAtUtc);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleUpdateRelayPolicyListMeta(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<UpdateRelayPolicyListMetaRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.ListId))
        {
            return new IpcResponse(false, "Invalid update-relay-policy-list-meta payload.");
        }

        var summary = _runtime.UpdateRelayPolicyListMeta(payload.RelayId ?? string.Empty, payload.ListId, payload.Label, payload.ListType);
        var set = _runtime.GetRelayPolicySetSnapshot(payload.RelayId ?? string.Empty);
        var response = new RelayPolicyListMutationResponse(
            summary.ListId,
            summary.RelayId,
            summary.Label,
            summary.ListType,
            summary.Priority,
            summary.EntryCount,
            set.Revision,
            set.UpdatedAtUtc);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleReorderRelayPolicyLists(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<ReorderRelayPolicyListsRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid reorder-relay-policy-lists payload.");
        }

        _runtime.ReorderRelayPolicyLists(payload.RelayId ?? string.Empty, payload.OrderedListIds);
        var set = _runtime.GetRelayPolicySetSnapshot(payload.RelayId ?? string.Empty);
        var response = new ListRelayPolicyListsResponse(
            set.RelayId,
            set.Lists.Select(x => new RelayPolicyListDto(x.ListId, x.RelayId, x.Label, x.ListType, x.Priority, x.EntryCount)).ToArray(),
            set.Revision,
            set.UpdatedAtUtc,
            set.TotalListCount,
            set.TotalEntryCount,
            set.WhitelistListCount,
            set.BlacklistListCount);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleReplaceRelayPolicyListEntries(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<ReplaceRelayPolicyListEntriesRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.ListId))
        {
            return new IpcResponse(false, "Invalid replace-relay-policy-list-entries payload.");
        }

        var result = _runtime.ReplaceRelayPolicyListEntries(payload.RelayId ?? string.Empty, payload.ListId, payload.Entries);
        var response = new ReplaceRelayPolicyListEntriesResponse(
            result.ListId,
            result.AppliedCount,
            result.DuplicateDroppedCount,
            result.InvalidCount,
            result.Count,
            result.Revision,
            result.UpdatedAtUtc);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleDeleteRelayPolicyList(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<DeleteRelayPolicyListRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.ListId))
        {
            return new IpcResponse(false, "Invalid delete-relay-policy-list payload.");
        }

        var deleted = _runtime.DeleteRelayPolicyList(payload.RelayId ?? string.Empty, payload.ListId);
        if (!deleted)
        {
            return new IpcResponse(false, "Policy list was not found.");
        }

        return new IpcResponse(true);
    }

    private IpcResponse HandleSetLicenseKey(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<SetLicenseKeyRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid set-license-key payload.");
        }

        _runtime.SetLicenseKey(payload.LicenseKey?.Trim() ?? string.Empty);
        _fileLog.Info("License key updated via IPC.");
        return new IpcResponse(true);
    }

    private IpcResponse HandleRequestLicenseTransfer()
    {
        _runtime.RequestLicenseTransfer();
        _fileLog.Info("License transfer requested via IPC.");
        return new IpcResponse(true);
    }

    private IpcResponse HandleStartProxy()
    {
        _runtime.RequestProxyStart();
        _fileLog.Info("Proxy start requested via IPC.");
        return new IpcResponse(true);
    }

    private IpcResponse HandleStopProxy()
    {
        _runtime.RequestProxyStop();
        _fileLog.Info("Proxy stop requested via IPC.");
        return new IpcResponse(true);
    }

    private async Task<IpcResponse> HandleVerifyLicenseAsync(CancellationToken cancellationToken)
    {
        var config = _runtime.GetConfigSnapshot();
        var transferRequested = _runtime.ConsumeLicenseTransferRequest();
        using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        verifyCts.CancelAfter(TimeSpan.FromMinutes(2));
        var result = await _licenseValidator.ValidateAsync(
            config,
            forceOnline: transferRequested,
            transferRequested: transferRequested,
            cancellationToken: verifyCts.Token);
        _runtime.SetLicenseStatus(result);

        var response = new VerifyLicenseResponse(
            result.IsValid,
            result.ExpiresAtUtc,
            result.FromCache,
            result.Error,
            result.Source,
            result.Reason,
            result.TransferRequired,
            result.TransferLimitPerRollingYear,
            result.TransfersUsedInWindow,
            result.TransfersRemainingInWindow,
            result.TransferWindowStartAt,
            result.ActiveDeviceIdHint);

        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private async Task<IpcResponse> HandleTestTunnelConnectionAsync(string? jsonPayload, CancellationToken cancellationToken)
    {
        var payload = IpcJson.Deserialize<TestTunnelConnectionRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid test tunnel payload.");
        }

        var result = await _tunnelConnectionTester.TestAsync(payload.Config, cancellationToken);
        if (!result.Success)
        {
            return new IpcResponse(false, result.Message);
        }

        _fileLog.Info("Tunnel connection test succeeded.");
        return new IpcResponse(true);
    }

    private IpcResponse HandleApplyLocalGatewayConfig(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<ApplyLocalGatewayConfigRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid local gateway config payload.");
        }

        if (!_runtime.TryApplyLocalGatewayConfig(payload.Config, out var error))
        {
            return new IpcResponse(false, error ?? "Failed applying local gateway config.");
        }

        _fileLog.Info("Local gateway config applied via IPC.");
        return new IpcResponse(true);
    }

    private IpcResponse HandleStartLocalGateway()
    {
        _runtime.RequestLocalGatewayStart();
        _fileLog.Info("Local gateway start requested via IPC.");
        return new IpcResponse(true);
    }

    private IpcResponse HandleStopLocalGateway()
    {
        _runtime.RequestLocalGatewayStop();
        _fileLog.Info("Local gateway stop requested via IPC.");
        return new IpcResponse(true);
    }

    private IpcResponse HandleRestartLocalGateway()
    {
        _runtime.RequestLocalGatewayRestart();
        _fileLog.Info("Local gateway restart requested via IPC.");
        return new IpcResponse(true);
    }

    private IpcResponse HandleGetLocalGatewayClients(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<RelayIdRequest>(jsonPayload);
        var relayId = (payload?.RelayId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(relayId) || !_runtime.TryGetRelay(relayId, out var relay))
        {
            return new IpcResponse(false, "Relay id is required and must reference an existing relay.");
        }
        var config = relay.LocalGateway;
        var clients = _runtime.GetLocalGatewayClientsSnapshot(relayId)
            .Select(x => new LocalGatewayClientRecord(
                x.Id,
                x.Email,
                x.Enabled,
                x.Remark,
                x.Protocol,
                x.Username,
                x.Secret,
                x.TotalGB,
                x.ExpiryTime,
                x.SpeedLimitKbps,
                x.LastSeenAtUnixMs,
                x.ActiveConnections,
                x.CreatedAtUtc))
            .ToArray();

        var responsePayload = new LocalGatewayClientsResponse(
            LocalGatewayProtocols.Normalize(config.Protocol),
            config.Port,
            clients);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(responsePayload));
    }

    private IpcResponse HandleAddLocalGatewayClient(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<AddLocalGatewayClientRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid add-local-client payload.");
        }

        if (string.IsNullOrWhiteSpace(payload.RelayId))
        {
            return new IpcResponse(false, "Relay id is required.");
        }

        if (!_runtime.TryAddLocalGatewayClient(payload.Email, payload.Remark, payload.RelayId, out var client, out var error) || client is null)
        {
            return new IpcResponse(false, error ?? "Failed adding local gateway client.");
        }

        var response = new LocalGatewayClientRecord(
            client.Id,
            client.Email,
            client.Enabled,
            client.Remark,
            client.Protocol,
            client.Username,
            client.Secret,
            client.TotalGB,
            client.ExpiryTime,
            client.SpeedLimitKbps,
            client.LastSeenAtUnixMs,
            client.ActiveConnections,
            client.CreatedAtUtc);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleUpdateLocalGatewayClient(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<UpdateLocalGatewayClientRequest>(jsonPayload);
        if (payload?.Client is null)
        {
            return new IpcResponse(false, "Invalid update-local-client payload.");
        }
        if (string.IsNullOrWhiteSpace(payload.RelayId))
        {
            return new IpcResponse(false, "Relay id is required.");
        }

        var runtimeModel = new LocalGatewayClient
        {
            Id = payload.Client.Id,
            Email = payload.Client.Email,
            Enabled = payload.Client.Enabled,
            Remark = payload.Client.Remark,
            Protocol = payload.Client.Protocol,
            Username = payload.Client.Username,
            Secret = payload.Client.Secret,
            TotalGB = payload.Client.TotalGB,
            ExpiryTime = payload.Client.ExpiryTime,
            SpeedLimitKbps = payload.Client.SpeedLimitKbps,
            LastSeenAtUnixMs = payload.Client.LastSeenAtUnixMs,
            ActiveConnections = payload.Client.ActiveConnections,
            CreatedAtUtc = payload.Client.CreatedAtUtc
        };

        if (!_runtime.TryUpdateLocalGatewayClient(runtimeModel, payload.RelayId, out var error))
        {
            return new IpcResponse(false, error ?? "Failed updating local gateway client.");
        }

        return new IpcResponse(true);
    }

    private IpcResponse HandleDeleteLocalGatewayClient(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<DeleteLocalGatewayClientRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid delete-local-client payload.");
        }
        if (string.IsNullOrWhiteSpace(payload.RelayId))
        {
            return new IpcResponse(false, "Relay id is required.");
        }

        if (!_runtime.TryDeleteLocalGatewayClient(payload.ClientId, payload.RelayId, out var error))
        {
            return new IpcResponse(false, error ?? "Failed deleting local gateway client.");
        }

        return new IpcResponse(true);
    }

    private IpcResponse HandleBuildLocalGatewayClientConfig(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<BuildLocalGatewayClientConfigRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid build-local-client-config payload.");
        }
        if (string.IsNullOrWhiteSpace(payload.RelayId))
        {
            return new IpcResponse(false, "Relay id is required.");
        }

        if (!_runtime.TryBuildLocalGatewayClientConfig(payload.ClientId, payload.RelayId, out var configPayload, out var error))
        {
            return new IpcResponse(false, error ?? "Failed building local gateway client config.");
        }

        var response = new LocalGatewayClientConfigResponse(
            configPayload.Mode,
            configPayload.Uri,
            configPayload.Title,
            configPayload.Username,
            configPayload.Password,
            configPayload.OvpnFileName,
            configPayload.OvpnContent);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

}
