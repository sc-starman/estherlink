using EstherLink.Ipc;
using EstherLink.Service.Runtime;

namespace EstherLink.Service.Ipc;

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
                IpcCommands.SetConfig => HandleSetConfig(request.JsonPayload),
                IpcCommands.UpdateWhitelist => HandleUpdateWhitelist(request.JsonPayload),
                IpcCommands.StartProxy => HandleStartProxy(),
                IpcCommands.StopProxy => HandleStopProxy(),
                IpcCommands.VerifyLicense => await HandleVerifyLicenseAsync(cancellationToken),
                IpcCommands.TestTunnelConnection => await HandleTestTunnelConnectionAsync(request.JsonPayload, cancellationToken),
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

        if (!_runtime.TryUpdateWhitelist(payload.Entries, out var error))
        {
            return new IpcResponse(false, error ?? "Invalid whitelist.");
        }

        _fileLog.Info($"Whitelist updated via IPC ({payload.Entries.Count} entries).");
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
        var result = await _licenseValidator.ValidateAsync(config, forceOnline: true, cancellationToken);
        _runtime.SetLicenseStatus(result);

        var response = new VerifyLicenseResponse(
            result.IsValid,
            result.ExpiresAtUtc,
            result.FromCache,
            result.Error);

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
}
