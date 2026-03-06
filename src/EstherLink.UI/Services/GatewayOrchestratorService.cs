using EstherLink.Core.Configuration;
using EstherLink.Core.Networking;
using EstherLink.Core.Status;
using EstherLink.Ipc;
using EstherLink.UI.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace EstherLink.UI.Services;

public sealed class GatewayOrchestratorService
{
    private readonly GatewayStateStore _state;
    private readonly IGatewayClientService _gatewayClient;
    private readonly IServiceControlService _serviceControl;
    private readonly IGatewayStatePersistenceService _statePersistence;
    private bool _suppressStatePersistence;

    public GatewayOrchestratorService(
        GatewayStateStore state,
        IGatewayClientService gatewayClient,
        IServiceControlService serviceControl,
        IGatewayStatePersistenceService statePersistence)
    {
        _state = state;
        _gatewayClient = gatewayClient;
        _serviceControl = serviceControl;
        _statePersistence = statePersistence;
        _state.PropertyChanged += OnStatePropertyChanged;
    }

    public GatewayStateStore State => _state;

    public void Initialize()
    {
        _suppressStatePersistence = true;
        try
        {
            var persisted = _statePersistence.Load();

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

            var persistedVps = adapters.FirstOrDefault(x => x.IfIndex == persisted.VpsAdapterIfIndex);
            var persistedOutgoing = adapters.FirstOrDefault(x => x.IfIndex == persisted.OutgoingAdapterIfIndex);

            if (adapters.Count > 0)
            {
                _state.VpsAdapter = persistedVps ?? adapters[0];
                _state.OutgoingAdapter = persistedOutgoing ?? adapters[Math.Min(1, adapters.Count - 1)];
            }
            else
            {
                _state.VpsAdapter = null;
                _state.OutgoingAdapter = null;
            }

            _state.ProxyPortText = NormalizeOrDefault(persisted.ProxyPortText, "19080");
            _state.BootstrapSocksLocalPortText = NormalizeOrDefault(persisted.BootstrapSocksLocalPortText, "19081");
            _state.BootstrapSocksRemotePortText = NormalizeOrDefault(persisted.BootstrapSocksRemotePortText, "16080");
            _state.TunnelHost = NormalizeOrDefault(persisted.TunnelHost, "vps.example.com");
            _state.TunnelSshPortText = NormalizeOrDefault(persisted.TunnelSshPortText, "22");
            _state.TunnelRemotePortText = NormalizeOrDefault(persisted.TunnelRemotePortText, "15000");
            _state.GatewayPublicPortText = NormalizeOrDefault(persisted.GatewayPublicPortText, "443");
            _state.GatewayPanelPortText = NormalizeOrDefault(persisted.GatewayPanelPortText, "2054");
            _state.GatewayBackendPortText = NormalizeOrDefault(persisted.GatewayBackendPortText, _state.TunnelRemotePortText);
            _state.GatewayDnsMode = NormalizeDnsModeOrDefault(persisted.GatewayDnsMode, "hybrid");
            _state.GatewayDohEndpointsText = NormalizeOrDefault(persisted.GatewayDohEndpointsText, "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query");
            _state.GatewayDnsUdpOnly = persisted.GatewayDnsUdpOnly;
            _state.TunnelUser = NormalizeOrDefault(persisted.TunnelUser, "estherlink");
            _state.TunnelAuthMethod = TunnelAuthMethods.Normalize(persisted.TunnelAuthMethod);
            _state.TunnelKeyPath = persisted.TunnelKeyPath ?? string.Empty;
            _state.TunnelKeyPassphrase = GatewayStatePersistenceService.Unprotect(persisted.EncryptedTunnelKeyPassphrase);
            _state.TunnelPassword = GatewayStatePersistenceService.Unprotect(persisted.EncryptedTunnelPassword);
            _state.LicenseKey = GatewayStatePersistenceService.Unprotect(persisted.EncryptedLicenseKey);
            _state.WhitelistText = persisted.WhitelistText ?? string.Empty;
            _state.LicenseActivated = false;
            _state.LicenseActivatedExpiresAtUtc = null;
        }
        finally
        {
            _suppressStatePersistence = false;
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
                if (payload?.Status is not null)
                {
                    _state.Status = payload.Status;
                }
                if (payload?.Status is not null)
                {
                    if (payload.Status.LicenseCheckedAtUtc is not null)
                    {
                        _state.LicenseActivated = payload.Status.LicenseValid;
                    }

                    if (payload.Status.LicenseExpiresAtUtc is not null)
                    {
                        _state.LicenseActivatedExpiresAtUtc = payload.Status.LicenseExpiresAtUtc;
                    }
                }
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
        return await ApplyConfigInternalAsync(requireTunnelAuthSecrets: true, cancellationToken);
    }

    public async Task<OperationResult> ApplyRelayConfigAsync(CancellationToken cancellationToken = default)
    {
        return await ApplyConfigInternalAsync(requireTunnelAuthSecrets: false, cancellationToken);
    }

    public async Task<OperationResult> ApplyGatewayConfigAsync(CancellationToken cancellationToken = default)
    {
        return await ApplyConfigInternalAsync(requireTunnelAuthSecrets: true, cancellationToken);
    }

    private async Task<OperationResult> ApplyConfigInternalAsync(bool requireTunnelAuthSecrets, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = BuildConfig(requireTunnelAuthSecrets);
            var response = await _gatewayClient.SetConfigAsync(config, cancellationToken);
            if (response?.Success == true)
            {
                return SetAction(true, "Configuration updated.");
            }

            var error = response?.Error ?? "service unavailable";
            if (error.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("access to the path is denied", StringComparison.OrdinalIgnoreCase))
            {
                error = $"{error}. Relay service IPC permission denied. Reinstall the Relay service with the latest build.";
            }

            return SetAction(false, $"Config update failed: {error}");
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
        var readiness = await CheckLicenseServiceCompatibilityAsync(cancellationToken);
        if (!readiness.Success)
        {
            _state.LicenseActivated = false;
            return readiness;
        }

        var licenseKey = (_state.LicenseKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return SetAction(false, "License key is required.");
        }

        var serviceState = await _serviceControl.QueryServiceStateAsync(cancellationToken);
        if (!string.Equals(serviceState, "Running", StringComparison.OrdinalIgnoreCase))
        {
            _state.LicenseActivated = false;
            return SetAction(false, $"Relay service is not running (state: {serviceState}). Start service, then verify license.");
        }

        var setLicenseKeyResponse = await _gatewayClient.SetLicenseKeyAsync(licenseKey, cancellationToken);
        if (setLicenseKeyResponse?.Success != true)
        {
            var setKeyError = setLicenseKeyResponse?.Error ?? "service unavailable";
            if (setKeyError.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) &&
                setKeyError.Contains(IpcCommands.SetLicenseKey, StringComparison.OrdinalIgnoreCase))
            {
                return SetAction(false, "Relay service is outdated and does not support set_license_key. Reinstall/update the Relay service, then retry.");
            }

            return SetAction(false, $"Set license key failed: {setKeyError}");
        }

        var verifyResponse = await _gatewayClient.VerifyLicenseAsync(cancellationToken);
        if (verifyResponse?.Success != true)
        {
            return SetAction(false, $"License verification failed: {verifyResponse?.Error ?? "service unavailable"}");
        }

        var payload = IpcJson.Deserialize<VerifyLicenseResponse>(verifyResponse.JsonPayload);
        if (payload is null)
        {
            return SetAction(false, "License verification payload was invalid.");
        }

        var status = EnsureStatusObject();
        status.LicenseCheckedAtUtc = DateTimeOffset.UtcNow;
        status.LicenseValid = payload.IsValid;
        status.LicenseExpiresAtUtc = payload.ExpiresAtUtc;

        _state.LicenseActivated = payload.IsValid;
        _state.LicenseActivatedExpiresAtUtc = payload.ExpiresAtUtc;

        if (payload.IsValid)
        {
            return SetAction(true, $"License valid. Expires: {payload.ExpiresAtUtc:O}. Source: {(payload.FromCache ? "cache" : "online")}.");
        }

        var error = payload.Error ?? "unknown error";
        if (IsTransientLicenseError(error))
        {
            return SetAction(false, $"License verification failed: {error}");
        }

        return SetAction(false, $"License invalid: {error}");
    }

