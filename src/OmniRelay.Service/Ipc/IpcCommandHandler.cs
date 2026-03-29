using OmniRelay.Ipc;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Policy;
using OmniRelay.Service.Runtime;
using System.Collections.Concurrent;

namespace OmniRelay.Service.Ipc;

public sealed class IpcCommandHandler
{
    private readonly GatewayRuntime _runtime;
    private readonly LicenseValidator _licenseValidator;
    private readonly TunnelConnectionTester _tunnelConnectionTester;
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<IpcCommandHandler> _logger;
    private readonly ConcurrentDictionary<string, PolicyUpdateSession> _policySessions = new(StringComparer.Ordinal);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

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
                IpcCommands.SetConfig => HandleSetConfig(request.JsonPayload),
                IpcCommands.SetLicenseKey => HandleSetLicenseKey(request.JsonPayload),
                IpcCommands.RequestLicenseTransfer => HandleRequestLicenseTransfer(),
                IpcCommands.GetCapabilities => HandleGetCapabilities(),
                IpcCommands.UpdateWhitelist => HandleUpdateWhitelist(request.JsonPayload),
                IpcCommands.GetPolicyList => HandleGetPolicyList(request.JsonPayload),
                IpcCommands.BeginPolicyUpdate => HandleBeginPolicyUpdate(request.JsonPayload),
                IpcCommands.AppendPolicyEntries => HandleAppendPolicyEntries(request.JsonPayload),
                IpcCommands.CommitPolicyUpdate => HandleCommitPolicyUpdate(request.JsonPayload),
                IpcCommands.CancelPolicyUpdate => HandleCancelPolicyUpdate(request.JsonPayload),
                IpcCommands.StartProxy => HandleStartProxy(),
                IpcCommands.StopProxy => HandleStopProxy(),
                IpcCommands.VerifyLicense => await HandleVerifyLicenseAsync(cancellationToken),
                IpcCommands.TestTunnelConnection => await HandleTestTunnelConnectionAsync(request.JsonPayload, cancellationToken),
                IpcCommands.ApplyLocalGatewayConfig => HandleApplyLocalGatewayConfig(request.JsonPayload),
                IpcCommands.StartLocalGateway => HandleStartLocalGateway(),
                IpcCommands.StopLocalGateway => HandleStopLocalGateway(),
                IpcCommands.RestartLocalGateway => HandleRestartLocalGateway(),
                IpcCommands.GetLocalGatewayClients => HandleGetLocalGatewayClients(),
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

    private IpcResponse HandleGetCapabilities()
    {
        var capabilities = new[]
        {
            IpcCommands.GetStatus,
            IpcCommands.SetConfig,
            IpcCommands.SetLicenseKey,
            IpcCommands.RequestLicenseTransfer,
            IpcCommands.UpdateWhitelist,
            IpcCommands.GetPolicyList,
            IpcCommands.BeginPolicyUpdate,
            IpcCommands.AppendPolicyEntries,
            IpcCommands.CommitPolicyUpdate,
            IpcCommands.CancelPolicyUpdate,
            "blacklist_supported",
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

    private IpcResponse HandleUpdateWhitelist(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<UpdateWhitelistRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid whitelist payload.");
        }

        if (!_runtime.TryApplyPolicyUpdate(
            PolicyListTypes.Whitelist,
            PolicyUpdateModes.Replace,
            payload.Entries,
            out var result,
            out var error))
        {
            return new IpcResponse(false, error ?? "Invalid whitelist.");
        }

        _fileLog.Info($"Whitelist updated via IPC ({payload.Entries.Count} entries).");
        var payloadJson = result is null
            ? null
            : IpcJson.Serialize(new CommitPolicyUpdateResponse(
                result.ListType,
                result.Mode,
                result.AppliedCount,
                result.DuplicateDroppedCount,
                result.InvalidCount,
                result.Count,
                result.Revision,
                result.UpdatedAtUtc));
        return new IpcResponse(true, JsonPayload: payloadJson);
    }

    private IpcResponse HandleGetPolicyList(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<GetPolicyListRequest>(jsonPayload);
        var listType = PolicyListTypes.Normalize(payload?.ListType);
        var snapshot = _runtime.GetPolicyListSnapshot(listType);
        var response = new GetPolicyListResponse(
            snapshot.ListType,
            snapshot.Entries,
            snapshot.Count,
            snapshot.Revision,
            snapshot.UpdatedAtUtc);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleBeginPolicyUpdate(string? jsonPayload)
    {
        CleanupStaleSessions();
        var payload = IpcJson.Deserialize<BeginPolicyUpdateRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid begin-policy-update payload.");
        }

        var listType = PolicyListTypes.Normalize(payload.ListType);
        var mode = PolicyUpdateModes.Normalize(payload.Mode);
        var sessionId = Guid.NewGuid().ToString("N");
        _policySessions[sessionId] = new PolicyUpdateSession(listType, mode);

        var response = new BeginPolicyUpdateResponse(sessionId, listType, mode);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleAppendPolicyEntries(string? jsonPayload)
    {
        CleanupStaleSessions();
        var payload = IpcJson.Deserialize<AppendPolicyEntriesRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId))
        {
            return new IpcResponse(false, "Invalid append-policy-entries payload.");
        }

        if (!_policySessions.TryGetValue(payload.SessionId, out var session))
        {
            return new IpcResponse(false, "Policy update session was not found or expired.");
        }

        session.Entries.AddRange(payload.Entries.Where(x => !string.IsNullOrWhiteSpace(x)));
        session.Touch();
        return new IpcResponse(true);
    }

