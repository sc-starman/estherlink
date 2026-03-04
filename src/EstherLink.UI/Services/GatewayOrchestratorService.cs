using EstherLink.Core.Configuration;
using EstherLink.Core.Networking;
using EstherLink.Ipc;
using EstherLink.UI.Models;
using System.Net.Sockets;

namespace EstherLink.UI.Services;

public sealed class GatewayOrchestratorService
{
    private readonly GatewayStateStore _state;
    private readonly IGatewayClientService _gatewayClient;
    private readonly IServiceControlService _serviceControl;

    public GatewayOrchestratorService(
        GatewayStateStore state,
        IGatewayClientService gatewayClient,
        IServiceControlService serviceControl)
    {
        _state = state;
        _gatewayClient = gatewayClient;
        _serviceControl = serviceControl;
    }

    public GatewayStateStore State => _state;

    public void Initialize()
    {
        _state.Adapters.Clear();
        var adapters = NetworkAdapterCatalog.ListIpv4Adapters()
            .Select(x => new AdapterChoiceModel
            {
                IfIndex = x.IfIndex,
                Display = $"{x.Name} (IfIndex={x.IfIndex}) | IPv4={string.Join(",", x.IPv4Addresses)} | GW={(x.HasDefaultGateway ? "Yes" : "No")}"
            })
            .ToList();

        foreach (var adapter in adapters)
        {
            _state.Adapters.Add(adapter);
        }

        if (adapters.Count > 0)
        {
            _state.VpsAdapter = adapters[0];
            _state.OutgoingAdapter = adapters[Math.Min(1, adapters.Count - 1)];
        }
    }

    public async Task<OperationResult> RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _state.ServiceState = await _serviceControl.QueryServiceStateAsync(cancellationToken);

            var response = await _gatewayClient.GetStatusAsync(cancellationToken);
            if (response?.Success == true)
            {
                var payload = IpcJson.Deserialize<StatusResponse>(response.JsonPayload);
                _state.Status = payload?.Status;
            }
            else
            {
                _state.Status = null;
            }

