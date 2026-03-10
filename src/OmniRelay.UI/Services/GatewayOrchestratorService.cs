using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;
using OmniRelay.Core.Status;
using OmniRelay.Ipc;
using OmniRelay.UI.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;

namespace OmniRelay.UI.Services;

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
            var defaultReality = GatewayRealityTargetCatalog.GetRandom();
            _state.GatewaySni = NormalizeOrDefault(persisted.GatewaySni, defaultReality.Sni);
            _state.GatewayTarget = NormalizeOrDefault(persisted.GatewayTarget, defaultReality.Target);
            _state.GatewayDnsMode = NormalizeDnsModeOrDefault(persisted.GatewayDnsMode, "hybrid");
            _state.GatewayDohEndpointsText = NormalizeOrDefault(persisted.GatewayDohEndpointsText, "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query");
            _state.GatewayDnsUdpOnly = persisted.GatewayDnsUdpOnly;
            _state.GatewayPanelUrl = NormalizeOrDefault(persisted.GatewayPanelUrl, string.Empty);
            _state.GatewayPanelUsername = NormalizeOrDefault(persisted.GatewayPanelUsername, string.Empty);
            _state.GatewayInitialPanelPassword = NormalizeOrDefault(persisted.GatewayInitialPanelPassword, string.Empty);
            _state.TunnelUser = NormalizeOrDefault(persisted.TunnelUser, "OmniRelay");
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
            var preflight = await ValidateTunnelReachabilityForApplyAsync(config, cancellationToken);
            var preflightWarning = preflight.Success ? null : preflight.Message;

            var response = await _gatewayClient.SetConfigAsync(config, cancellationToken);
            if (response?.Success == true)
            {
                if (string.IsNullOrWhiteSpace(preflightWarning))
                {
                    return SetAction(true, "Configuration updated.");
                }

                return SetAction(true, $"Configuration updated. Local preflight warning: {preflightWarning}");
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

    private async Task<OperationResult> ValidateTunnelReachabilityForApplyAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.TunnelHost))
        {
            return SetAction(true, "Tunnel host is empty; IC1 reachability preflight skipped.");
        }

        var probe = await TestSshTcpConnectivityAsync(config, cancellationToken);
        if (probe.Success)
        {
            return new OperationResult(true, probe.Message);
        }

        return SetAction(false, $"IC1 preflight failed. {probe.Message}");
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
        status.LicenseReason = payload.Reason;
        status.LicenseTransferRequired = payload.TransferRequired;
        status.LicenseTransferLimitPerRollingYear = payload.TransferLimitPerRollingYear;
        status.LicenseTransfersUsedInWindow = payload.TransfersUsedInWindow;
        status.LicenseTransfersRemainingInWindow = payload.TransfersRemainingInWindow;
        status.LicenseTransferWindowStartAt = payload.TransferWindowStartAt;
        status.LicenseActiveDeviceHint = payload.ActiveDeviceIdHint;

        _state.LicenseActivated = payload.IsValid;
        _state.LicenseActivatedExpiresAtUtc = payload.ExpiresAtUtc;

        if (payload.IsValid)
        {
            return SetAction(true, $"License valid. Expires: {payload.ExpiresAtUtc:O}. Source: {(payload.FromCache ? "cache" : "online")}.");
        }

        if (payload.TransferRequired)
        {
            var hint = string.IsNullOrWhiteSpace(payload.ActiveDeviceIdHint)
                ? "another device"
                : payload.ActiveDeviceIdHint;
            return SetAction(
                false,
                $"License transfer required. Active on {hint}. Remaining transfers: {payload.TransfersRemainingInWindow}/{payload.TransferLimitPerRollingYear}.");
        }

        var error = payload.Error ?? "unknown error";
        if (IsTransientLicenseError(error))
        {
            return SetAction(false, $"License verification failed: {error}");
        }

        return SetAction(false, $"License invalid: {error}");
    }

    public async Task<OperationResult> RequestLicenseTransferAndVerifyAsync(CancellationToken cancellationToken = default)
    {
        var readiness = await CheckLicenseServiceCompatibilityAsync(
            requiredCommands: [IpcCommands.SetLicenseKey, IpcCommands.RequestLicenseTransfer],
            cancellationToken);
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

        var setLicenseKeyResponse = await _gatewayClient.SetLicenseKeyAsync(licenseKey, cancellationToken);
        if (setLicenseKeyResponse?.Success != true)
        {
            return SetAction(false, $"Set license key failed: {setLicenseKeyResponse?.Error ?? "service unavailable"}");
        }

        SetAction(true, "Submitting license transfer request...");
        var transferResponse = await _gatewayClient.RequestLicenseTransferAsync(cancellationToken);
        if (transferResponse?.Success != true)
        {
            return SetAction(false, $"License transfer request failed: {transferResponse?.Error ?? "service unavailable"}");
        }

        return await VerifyLicenseAsync(cancellationToken);
    }

    public async Task<OperationResult> CheckLicenseServiceCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        return await CheckLicenseServiceCompatibilityAsync(
            requiredCommands: [IpcCommands.SetLicenseKey],
            cancellationToken);
    }

    private async Task<OperationResult> CheckLicenseServiceCompatibilityAsync(
        IReadOnlyList<string> requiredCommands,
        CancellationToken cancellationToken = default)
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

        var missing = requiredCommands
            .Where(required => !payload.Capabilities.Any(x => string.Equals(x, required, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count > 0)
        {
            return SetAction(false, $"Relay service v{payload.ServiceVersion} is outdated and missing capability: {string.Join(", ", missing)}.");
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
            if (tcpFallback.Success)
            {
                return SetAction(false, $"SSH endpoint is reachable, but authentication test failed: {authFallback.Message}");
            }

            return SetAction(false, $"Tunnel test failed: service unavailable and SSH endpoint was not reachable. Details: {FirstNonEmpty(authFallback.Message, tcpFallback.Message)}");
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

            if (!OmniRelay.Core.Policy.NetworkRule.TryParse(trimmed, out _, out var error))
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
            GatewaySni = _state.GatewaySni,
            GatewayTarget = _state.GatewayTarget,
            GatewayDnsMode = _state.GatewayDnsMode,
            GatewayDohEndpointsText = _state.GatewayDohEndpointsText,
            GatewayDnsUdpOnly = _state.GatewayDnsUdpOnly,
            GatewayPanelUrl = _state.GatewayPanelUrl,
            GatewayPanelUsername = _state.GatewayPanelUsername,
            GatewayInitialPanelPassword = _state.GatewayInitialPanelPassword,
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

    private static async Task<(bool Success, string Message)> TestSshTcpConnectivityAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        if (!SshCliStartInfoFactory.TryResolveIc1BindIp(config, out var bindIp, out var bindError))
        {
            return (false, bindError ?? "VPS Network (IC1) adapter has no usable IPv4 address.");
        }

        try
        {
            using var tcp = new TcpClient();
            tcp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(bindIp), 0));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(config.TunnelHost, config.TunnelSshPort, cts.Token);
            return (true, $"SSH endpoint reachable via IC1 source IP {bindIp}.");
        }
        catch (Exception ex)
        {
            return (false, $"Cannot open SSH endpoint from IC1 adapter IPv4 {bindIp}: {ex.Message}");
        }
    }

    private static async Task<(bool Success, string Message)> TestSshAuthConnectivityAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        var attemptedRepair = false;
        while (true)
        {
            if (!SshCliStartInfoFactory.TryCreateBoundSshConnectionTestStartInfo(config, out var startInfo, out var bindIp, out var error))
            {
                return (false, error ?? "Cannot prepare SSH auth connectivity test command for IC1.");
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
                // ConnectTimeout is 10s in SSH start info; wait beyond that before
                // treating a still-running ssh process as success.
                var runningDelay = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
                var completed = await Task.WhenAny(waitTask, runningDelay);

                if (completed == waitTask)
                {
                    var stderr = (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim();
                    var stdout = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();

                    if (process.ExitCode == 0)
                    {
                        return (true, $"SSH connection established via IC1 source IP {bindIp}.");
                    }

                    var message = FirstNonEmpty(stderr, stdout) ?? $"SSH exited with code {process.ExitCode}.";
                    if (!attemptedRepair && SshCliStartInfoFactory.LooksLikeHostKeyMismatch(message))
                    {
                        attemptedRepair = true;
                        var repair = await SshCliStartInfoFactory.TryRepairKnownHostEntryAsync(config, cancellationToken);
                        if (repair.Success)
                        {
                            continue;
                        }

                        return (false, $"{message} | {repair.Message}");
                    }

                    return (false, message);
                }

                await StopProcessAsync(process);
                return (true, $"SSH connection established via IC1 source IP {bindIp}.");
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