    public async Task<OperationResult> CheckLicenseServiceCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        var serviceState = await _serviceControl.QueryServiceStateAsync(cancellationToken);
        if (!string.Equals(serviceState, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return SetAction(false, $"Relay service is not running (state: {serviceState}).");
        }

        var capsResponse = await _gatewayClient.GetCapabilitiesAsync(cancellationToken);
        if (capsResponse?.Success != true)
        {
            return SetAction(false, $"Relay service capabilities check failed: {capsResponse?.Error ?? "service unavailable"}");
        }

        var payload = IpcJson.Deserialize<CapabilitiesResponse>(capsResponse.JsonPayload);
        if (payload is null)
        {
            return SetAction(false, "Relay service capabilities payload was invalid.");
        }

        var supportsSetLicenseKey = payload.Capabilities.Any(x =>
            string.Equals(x, IpcCommands.SetLicenseKey, StringComparison.OrdinalIgnoreCase));
        if (!supportsSetLicenseKey)
        {
            return SetAction(false, $"Relay service v{payload.ServiceVersion} is outdated and does not support '{IpcCommands.SetLicenseKey}'.");
        }

        return SetAction(true, $"Relay service compatible (v{payload.ServiceVersion}).");
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
            var authFallback = await TestSshAuthConnectivityAsync(config, cancellationToken);
            if (authFallback.Success)
            {
                return SetAction(true, "Tunnel connection test succeeded (UI fallback while service IPC was unavailable).");
            }

            var tcpFallback = await TestSshTcpConnectivityAsync(config, cancellationToken);
            if (tcpFallback)
            {
                return SetAction(false, $"SSH endpoint is reachable, but authentication test failed: {authFallback.Message}");
            }

            return SetAction(false, $"Tunnel test failed: service unavailable and SSH endpoint was not reachable. Details: {authFallback.Message}");
        }