            return SetAction(true, "Status refreshed.");
        }
        catch (Exception ex)
        {
            return SetAction(false, $"Status refresh failed: {ex.Message}");
        }
    }

    public async Task<OperationResult> ApplyConfigAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = BuildConfig();
            var response = await _gatewayClient.SetConfigAsync(config, cancellationToken);
            if (response?.Success == true)
            {
                return SetAction(true, "Configuration updated.");
            }

            return SetAction(false, $"Config update failed: {response?.Error ?? "service unavailable"}");
        }
        catch (Exception ex)
        {
            return SetAction(false, ex.Message);
        }
    }

    public async Task<OperationResult> UpdateWhitelistAsync(CancellationToken cancellationToken = default)
    {
        var entries = _state.WhitelistText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var response = await _gatewayClient.UpdateWhitelistAsync(entries, cancellationToken);
        if (response?.Success == true)
        {
            return SetAction(true, $"Whitelist updated ({entries.Count} lines).");
        }

        return SetAction(false, $"Whitelist update failed: {response?.Error ?? "service unavailable"}");
    }

    public async Task<OperationResult> VerifyLicenseAsync(CancellationToken cancellationToken = default)
    {
        var configResult = await ApplyConfigAsync(cancellationToken);
        if (!configResult.Success)
        {
            return configResult;
        }

        var response = await _gatewayClient.VerifyLicenseAsync(cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"License verification failed: {response?.Error ?? "service unavailable"}");
        }

        var payload = IpcJson.Deserialize<VerifyLicenseResponse>(response.JsonPayload);
        if (payload is null)
        {
            return SetAction(false, "License verification payload was invalid.");
        }

        return payload.IsValid
            ? SetAction(true, $"License valid. Expires: {payload.ExpiresAtUtc:O}. Source: {(payload.FromCache ? "cache" : "online")}.")
            : SetAction(false, $"License invalid: {payload.Error ?? "unknown error"}");
    }

    public async Task<OperationResult> TestTunnelConnectionAsync(CancellationToken cancellationToken = default)
    {
        ServiceConfig config;
        try
        {
            config = BuildConfig();
        }
        catch (Exception ex)
        {
            return SetAction(false, ex.Message);
        }

        var response = await _gatewayClient.TestTunnelConnectionAsync(config, cancellationToken);
        if (response?.Success == true)
        {
            return SetAction(true, "Tunnel connection test succeeded.");
        }

        if (response is null)
        {
            var fallback = await TestSshTcpConnectivityAsync(config, cancellationToken);
            return fallback
                ? SetAction(true, "SSH endpoint is reachable (service not connected for auth-level test).")
                : SetAction(false, "Tunnel test failed: service unavailable and SSH endpoint was not reachable.");
        }

        return SetAction(false, $"Tunnel test failed: {response.Error ?? "unknown error"}");
    }

    public async Task<OperationResult> InstallStartServiceAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _serviceControl.InstallOrStartWindowsServiceAsync(cancellationToken);
        if (!installed)
        {
            return SetAction(false, "Service install/start canceled or failed.");
        }

        await Task.Delay(1200, cancellationToken);
        var config = await ApplyConfigAsync(cancellationToken);
        if (!config.Success)
        {
            return config;
        }

        var whitelist = await UpdateWhitelistAsync(cancellationToken);
        if (!whitelist.Success)
        {
            return whitelist;
        }

        var startResponse = await _gatewayClient.StartProxyAsync(cancellationToken);
        if (startResponse?.Success != true)
        {
            return SetAction(false, $"Proxy start failed: {startResponse?.Error ?? "service unavailable"}");
        }

        return SetAction(true, "Windows service started and proxy start requested.");
    }

    public async Task<OperationResult> StopServiceAsync(CancellationToken cancellationToken = default)
    {
        await _gatewayClient.StopProxyAsync(cancellationToken);
        var stopped = await _serviceControl.StopWindowsServiceAsync(cancellationToken);
        return stopped
            ? SetAction(true, "Service stop requested.")
            : SetAction(false, "Service stop canceled or failed.");
    }

    public async Task<OperationResult> StartProxyAsync(CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.StartProxyAsync(cancellationToken);
        return response?.Success == true
            ? SetAction(true, "Proxy start requested.")
            : SetAction(false, $"Proxy start failed: {response?.Error ?? "service unavailable"}");
    }

    public async Task<OperationResult> StopProxyAsync(CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.StopProxyAsync(cancellationToken);
        return response?.Success == true
            ? SetAction(true, "Proxy stop requested.")
            : SetAction(false, $"Proxy stop failed: {response?.Error ?? "service unavailable"}");
    }

    public IReadOnlyList<string> ValidateWhitelistLines()
    {
        var lines = _state.WhitelistText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var errors = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var trimmed = line;
            var commentMarker = trimmed.IndexOf('#');
            if (commentMarker > 0)
            {
                trimmed = trimmed[..commentMarker].Trim();
            }

            if (!EstherLink.Core.Policy.NetworkRule.TryParse(trimmed, out _, out var error))
            {
                errors.Add($"Line {i + 1}: {error ?? "invalid entry"}");
            }
        }

        return errors;
    }

    private ServiceConfig BuildConfig()
    {
        if (!int.TryParse(_state.VpsPortText.Trim(), out var vpsPort) || vpsPort <= 0)
        {
            throw new InvalidOperationException("VPS port must be a positive integer.");
        }

        if (!int.TryParse(_state.ProxyPortText.Trim(), out var proxyPort) || proxyPort <= 0)
        {
            throw new InvalidOperationException("Proxy listen port must be a positive integer.");
        }

        if (!int.TryParse(_state.TunnelSshPortText.Trim(), out var tunnelSshPort) || tunnelSshPort <= 0)
        {
            throw new InvalidOperationException("Tunnel SSH port must be a positive integer.");
        }

        if (!int.TryParse(_state.TunnelRemotePortText.Trim(), out var tunnelRemotePort) || tunnelRemotePort <= 0)
        {
            throw new InvalidOperationException("Tunnel remote port must be a positive integer.");
        }

        var authMethod = TunnelAuthMethods.Normalize(_state.TunnelAuthMethod);
        if (authMethod == TunnelAuthMethods.Password && string.IsNullOrWhiteSpace(_state.TunnelPassword))
        {
            throw new InvalidOperationException("Tunnel password is required when password authentication is selected.");
        }

        if (authMethod == TunnelAuthMethods.HostKey && string.IsNullOrWhiteSpace(_state.TunnelKeyPath))
        {
            throw new InvalidOperationException("Tunnel host key file path is required when host-key authentication is selected.");
        }

        return new ServiceConfig
        {
            VpsHost = _state.VpsHost.Trim(),
            VpsPort = vpsPort,
            LocalProxyListenPort = proxyPort,
            WhitelistAdapterIfIndex = _state.VpsAdapter?.IfIndex ?? -1,
            DefaultAdapterIfIndex = _state.OutgoingAdapter?.IfIndex ?? -1,
            TunnelHost = _state.TunnelHost.Trim(),
            TunnelSshPort = tunnelSshPort,
            TunnelRemotePort = tunnelRemotePort,
            TunnelUser = _state.TunnelUser.Trim(),
            TunnelAuthMethod = authMethod,
            TunnelPrivateKeyPath = _state.TunnelKeyPath.Trim(),
            TunnelPrivateKeyPassphrase = _state.TunnelKeyPassphrase,
            TunnelPassword = _state.TunnelPassword,
            LicenseKey = _state.LicenseKey.Trim()
        };
    }

    private OperationResult SetAction(bool success, string message)
    {
        _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
        return new OperationResult(success, message);
    }

    private static async Task<bool> TestSshTcpConnectivityAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(config.TunnelHost, config.TunnelSshPort, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