    private IpcResponse HandleCommitPolicyUpdate(string? jsonPayload)
    {
        CleanupStaleSessions();
        var payload = IpcJson.Deserialize<CommitPolicyUpdateRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId))
        {
            return new IpcResponse(false, "Invalid commit-policy-update payload.");
        }

        if (!_policySessions.TryRemove(payload.SessionId, out var session))
        {
            return new IpcResponse(false, "Policy update session was not found or expired.");
        }

        if (!_runtime.TryApplyPolicyUpdate(session.ListType, session.Mode, session.Entries, out var result, out var error))
        {
            return new IpcResponse(false, error ?? "Policy update failed.");
        }

        var response = new CommitPolicyUpdateResponse(
            session.ListType,
            session.Mode,
            result?.AppliedCount ?? 0,
            result?.DuplicateDroppedCount ?? 0,
            result?.InvalidCount ?? 0,
            result?.Count ?? 0,
            result?.Revision ?? 0,
            result?.UpdatedAtUtc ?? DateTimeOffset.MinValue);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private IpcResponse HandleCancelPolicyUpdate(string? jsonPayload)
    {
        CleanupStaleSessions();
        var payload = IpcJson.Deserialize<CancelPolicyUpdateRequest>(jsonPayload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId))
        {
            return new IpcResponse(false, "Invalid cancel-policy-update payload.");
        }

        _policySessions.TryRemove(payload.SessionId, out _);
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
            forceOnline: true,
            transferRequested: transferRequested,
            cancellationToken: verifyCts.Token);
        _runtime.SetLicenseStatus(result);

        var response = new VerifyLicenseResponse(
            result.IsValid,
            result.ExpiresAtUtc,
            result.FromCache,
            result.Error,
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

    private IpcResponse HandleGetLocalGatewayClients()
    {
        var config = _runtime.GetConfigSnapshot().LocalGateway;
        var clients = _runtime.GetLocalGatewayClientsSnapshot()
            .Select(x => new LocalGatewayClientRecord(
                x.Id,
                x.Email,
                x.Enabled,
                x.Remark,
                x.Protocol,
                x.Secret,
                x.CreatedAtUtc))
            .ToArray();

        var payload = new LocalGatewayClientsResponse(
            LocalGatewayProtocols.Normalize(config.Protocol),
            config.Port,
            clients);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(payload));
    }

    private IpcResponse HandleAddLocalGatewayClient(string? jsonPayload)
    {
        var payload = IpcJson.Deserialize<AddLocalGatewayClientRequest>(jsonPayload);
        if (payload is null)
        {
            return new IpcResponse(false, "Invalid add-local-client payload.");
        }

        if (!_runtime.TryAddLocalGatewayClient(payload.Email, payload.Remark, out var client, out var error) || client is null)
        {
            return new IpcResponse(false, error ?? "Failed adding local gateway client.");
        }

        var response = new LocalGatewayClientRecord(
            client.Id,
            client.Email,
            client.Enabled,
            client.Remark,
            client.Protocol,
            client.Secret,
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

        var runtimeModel = new LocalGatewayClient
        {
            Id = payload.Client.Id,
            Email = payload.Client.Email,
            Enabled = payload.Client.Enabled,
            Remark = payload.Client.Remark,
            Protocol = payload.Client.Protocol,
            Secret = payload.Client.Secret,
            CreatedAtUtc = payload.Client.CreatedAtUtc
        };

        if (!_runtime.TryUpdateLocalGatewayClient(runtimeModel, out var error))
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

        if (!_runtime.TryDeleteLocalGatewayClient(payload.ClientId, out var error))
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

        if (!_runtime.TryBuildLocalGatewayClientUri(payload.ClientId, out var uri, out var title, out var error))
        {
            return new IpcResponse(false, error ?? "Failed building local gateway client config.");
        }

        var response = new LocalGatewayClientConfigResponse(uri, title);
        return new IpcResponse(true, JsonPayload: IpcJson.Serialize(response));
    }

    private void CleanupStaleSessions()
    {
        var threshold = DateTimeOffset.UtcNow - SessionTtl;
        foreach (var kvp in _policySessions)
        {
            if (kvp.Value.LastUpdatedUtc < threshold)
            {
                _policySessions.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class PolicyUpdateSession
    {
        public PolicyUpdateSession(string listType, string mode)
        {
            ListType = listType;
            Mode = mode;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
        }

        public string ListType { get; }
        public string Mode { get; }
        public List<string> Entries { get; } = [];
        public DateTimeOffset LastUpdatedUtc { get; private set; }

        public void Touch()
        {
            LastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }
}