        return SetAction(false, $"Tunnel test failed: {response.Error ?? "unknown error"}");
    }

    public async Task<OperationResult> InstallStartServiceAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _serviceControl.InstallOrStartWindowsServiceAsync(cancellationToken);
        if (!installed)
        {
            var state = await _serviceControl.QueryServiceStateAsync(cancellationToken);
            return SetAction(false, $"Service install/start canceled or failed. Service state: {state}.");
        }

        var running = await WaitForServiceRunningAsync(cancellationToken);
        if (!running.Success)
        {
            return running;
        }

        var config = await RetryForServiceReadinessAsync(
            ct => ApplyConfigAsync(ct),
            stepName: "Configuration update",
            maxAttempts: 20,
            delayBetweenAttempts: TimeSpan.FromSeconds(1),
            cancellationToken: cancellationToken);
        if (!config.Success)
        {
            return config;
        }

        var whitelist = await RetryForServiceReadinessAsync(
            ct => UpdateWhitelistAsync(ct),
            stepName: "Whitelist update",
            maxAttempts: 10,
            delayBetweenAttempts: TimeSpan.FromSeconds(1),
            cancellationToken: cancellationToken);
        if (!whitelist.Success)
        {
            return whitelist;
        }

        var startProxy = await RetryForServiceReadinessAsync(
            ct => StartProxyAsync(ct),
            stepName: "Proxy start",
            maxAttempts: 10,
            delayBetweenAttempts: TimeSpan.FromSeconds(1),
            cancellationToken: cancellationToken);
        if (!startProxy.Success)
        {
            return startProxy;
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

    private ServiceConfig BuildConfig(bool requireTunnelAuthSecrets = true)
    {
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
        if (requireTunnelAuthSecrets &&
            authMethod == TunnelAuthMethods.Password &&
            string.IsNullOrWhiteSpace(_state.TunnelPassword))
        {
            throw new InvalidOperationException("Tunnel password is required when password authentication is selected.");
        }

        if (requireTunnelAuthSecrets &&
            authMethod == TunnelAuthMethods.HostKey &&
            string.IsNullOrWhiteSpace(_state.TunnelKeyPath))
        {
            throw new InvalidOperationException("Tunnel host key file path is required when host-key authentication is selected.");
        }

        return new ServiceConfig
        {
            LocalProxyListenPort = proxyPort,
            BootstrapSocksLocalPort = ParsePositivePort(_state.BootstrapSocksLocalPortText, "Bootstrap SOCKS local port"),
            BootstrapSocksRemotePort = ParsePositivePort(_state.BootstrapSocksRemotePortText, "Bootstrap SOCKS remote port"),
            GatewayOnlineInstallEnabled = true,
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

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressStatePersistence)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(e.PropertyName))
        {
            PersistUiState();
            return;
        }

        if (e.PropertyName is nameof(GatewayStateStore.ServiceState) or
            nameof(GatewayStateStore.Status) or
            nameof(GatewayStateStore.LastAction))
        {
            return;
        }

        PersistUiState();
    }

    private void PersistUiState()
    {
        var snapshot = new GatewayUiStateModel
        {
            VpsAdapterIfIndex = _state.VpsAdapter?.IfIndex,
            OutgoingAdapterIfIndex = _state.OutgoingAdapter?.IfIndex,
            ProxyPortText = _state.ProxyPortText,
            BootstrapSocksLocalPortText = _state.BootstrapSocksLocalPortText,
            BootstrapSocksRemotePortText = _state.BootstrapSocksRemotePortText,
            TunnelHost = _state.TunnelHost,
            TunnelSshPortText = _state.TunnelSshPortText,
            TunnelRemotePortText = _state.TunnelRemotePortText,
            GatewayPublicPortText = _state.GatewayPublicPortText,
            GatewayPanelPortText = _state.GatewayPanelPortText,
            GatewayBackendPortText = _state.GatewayBackendPortText,
            GatewayDnsMode = _state.GatewayDnsMode,
            GatewayDohEndpointsText = _state.GatewayDohEndpointsText,
            GatewayDnsUdpOnly = _state.GatewayDnsUdpOnly,
            TunnelUser = _state.TunnelUser,
            TunnelAuthMethod = _state.TunnelAuthMethod,
            TunnelKeyPath = _state.TunnelKeyPath,
            EncryptedTunnelKeyPassphrase = GatewayStatePersistenceService.Protect(_state.TunnelKeyPassphrase),
            EncryptedTunnelPassword = GatewayStatePersistenceService.Protect(_state.TunnelPassword),
            EncryptedLicenseKey = GatewayStatePersistenceService.Protect(_state.LicenseKey),
            WhitelistText = _state.WhitelistText
        };

        _statePersistence.Save(snapshot);
    }

    private static string NormalizeDnsModeOrDefault(string? value, string fallback)
    {
        var normalized = NormalizeOrDefault(value, fallback).Trim().ToLowerInvariant();
        return normalized is "hybrid" or "doh" or "udp" ? normalized : fallback;
    }

    private GatewayStatus EnsureStatusObject()
    {
        if (_state.Status is null)
        {
            _state.Status = new GatewayStatus();
        }

        return _state.Status;
    }

    private static bool IsServiceUnavailable(string? error)
    {
        return string.IsNullOrWhiteSpace(error) ||
               error.Contains("service unavailable", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("pipe", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("access to the path is denied", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<OperationResult> WaitForServiceRunningAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 20;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var state = await _serviceControl.QueryServiceStateAsync(cancellationToken);
            if (string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return SetAction(true, "Windows service is running.");
            }

            if (string.Equals(state, "Not Installed", StringComparison.OrdinalIgnoreCase))
            {
                return SetAction(false, "Windows service was not found after install/start.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var finalState = await _serviceControl.QueryServiceStateAsync(cancellationToken);
        return SetAction(false, $"Windows service did not reach running state in time (state: {finalState}).");
    }

    private async Task<OperationResult> RetryForServiceReadinessAsync(
        Func<CancellationToken, Task<OperationResult>> action,
        string stepName,
        int maxAttempts,
        TimeSpan delayBetweenAttempts,
        CancellationToken cancellationToken)
    {
        OperationResult? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            last = await action(cancellationToken);
            if (last.Success)
            {
                return last;
            }

            if (!IsServiceUnavailable(last.Message))
            {
                return last;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(delayBetweenAttempts, cancellationToken);
            }
        }

        return SetAction(false, $"{stepName} failed after {maxAttempts} attempts: {last?.Message ?? "unknown error"}");
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

    private static async Task<(bool Success, string Message)> TestSshAuthConnectivityAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        if (!TryCreateUiSshTestStartInfo(config, out var startInfo, out var error))
        {
            return (false, error ?? "Tunnel configuration is invalid.");
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo!);
            if (process is null)
            {
                return (false, "Unable to start ssh process.");
            }

            process.StandardInput.Close();

            var waitTask = process.WaitForExitAsync(cancellationToken);
            var runningDelay = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var completed = await Task.WhenAny(waitTask, runningDelay);

            if (completed == waitTask)
            {
                var stderr = (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim();
                var stdout = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();

                if (process.ExitCode == 0)
                {
                    return (true, "SSH connection established.");
                }

                return (false, FirstNonEmpty(stderr, stdout) ?? $"SSH exited with code {process.ExitCode}.");
            }

            await StopProcessAsync(process);
            return (true, "SSH connection established.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (process is not null)
            {
                await StopProcessAsync(process);
                process.Dispose();
            }
        }
    }

    private static bool TryCreateUiSshTestStartInfo(
        ServiceConfig config,
        out ProcessStartInfo? startInfo,
        out string? error)
    {
        startInfo = null;
        error = ValidateTunnelConfigForTest(config);
        if (error is not null)
        {
            return false;
        }

        var args = new List<string>
        {
            "-N",
            "-o", "ServerAliveInterval=10",
            "-o", "ServerAliveCountMax=1",
            "-o", "TCPKeepAlive=yes",
            "-o", "ConnectTimeout=10",
            "-o", "StrictHostKeyChecking=accept-new"
        };

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EstherLink");
        Directory.CreateDirectory(appDataDir);
        args.Add("-o");
        args.Add($"UserKnownHostsFile={Path.Combine(appDataDir, "known_hosts_ui")}");

        var method = TunnelAuthMethods.Normalize(config.TunnelAuthMethod);
        string? secret = null;
        if (string.Equals(method, TunnelAuthMethods.Password, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(config.TunnelPassword))
            {
                error = "Tunnel password is required for password authentication.";
                return false;
            }

            args.Add("-o");
            args.Add("PreferredAuthentications=password,keyboard-interactive");
            args.Add("-o");
            args.Add("PubkeyAuthentication=no");
            args.Add("-o");
            args.Add("NumberOfPasswordPrompts=1");
            secret = config.TunnelPassword;
        }
        else
        {
            var keyPath = (config.TunnelPrivateKeyPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(keyPath))
            {
                error = "Tunnel host key file path is required for host-key authentication.";
                return false;
            }

            if (!File.Exists(keyPath))
            {
                error = $"Tunnel host key file not found: {keyPath}";
                return false;
            }

            args.Add("-i");
            args.Add(keyPath);
            args.Add("-o");
            args.Add("PreferredAuthentications=publickey");

            if (string.IsNullOrWhiteSpace(config.TunnelPrivateKeyPassphrase))
            {
                args.Add("-o");
                args.Add("BatchMode=yes");
            }
            else
            {
                secret = config.TunnelPrivateKeyPassphrase;
            }
        }

        args.Add($"{config.TunnelUser.Trim()}@{config.TunnelHost.Trim()}");
        args.Add("-p");
        args.Add(config.TunnelSshPort.ToString());

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(secret))
        {
            ConfigureAskPass(psi, secret);
        }

        startInfo = psi;
        return true;
    }

    private static string? ValidateTunnelConfigForTest(ServiceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.TunnelHost))
        {
            return "Tunnel host is required.";
        }

        if (string.IsNullOrWhiteSpace(config.TunnelUser))
        {
            return "Tunnel user is required.";
        }

        if (config.TunnelSshPort <= 0 || config.TunnelSshPort > 65535)
        {
            return "Tunnel SSH port must be between 1 and 65535.";
        }

        return null;
    }

    private static void ConfigureAskPass(ProcessStartInfo startInfo, string secret)
    {
        var launcherPath = EnsureAskPassLauncher();
        startInfo.Environment["SSH_ASKPASS"] = launcherPath;
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        startInfo.Environment["DISPLAY"] = "estherlink-ui:0";
        startInfo.Environment["ESTHERLINK_UI_SSH_SECRET"] = secret;
    }

    private static string EnsureAskPassLauncher()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EstherLink",
            "ssh-ui");
        Directory.CreateDirectory(root);

        var scriptPath = Path.Combine(root, "ssh-askpass.ps1");
        var launcherPath = Path.Combine(root, "ssh-askpass.cmd");

        if (!File.Exists(scriptPath))
        {
            var scriptContent = "$secret = $env:ESTHERLINK_UI_SSH_SECRET; if ($null -ne $secret) { [Console]::Out.Write($secret) }";
            File.WriteAllText(scriptPath, scriptContent);
        }

        if (!File.Exists(launcherPath))
        {
            var launcherContent = "@echo off\r\npowershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"%~dp0ssh-askpass.ps1\"";
            File.WriteAllText(launcherPath, launcherContent);
        }

        return launcherPath;
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string NormalizeOrDefault(string? value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static int ParsePositivePort(string text, string fieldName)
    {
        if (!int.TryParse((text ?? string.Empty).Trim(), out var port) || port <= 0 || port > 65535)
        {
            throw new InvalidOperationException($"{fieldName} must be a positive integer between 1 and 65535.");
        }

        return port;
    }

    private static bool IsTransientLicenseError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("request was canceled", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("temporarily", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("http", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("network", StringComparison.OrdinalIgnoreCase);
    }

}
