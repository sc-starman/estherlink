using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;
using OmniRelay.Core.Policy;
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
            var remoteProfile = ResolveRemoteProfile(persisted);
            var localProfile = ResolveLocalProfile(persisted);

            _state.Adapters.Clear();
            var adapters = NetworkAdapterCatalog.ListIpv4Adapters()
                .Select(x => new AdapterChoiceModel
                {
                    AdapterId = x.AdapterId,
                    IfIndex = x.IfIndex,
                    MacAddress = x.MacAddress,
                    Display = $"{x.Name} (IfIndex={x.IfIndex}) | MAC={x.MacAddress} | IPv4={string.Join(",", x.IPv4Addresses)} | GW={(x.HasDefaultGateway ? "Yes" : "No")}"
                })
                .ToList();

            foreach (var adapter in adapters)
            {
                _state.Adapters.Add(adapter);
            }

            _state.GatewayType = GatewayTypes.Normalize(persisted.GatewayType);
            var preferredOutgoingIfIndex = string.Equals(_state.GatewayType, GatewayTypes.Local, StringComparison.OrdinalIgnoreCase)
                ? localProfile.OutgoingAdapterIfIndex
                : remoteProfile.OutgoingAdapterIfIndex;

            var persistedVps = adapters.FirstOrDefault(x => x.IfIndex == remoteProfile.VpsAdapterIfIndex);
            var persistedOutgoing = adapters.FirstOrDefault(x => x.IfIndex == preferredOutgoingIfIndex);

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

            _state.ProxyPortText = NormalizeOrDefault(remoteProfile.ProxyPortText, "24080");
            _state.BootstrapSocksLocalPortText = NormalizeOrDefault(remoteProfile.BootstrapSocksLocalPortText, "24081");
            _state.BootstrapSocksRemotePortText = NormalizeOrDefault(remoteProfile.BootstrapSocksRemotePortText, "16080");
            _state.BootstrapMode = GatewayBootstrapModes.Normalize(remoteProfile.BootstrapMode);
            _state.TunnelHost = NormalizeOrDefault(remoteProfile.TunnelHost, "vps.example.com");
            _state.TunnelSshPortText = NormalizeOrDefault(remoteProfile.TunnelSshPortText, "22");
            _state.TunnelRemotePortText = NormalizeOrDefault(remoteProfile.TunnelRemotePortText, "15000");
            _state.SelectedGatewayProtocol = GatewayProtocols.Normalize(remoteProfile.SelectedGatewayProtocol);
            _state.GatewayPublicPortText = NormalizeOrDefault(remoteProfile.GatewayPublicPortText, "443");
            _state.GatewayPanelPortText = NormalizeOrDefault(remoteProfile.GatewayPanelPortText, "2054");
            _state.GatewayPanelConfiguredUser = NormalizeOrDefault(remoteProfile.GatewayPanelConfiguredUser, string.Empty);
            _state.GatewayPanelConfiguredPassword = GatewayStatePersistenceService.Unprotect(remoteProfile.EncryptedGatewayPanelConfiguredPassword);
            _state.GatewayPanelDomain = NormalizeOrDefault(remoteProfile.GatewayPanelDomain, string.Empty);
            _state.GatewayPanelDomainOnly = remoteProfile.GatewayPanelDomainOnly;
            _state.GatewayPanelUseSsl = remoteProfile.GatewayPanelUseSsl;
            _state.GatewayPanelSslMode = NormalizePanelSslModeOrDefault(remoteProfile.GatewayPanelSslMode, "letsencrypt");
            // Keep uploaded certificate/key local file picks session-only.
            _state.GatewayPanelUploadedCertPath = string.Empty;
            _state.GatewayPanelUploadedKeyPath = string.Empty;
            _state.GatewayBackendPortText = NormalizeOrDefault(remoteProfile.GatewayBackendPortText, _state.TunnelRemotePortText);
            _state.GatewaySni = NormalizeOrDefault(remoteProfile.GatewaySni, string.Empty);
            _state.GatewayTarget = NormalizeOrDefault(remoteProfile.GatewayTarget, string.Empty);
            _state.GatewayProtocolTlsEnabled = remoteProfile.GatewayProtocolTlsEnabled;
            _state.GatewayProtocolTlsServerName = NormalizeOrDefault(remoteProfile.GatewayProtocolTlsServerName, string.Empty);
            _state.GatewayProtocolCertPath = NormalizeOrDefault(remoteProfile.GatewayProtocolCertPath, string.Empty);
            _state.GatewayProtocolKeyPath = NormalizeOrDefault(remoteProfile.GatewayProtocolKeyPath, string.Empty);
            _state.GatewayProtocolTlsMode = NormalizeOrDefault(remoteProfile.GatewayProtocolTlsMode, "uploaded");
            _state.GatewayProtocolAlpnCsv = NormalizeOrDefault(remoteProfile.GatewayProtocolAlpnCsv, string.Empty);
            _state.GatewayProxyUsername = NormalizeOrDefault(remoteProfile.GatewayProxyUsername, "omni");
            _state.GatewayProxyPassword = NormalizeOrDefault(remoteProfile.GatewayProxyPassword, string.Empty);
            _state.VlessTlsFlow = NormalizeOrDefault(remoteProfile.VlessTlsFlow, string.Empty);
            _state.Hysteria2UpMbpsText = NormalizeOrDefault(remoteProfile.Hysteria2UpMbpsText, "100");
            _state.Hysteria2DownMbpsText = NormalizeOrDefault(remoteProfile.Hysteria2DownMbpsText, "100");
            _state.Hysteria2ObfsPassword = NormalizeOrDefault(remoteProfile.Hysteria2ObfsPassword, string.Empty);
            _state.Hysteria2IgnoreClientBandwidth = remoteProfile.Hysteria2IgnoreClientBandwidth;
            _state.Hysteria2MasqueradeUrl = NormalizeOrDefault(remoteProfile.Hysteria2MasqueradeUrl, string.Empty);
            _state.NaiveNetwork = NormalizeOrDefault(remoteProfile.NaiveNetwork, string.Empty);
            _state.NaiveQuicCongestionControl = NormalizeOrDefault(remoteProfile.NaiveQuicCongestionControl, string.Empty);
            _state.ShadowTlsCamouflageServer = NormalizeOrDefault(remoteProfile.ShadowTlsCamouflageServer, GatewayCamouflageCatalog.GetRandom());
            _state.ShadowTlsStrictMode = remoteProfile.ShadowTlsStrictMode;
            _state.ShadowTlsWildcardSni = NormalizeOrDefault(remoteProfile.ShadowTlsWildcardSni, string.Empty);
            _state.OpenVpnNetwork = NormalizeOrDefault(remoteProfile.OpenVpnNetwork, "10.29.0.0/24");
            _state.IpsecL2tpNetwork = NormalizeOrDefault(remoteProfile.IpsecL2tpNetwork, "10.39.0.0/24");
            _state.GatewayDohEndpointsText = NormalizeOrDefault(remoteProfile.GatewayDohEndpointsText, "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query");
            // Install result credentials are one-time only and never reloaded from persisted UI state.
            _state.GatewayPanelUrl = string.Empty;
            _state.GatewayPanelUsername = string.Empty;
            _state.GatewayInitialPanelPassword = string.Empty;
            _state.TunnelUser = NormalizeOrDefault(remoteProfile.TunnelUser, "OmniRelay");
            _state.TunnelAuthMethod = TunnelAuthMethods.Normalize(remoteProfile.TunnelAuthMethod);
            _state.TunnelKeyPath = remoteProfile.TunnelKeyPath ?? string.Empty;
            _state.TunnelKeyPassphrase = GatewayStatePersistenceService.Unprotect(remoteProfile.EncryptedTunnelKeyPassphrase);
            _state.TunnelPassword = GatewayStatePersistenceService.Unprotect(remoteProfile.EncryptedTunnelPassword);

            _state.LocalGatewayProtocol = LocalGatewayProtocols.Normalize(localProfile.LocalGatewayProtocol);
            _state.LocalGatewayPortText = NormalizeOrDefault(localProfile.LocalGatewayPortText, "443");
            _state.LocalGatewayBindAddress = NormalizeOrDefault(localProfile.LocalGatewayBindAddress, "0.0.0.0");
            _state.LocalGatewayRemoteAddress = NormalizeOrDefault(localProfile.LocalGatewayRemoteAddress, string.Empty);
            _state.LocalGatewayRemark = NormalizeOrDefault(localProfile.LocalGatewayRemark, "OmniRelay Local Gateway");
            _state.LocalGatewayRuntimeEnabled = localProfile.LocalGatewayRuntimeEnabled;

            var preferredEncryptedLicense = string.Equals(_state.GatewayType, GatewayTypes.Local, StringComparison.OrdinalIgnoreCase)
                ? localProfile.EncryptedLicenseKey
                : remoteProfile.EncryptedLicenseKey;
            if (string.IsNullOrWhiteSpace(preferredEncryptedLicense))
            {
                preferredEncryptedLicense = remoteProfile.EncryptedLicenseKey;
            }

            var license = GatewayStatePersistenceService.Unprotect(preferredEncryptedLicense);
            _state.LicenseKey = license;
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
                    _state.GatewayType = GatewayTypes.Normalize(payload.Status.GatewayType);
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

            var appResponse = await _gatewayClient.GetAppStatusAsync(cancellationToken);
            if (appResponse?.Success == true)
            {
                var appPayload = IpcJson.Deserialize<AppStatusResponse>(appResponse.JsonPayload);
                if (appPayload?.Status is not null)
                {
                    _state.AppStatus = appPayload.Status;
                    SyncRelayList(appPayload.Status.Relays);
                    if (appPayload.Status.LicenseCheckedAtUtc is not null)
                    {
                        _state.LicenseActivated = appPayload.Status.LicenseValid;
                    }

                    if (appPayload.Status.LicenseExpiresAtUtc is not null)
                    {
                        _state.LicenseActivatedExpiresAtUtc = appPayload.Status.LicenseExpiresAtUtc;
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

    public async Task<OperationResult> ApplyLocalGatewayConfigAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = BuildConfig(requireTunnelAuthSecrets: false);
            config.GatewayType = GatewayTypes.Local;
            var response = await _gatewayClient.SetConfigAsync(config, cancellationToken);
            if (response?.Success != true)
            {
                return SetAction(false, $"Configuration update failed: {response?.Error ?? "service unavailable"}");
            }

            await RefreshStatusAsync(cancellationToken);
            return SetAction(true, "Local gateway configuration updated.");
        }
        catch (Exception ex)
        {
            return SetAction(false, ex.Message);
        }
    }

    public async Task<OperationResult> StartLocalGatewayAsync(Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.StartLocalGatewayAsync(cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Local gateway start failed: {response?.Error ?? "service unavailable"}");
        }

        if (!IsOpenVpnLocalProtocolSelected())
        {
            await RefreshStatusAsync(cancellationToken);
            return SetAction(true, "Local gateway start requested.");
        }

        return await WaitForLocalGatewayActivationAsync("start", progress, cancellationToken);
    }

    public async Task<OperationResult> LoadRelaysAsync(CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.ListRelaysAsync(cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Relay list fetch failed: {response?.Error ?? "service unavailable"}");
        }

        var payload = IpcJson.Deserialize<RelaysResponse>(response.JsonPayload);
        if (payload is null)
        {
            return SetAction(false, "Relay list fetch failed: invalid response payload.");
        }

        _state.Relays.Clear();
        foreach (var relay in payload.Relays)
        {
            if (relay is not null)
            {
                _state.Relays.Add(NormalizeRelayForUi(relay));
            }
        }

        return SetAction(true, $"Loaded {payload.Relays.Count} relays.");
    }

    public async Task<(bool Success, string Message, RelayConfig? Relay)> GetRelayAsync(string relayId, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.GetRelayAsync(relayId, cancellationToken);
        if (response?.Success != true)
        {
            return (false, $"Relay fetch failed: {response?.Error ?? "service unavailable"}", null);
        }

        var payload = IpcJson.Deserialize<RelayResponse>(response.JsonPayload);
        if (payload?.Relay is null)
        {
            return (false, "Relay fetch failed: invalid response payload.", null);
        }

        return (true, "Relay loaded.", NormalizeRelayForUi(payload.Relay));
    }

    public async Task<OperationResult> UpsertRelayAsync(RelayConfig relay, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.UpsertRelayAsync(relay, cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Relay save failed: {response?.Error ?? "service unavailable"}");
        }

        await LoadRelaysAsync(cancellationToken);
        await RefreshStatusAsync(cancellationToken);
        return SetAction(true, "Relay saved.");
    }

    public async Task<OperationResult> DeleteRelayAsync(string relayId, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.DeleteRelayAsync(relayId, cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Relay delete failed: {response?.Error ?? "service unavailable"}");
        }

        await LoadRelaysAsync(cancellationToken);
        await RefreshStatusAsync(cancellationToken);
        return SetAction(true, "Relay deleted.");
    }

    public async Task<OperationResult> SetRelayEnabledAsync(string relayId, bool enabled, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.SetRelayEnabledAsync(relayId, enabled, cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Relay update failed: {response?.Error ?? "service unavailable"}");
        }

        await LoadRelaysAsync(cancellationToken);
        await RefreshStatusAsync(cancellationToken);
        return SetAction(true, enabled ? "Relay enabled." : "Relay disabled.");
    }

    public async Task<OperationResult> StopLocalGatewayAsync(CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.StopLocalGatewayAsync(cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Local gateway stop failed: {response?.Error ?? "service unavailable"}");
        }

        await RefreshStatusAsync(cancellationToken);
        return SetAction(true, "Local gateway stop requested.");
    }

    public async Task<OperationResult> RestartLocalGatewayAsync(Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.RestartLocalGatewayAsync(cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Local gateway restart failed: {response?.Error ?? "service unavailable"}");
        }

        if (!IsOpenVpnLocalProtocolSelected())
        {
            await RefreshStatusAsync(cancellationToken);
            return SetAction(true, "Local gateway restart requested.");
        }

        return await WaitForLocalGatewayActivationAsync("restart", progress, cancellationToken);
    }

    public async Task<LocalGatewayClientsResult> GetLocalGatewayClientsAsync(string relayId, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.GetLocalGatewayClientsAsync(relayId, cancellationToken);
        if (response?.Success != true)
        {
            var errorMessage = $"Local clients fetch failed: {response?.Error ?? "service unavailable"}";
            SetAction(false, errorMessage);
            return new LocalGatewayClientsResult(false, errorMessage, []);
        }

        var payload = IpcJson.Deserialize<LocalGatewayClientsResponse>(response.JsonPayload);
        if (payload is null)
        {
            const string invalidPayload = "Local clients fetch failed: invalid response payload.";
            SetAction(false, invalidPayload);
            return new LocalGatewayClientsResult(false, invalidPayload, []);
        }

        var message = $"Loaded {payload.Clients.Count} local gateway clients.";
        SetAction(true, message);
        return new LocalGatewayClientsResult(true, message, payload.Clients);
    }

    public async Task<OperationResult> AddLocalGatewayClientAsync(string email, string relayId, string? remark, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.AddLocalGatewayClientAsync(email, relayId, remark, cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Add local client failed: {response?.Error ?? "service unavailable"}");
        }

        return SetAction(true, "Local client added.");
    }

    public async Task<OperationResult> UpdateLocalGatewayClientAsync(LocalGatewayClientRecord client, string relayId, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.UpdateLocalGatewayClientAsync(client, relayId, cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Update local client failed: {response?.Error ?? "service unavailable"}");
        }

        return SetAction(true, "Local client updated.");
    }

    public async Task<OperationResult> DeleteLocalGatewayClientAsync(string clientId, string relayId, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.DeleteLocalGatewayClientAsync(clientId, relayId, cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Delete local client failed: {response?.Error ?? "service unavailable"}");
        }

        return SetAction(true, "Local client deleted.");
    }

    public async Task<LocalGatewayClientConfigBuildResult> BuildLocalGatewayClientConfigAsync(string clientId, string relayId, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.BuildLocalGatewayClientConfigAsync(clientId, relayId, cancellationToken);
        if (response?.Success != true)
        {
            var message = $"Build config failed: {response?.Error ?? "service unavailable"}";
            SetAction(false, message);
            return new LocalGatewayClientConfigBuildResult(false, message, "uri", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var payload = IpcJson.Deserialize<LocalGatewayClientConfigResponse>(response.JsonPayload);
        if (payload is null)
        {
            const string invalid = "Build config failed: invalid response payload.";
            SetAction(false, invalid);
            return new LocalGatewayClientConfigBuildResult(false, invalid, "uri", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        SetAction(true, "Client config generated.");
        return new LocalGatewayClientConfigBuildResult(
            true,
            "Client config generated.",
            string.IsNullOrWhiteSpace(payload.Mode) ? "uri" : payload.Mode,
            payload.Uri ?? string.Empty,
            payload.Title ?? string.Empty,
            payload.Username ?? string.Empty,
            payload.Password ?? string.Empty,
            payload.OvpnFileName ?? string.Empty,
            payload.OvpnContent ?? string.Empty);
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
        if (string.Equals(GatewayTypes.Normalize(config.GatewayType), GatewayTypes.Local, StringComparison.OrdinalIgnoreCase))
        {
            return SetAction(true, "Local gateway mode selected; SSH tunnel preflight skipped.");
        }

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

    public async Task<PolicyListResult> GetPolicyListAsync(string listType, string? relayId = null, CancellationToken cancellationToken = default)
    {
        var normalizedListType = PolicyListTypes.Normalize(listType);
        var response = await _gatewayClient.GetPolicyListAsync(normalizedListType, relayId, cancellationToken);
        if (response?.Success != true)
        {
            if (IsMissingPolicyCapability(response?.Error, IpcCommands.GetPolicyList))
            {
                const string upgradeMessage = "Relay service is outdated and does not support policy list APIs. Please update/reinstall the Relay service.";
                SetAction(false, upgradeMessage);
                return new PolicyListResult(false, upgradeMessage, normalizedListType, [], 0, 0, DateTimeOffset.MinValue);
            }

            var message = $"Policy fetch failed: {response?.Error ?? "service unavailable"}";
            SetAction(false, message);
            return new PolicyListResult(false, message, normalizedListType, [], 0, 0, DateTimeOffset.MinValue);
        }

        var payload = IpcJson.Deserialize<GetPolicyListResponse>(response.JsonPayload);
        if (payload is null)
        {
            const string invalidPayload = "Policy fetch failed: invalid response payload.";
            SetAction(false, invalidPayload);
            return new PolicyListResult(false, invalidPayload, normalizedListType, [], 0, 0, DateTimeOffset.MinValue);
        }

        var messageText = $"{payload.ListType} loaded ({payload.Count} entries).";
        SetAction(true, messageText);
        return new PolicyListResult(
            true,
            messageText,
            payload.ListType,
            payload.Entries,
            payload.Count,
            payload.Revision,
            payload.UpdatedAtUtc);
    }

    public async Task<PolicyCommitSummary> UpdatePolicyListAsync(
        string listType,
        string mode,
        IReadOnlyList<string> entries,
        string? relayId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedListType = PolicyListTypes.Normalize(listType);
        var normalizedMode = PolicyUpdateModes.Normalize(mode);

        var begin = await _gatewayClient.BeginPolicyUpdateAsync(normalizedListType, normalizedMode, relayId, cancellationToken);
        if (begin?.Success != true)
        {
            if (IsMissingPolicyCapability(begin?.Error, IpcCommands.BeginPolicyUpdate))
            {
                const string upgradeMessage = "Relay service is outdated and does not support large policy updates. Please update/reinstall the Relay service.";
                SetAction(false, upgradeMessage);
                return new PolicyCommitSummary(false, upgradeMessage, normalizedListType, normalizedMode, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
            }

            var error = begin?.Error ?? "service unavailable";
            var message = $"Policy update failed: {error}";
            SetAction(false, message);
            return new PolicyCommitSummary(false, message, normalizedListType, normalizedMode, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
        }

        var beginPayload = IpcJson.Deserialize<BeginPolicyUpdateResponse>(begin.JsonPayload);
        if (beginPayload is null || string.IsNullOrWhiteSpace(beginPayload.SessionId))
        {
            const string invalidBegin = "Policy update failed: invalid begin session payload.";
            SetAction(false, invalidBegin);
            return new PolicyCommitSummary(false, invalidBegin, normalizedListType, normalizedMode, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
        }

        var sessionId = beginPayload.SessionId;
        try
        {
            const int chunkSize = 10_000;
            for (var i = 0; i < entries.Count; i += chunkSize)
            {
                var chunk = entries.Skip(i).Take(chunkSize).ToArray();
                var append = await _gatewayClient.AppendPolicyEntriesAsync(sessionId, chunk, cancellationToken);
                if (append?.Success != true)
                {
                    if (IsMissingPolicyCapability(append?.Error, IpcCommands.AppendPolicyEntries))
                    {
                        const string upgradeMessage = "Relay service is outdated and does not support chunked policy appends. Please update/reinstall the Relay service.";
                        SetAction(false, upgradeMessage);
                        return new PolicyCommitSummary(false, upgradeMessage, normalizedListType, normalizedMode, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
                    }

                    var appendError = append?.Error ?? "service unavailable";
                    var appendMessage = $"Policy update append failed: {appendError}";
                    SetAction(false, appendMessage);
                    return new PolicyCommitSummary(false, appendMessage, normalizedListType, normalizedMode, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
                }
            }

            var commit = await _gatewayClient.CommitPolicyUpdateAsync(sessionId, cancellationToken);
            if (commit?.Success != true)
            {
                if (IsMissingPolicyCapability(commit?.Error, IpcCommands.CommitPolicyUpdate))
                {
                    const string upgradeMessage = "Relay service is outdated and does not support policy commit APIs. Please update/reinstall the Relay service.";
                    SetAction(false, upgradeMessage);
                    return new PolicyCommitSummary(false, upgradeMessage, normalizedListType, normalizedMode, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
                }

                var commitError = commit?.Error ?? "service unavailable";
                var commitMessage = $"Policy update commit failed: {commitError}";
                SetAction(false, commitMessage);
                return new PolicyCommitSummary(false, commitMessage, normalizedListType, normalizedMode, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
            }

            var commitPayload = IpcJson.Deserialize<CommitPolicyUpdateResponse>(commit.JsonPayload);
            if (commitPayload is null)
            {
                const string invalidCommit = "Policy update failed: invalid commit payload.";
                SetAction(false, invalidCommit);
                return new PolicyCommitSummary(false, invalidCommit, normalizedListType, normalizedMode, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
            }

            var successMessage =
                $"{commitPayload.ListType} updated. applied={commitPayload.AppliedCount}, duplicates={commitPayload.DuplicateDroppedCount}, invalid={commitPayload.InvalidCount}, total={commitPayload.Count}.";
            SetAction(true, successMessage);
            return new PolicyCommitSummary(
                true,
                successMessage,
                commitPayload.ListType,
                commitPayload.Mode,
                commitPayload.AppliedCount,
                commitPayload.DuplicateDroppedCount,
                commitPayload.InvalidCount,
                commitPayload.Count,
                commitPayload.Revision,
                commitPayload.UpdatedAtUtc);
        }
        catch
        {
            await _gatewayClient.CancelPolicyUpdateAsync(sessionId, CancellationToken.None);
            throw;
        }
    }

    public async Task<RelayPolicyListsResult> GetRelayPolicyListsAsync(string relayId, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.ListRelayPolicyListsAsync(relayId, cancellationToken);
        if (response?.Success != true)
        {
            var message = $"Policy list fetch failed: {response?.Error ?? "service unavailable"}";
            SetAction(false, message);
            return new RelayPolicyListsResult(false, message, relayId, [], 0, DateTimeOffset.MinValue, 0, 0, 0, 0);
        }

        var payload = IpcJson.Deserialize<ListRelayPolicyListsResponse>(response.JsonPayload);
        if (payload is null)
        {
            const string invalidPayload = "Policy list fetch failed: invalid response payload.";
            SetAction(false, invalidPayload);
            return new RelayPolicyListsResult(false, invalidPayload, relayId, [], 0, DateTimeOffset.MinValue, 0, 0, 0, 0);
        }

        var lists = payload.Lists
            .OrderBy(x => x.Priority)
            .Select(x => new RelayPolicyListItemResult(x.ListId, x.RelayId, x.Label, x.ListType, x.Priority, x.EntryCount))
            .ToArray();
        var messageText = $"Loaded {lists.Length} policy list(s).";
        SetAction(true, messageText);
        return new RelayPolicyListsResult(
            true,
            messageText,
            payload.RelayId,
            lists,
            payload.Revision,
            payload.UpdatedAtUtc,
            payload.TotalListCount,
            payload.TotalEntryCount,
            payload.WhitelistListCount,
            payload.BlacklistListCount);
    }

    public async Task<RelayPolicyListDetailsResult> GetRelayPolicyListDetailsAsync(string relayId, string listId, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.GetRelayPolicyListAsync(listId, relayId, cancellationToken);
        if (response?.Success != true)
        {
            var message = $"Policy list load failed: {response?.Error ?? "service unavailable"}";
            SetAction(false, message);
            return new RelayPolicyListDetailsResult(false, message, listId, relayId, string.Empty, PolicyListTypes.Whitelist, 0, [], 0, 0, DateTimeOffset.MinValue);
        }

        var payload = IpcJson.Deserialize<GetRelayPolicyListResponse>(response.JsonPayload);
        if (payload is null)
        {
            const string invalidPayload = "Policy list load failed: invalid response payload.";
            SetAction(false, invalidPayload);
            return new RelayPolicyListDetailsResult(false, invalidPayload, listId, relayId, string.Empty, PolicyListTypes.Whitelist, 0, [], 0, 0, DateTimeOffset.MinValue);
        }

        SetAction(true, $"Loaded policy list '{payload.Label}'.");
        return new RelayPolicyListDetailsResult(
            true,
            "Loaded.",
            payload.ListId,
            payload.RelayId,
            payload.Label,
            payload.ListType,
            payload.Priority,
            payload.Entries,
            payload.Count,
            payload.Revision,
            payload.UpdatedAtUtc);
    }

    public async Task<RelayPolicyMutationResult> CreateRelayPolicyListAsync(string relayId, string label, string listType, int? priority = null, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.CreateRelayPolicyListAsync(label, listType, relayId, priority, cancellationToken);
        return ParseRelayPolicyMutationResponse(response, "create");
    }

    public async Task<RelayPolicyMutationResult> UpdateRelayPolicyListMetaAsync(string relayId, string listId, string label, string listType, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.UpdateRelayPolicyListMetaAsync(listId, label, listType, relayId, cancellationToken);
        return ParseRelayPolicyMutationResponse(response, "update");
    }

    public async Task<PolicyCommitSummary> ReplaceRelayPolicyListEntriesAsync(string relayId, string listId, IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.ReplaceRelayPolicyListEntriesAsync(listId, entries, relayId, cancellationToken);
        if (response?.Success != true)
        {
            var message = $"Policy entries update failed: {response?.Error ?? "service unavailable"}";
            SetAction(false, message);
            return new PolicyCommitSummary(false, message, listId, "replace", 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
        }

        var payload = IpcJson.Deserialize<ReplaceRelayPolicyListEntriesResponse>(response.JsonPayload);
        if (payload is null)
        {
            const string invalidPayload = "Policy entries update failed: invalid response payload.";
            SetAction(false, invalidPayload);
            return new PolicyCommitSummary(false, invalidPayload, listId, "replace", 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
        }

        var successMessage = $"Policy entries updated. applied={payload.AppliedCount}, duplicates={payload.DuplicateDroppedCount}, invalid={payload.InvalidCount}, total={payload.Count}.";
        SetAction(true, successMessage);
        return new PolicyCommitSummary(
            true,
            successMessage,
            payload.ListId,
            "replace",
            payload.AppliedCount,
            payload.DuplicateDroppedCount,
            payload.InvalidCount,
            payload.Count,
            payload.Revision,
            payload.UpdatedAtUtc);
    }

    public async Task<OperationResult> ReorderRelayPolicyListsAsync(string relayId, IReadOnlyList<string> orderedListIds, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.ReorderRelayPolicyListsAsync(orderedListIds, relayId, cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Policy reorder failed: {response?.Error ?? "service unavailable"}");
        }

        return SetAction(true, "Policy lists reordered.");
    }

    public async Task<OperationResult> DeleteRelayPolicyListAsync(string relayId, string listId, CancellationToken cancellationToken = default)
    {
        var response = await _gatewayClient.DeleteRelayPolicyListAsync(listId, relayId, cancellationToken);
        if (response?.Success != true)
        {
            return SetAction(false, $"Policy list delete failed: {response?.Error ?? "service unavailable"}");
        }

        return SetAction(true, "Policy list deleted.");
    }

    private RelayPolicyMutationResult ParseRelayPolicyMutationResponse(IpcResponse? response, string actionName)
    {
        if (response?.Success != true)
        {
            var message = $"Policy list {actionName} failed: {response?.Error ?? "service unavailable"}";
            SetAction(false, message);
            return new RelayPolicyMutationResult(false, message, string.Empty, string.Empty, string.Empty, PolicyListTypes.Whitelist, 0, 0, 0, DateTimeOffset.MinValue);
        }

        var payload = IpcJson.Deserialize<RelayPolicyListMutationResponse>(response.JsonPayload);
        if (payload is null)
        {
            var message = $"Policy list {actionName} failed: invalid response payload.";
            SetAction(false, message);
            return new RelayPolicyMutationResult(false, message, string.Empty, string.Empty, string.Empty, PolicyListTypes.Whitelist, 0, 0, 0, DateTimeOffset.MinValue);
        }

        SetAction(true, $"Policy list {actionName} completed.");
        return new RelayPolicyMutationResult(
            true,
            "OK",
            payload.ListId,
            payload.RelayId,
            payload.Label,
            payload.ListType,
            payload.Priority,
            payload.EntryCount,
            payload.Revision,
            payload.UpdatedAtUtc);
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
        status.LicenseSource = payload.Source;
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
            var source = string.IsNullOrWhiteSpace(payload.Source)
                ? (payload.FromCache ? "legacy_cache" : "online")
                : payload.Source;
            return SetAction(true, $"License valid. Expires: {payload.ExpiresAtUtc:O}. Source: {source}.");
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

    public async Task<OperationResult> TestRelayTunnelConnectionAsync(RelayConfig relay, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(GatewayTypes.Normalize(relay.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
        {
            return SetAction(true, "Local Relay mode selected; SSH tunnel test skipped.");
        }

        var config = BuildServiceConfigForRelay(relay);
        var response = await _gatewayClient.TestTunnelConnectionAsync(config, cancellationToken);
        return response?.Success == true
            ? SetAction(true, "Relay tunnel connection test succeeded.")
            : SetAction(false, $"Relay tunnel test failed: {response?.Error ?? "service unavailable"}");
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

    public IReadOnlyList<string> ValidatePolicyLines(string? text)
    {
        var lines = (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
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
        var gatewayType = GatewayTypes.Normalize(_state.GatewayType);

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
        if (string.Equals(gatewayType, GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase) &&
            requireTunnelAuthSecrets &&
            authMethod == TunnelAuthMethods.Password &&
            string.IsNullOrWhiteSpace(_state.TunnelPassword))
        {
            throw new InvalidOperationException("Tunnel password is required when password authentication is selected.");
        }

        if (string.Equals(gatewayType, GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase) &&
            requireTunnelAuthSecrets &&
            authMethod == TunnelAuthMethods.HostKey &&
            string.IsNullOrWhiteSpace(_state.TunnelKeyPath))
        {
            throw new InvalidOperationException("Tunnel host key file path is required when host-key authentication is selected.");
        }

        var localPort = ParsePositivePort(_state.LocalGatewayPortText, "Local gateway port");
        var localBind = string.IsNullOrWhiteSpace(_state.LocalGatewayBindAddress)
            ? "0.0.0.0"
            : _state.LocalGatewayBindAddress.Trim();
        if (!string.Equals(localBind, "0.0.0.0", StringComparison.OrdinalIgnoreCase) &&
            !System.Net.IPAddress.TryParse(localBind, out _))
        {
            throw new InvalidOperationException("Local gateway bind address must be 0.0.0.0 or a valid IP address.");
        }
        var localRemoteAddress = string.IsNullOrWhiteSpace(_state.LocalGatewayRemoteAddress)
            ? string.Empty
            : _state.LocalGatewayRemoteAddress.Trim();
        if (!string.IsNullOrWhiteSpace(localRemoteAddress) && Uri.CheckHostName(localRemoteAddress) == UriHostNameType.Unknown)
        {
            throw new InvalidOperationException("Local gateway remote address must be empty, a hostname, or an IP address.");
        }

        return new ServiceConfig
        {
            GatewayType = gatewayType,
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
            LicenseKey = _state.LicenseKey.Trim(),
            LocalGateway = new LocalGatewayConfig
            {
                Protocol = LocalGatewayProtocols.Normalize(_state.LocalGatewayProtocol),
                Port = localPort,
                BindAddress = localBind,
                RemoteAddress = localRemoteAddress,
                Remark = string.IsNullOrWhiteSpace(_state.LocalGatewayRemark)
                    ? "OmniRelay Local Gateway"
                    : _state.LocalGatewayRemark.Trim(),
                RuntimeEnabled = _state.LocalGatewayRuntimeEnabled
            }
        };
    }

    private static ServiceConfig BuildServiceConfigForRelay(RelayConfig relay)
    {
        var remote = relay.RemoteGateway ?? new RemoteGatewayConfig();
        return new ServiceConfig
        {
            GatewayType = GatewayTypes.Normalize(relay.GatewayType),
            LocalProxyListenPort = NormalizePortOrDefault(relay.DataPlaneLocalPort, 24080),
            BootstrapSocksLocalPort = NormalizePortOrDefault(relay.BootstrapSocksLocalPort, 24081),
            BootstrapSocksRemotePort = NormalizePortOrDefault(relay.BootstrapSocksRemotePort, 16080),
            WhitelistAdapterIfIndex = relay.IncomingAdapterIfIndex,
            DefaultAdapterIfIndex = relay.OutgoingAdapterIfIndex,
            TunnelHost = remote.TunnelHost,
            TunnelSshPort = NormalizePortOrDefault(remote.TunnelSshPort, 22),
            TunnelRemotePort = NormalizePortOrDefault(remote.TunnelRemotePort, 15000),
            TunnelUser = string.IsNullOrWhiteSpace(remote.TunnelUser) ? "OmniRelay" : remote.TunnelUser.Trim(),
            TunnelAuthMethod = TunnelAuthMethods.Normalize(remote.TunnelAuthMethod),
            TunnelPrivateKeyPath = remote.TunnelPrivateKeyPath,
            TunnelPrivateKeyPassphrase = remote.TunnelPrivateKeyPassphrase,
            TunnelPassword = remote.TunnelPassword
        };
    }

    private static int NormalizePortOrDefault(int port, int fallback)
    {
        return port > 0 && port <= 65535 ? port : fallback;
    }

    private OperationResult SetAction(bool success, string message)
    {
        _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
        return new OperationResult(success, message);
    }

    private void SyncRelayList(IReadOnlyList<RelayStatus> statuses)
    {
        foreach (var status in statuses)
        {
            if (status is null || string.IsNullOrWhiteSpace(status.RelayId))
            {
                continue;
            }

            var relayId = status.RelayId.Trim();
            var relay = _state.Relays.FirstOrDefault(x => x is not null && string.Equals(x.Id, relayId, StringComparison.Ordinal));
            if (relay is null)
            {
                _state.Relays.Add(new RelayConfig
                {
                    Id = relayId,
                    Name = string.IsNullOrWhiteSpace(status.Name) ? "Relay" : status.Name.Trim(),
                    GatewayType = GatewayTypes.Normalize(status.GatewayType),
                    Enabled = status.Enabled,
                    RemoteGateway = new RemoteGatewayConfig(),
                    LocalGateway = new LocalGatewayConfig()
                });
            }
            else
            {
                relay.Name = string.IsNullOrWhiteSpace(status.Name) ? relay.Name : status.Name.Trim();
                relay.GatewayType = GatewayTypes.Normalize(status.GatewayType);
                relay.Enabled = status.Enabled;
                relay.RemoteGateway ??= new RemoteGatewayConfig();
                relay.LocalGateway ??= new LocalGatewayConfig();
            }
        }
    }

    private static RelayConfig NormalizeRelayForUi(RelayConfig relay)
    {
        relay.Id = string.IsNullOrWhiteSpace(relay.Id) ? Guid.NewGuid().ToString("N") : relay.Id.Trim();
        relay.Name = string.IsNullOrWhiteSpace(relay.Name) ? "Relay" : relay.Name.Trim();
        relay.GatewayType = GatewayTypes.Normalize(relay.GatewayType);
        relay.IncomingAdapterId = relay.IncomingAdapterId ?? string.Empty;
        relay.OutgoingAdapterId = relay.OutgoingAdapterId ?? string.Empty;
        relay.RemoteGateway ??= new RemoteGatewayConfig();
        relay.LocalGateway ??= new LocalGatewayConfig();
        return relay;
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

        if (e.PropertyName == nameof(GatewayStateStore.GatewayType))
        {
            ApplyGatewayTypeSelections();
        }

        PersistUiState();
    }

    private void PersistUiState()
    {
        var remoteProfile = new GatewayRemoteUiProfileModel
        {
            VpsAdapterIfIndex = _state.VpsAdapter?.IfIndex,
            OutgoingAdapterIfIndex = _state.OutgoingAdapter?.IfIndex,
            ProxyPortText = _state.ProxyPortText,
            BootstrapSocksLocalPortText = _state.BootstrapSocksLocalPortText,
            BootstrapSocksRemotePortText = _state.BootstrapSocksRemotePortText,
            BootstrapMode = GatewayBootstrapModes.Normalize(_state.BootstrapMode),
            TunnelHost = _state.TunnelHost,
            TunnelSshPortText = _state.TunnelSshPortText,
            TunnelRemotePortText = _state.TunnelRemotePortText,
            SelectedGatewayProtocol = _state.SelectedGatewayProtocol,
            GatewayPublicPortText = _state.GatewayPublicPortText,
            GatewayPanelPortText = _state.GatewayPanelPortText,
            GatewayPanelConfiguredUser = _state.GatewayPanelConfiguredUser,
            EncryptedGatewayPanelConfiguredPassword = GatewayStatePersistenceService.Protect(_state.GatewayPanelConfiguredPassword),
            GatewayPanelDomain = _state.GatewayPanelDomain,
            GatewayPanelDomainOnly = _state.GatewayPanelDomainOnly,
            GatewayPanelUseSsl = _state.GatewayPanelUseSsl,
            GatewayPanelSslMode = _state.GatewayPanelSslMode,
            GatewayBackendPortText = _state.GatewayBackendPortText,
            GatewaySni = _state.GatewaySni,
            GatewayTarget = _state.GatewayTarget,
            GatewayProtocolTlsEnabled = _state.GatewayProtocolTlsEnabled,
            GatewayProtocolTlsServerName = _state.GatewayProtocolTlsServerName,
            GatewayProtocolCertPath = _state.GatewayProtocolCertPath,
            GatewayProtocolKeyPath = _state.GatewayProtocolKeyPath,
            GatewayProtocolTlsMode = _state.GatewayProtocolTlsMode,
            GatewayProtocolAlpnCsv = _state.GatewayProtocolAlpnCsv,
            GatewayProxyUsername = _state.GatewayProxyUsername,
            GatewayProxyPassword = _state.GatewayProxyPassword,
            VlessTlsFlow = _state.VlessTlsFlow,
            Hysteria2UpMbpsText = _state.Hysteria2UpMbpsText,
            Hysteria2DownMbpsText = _state.Hysteria2DownMbpsText,
            Hysteria2ObfsPassword = _state.Hysteria2ObfsPassword,
            Hysteria2IgnoreClientBandwidth = _state.Hysteria2IgnoreClientBandwidth,
            Hysteria2MasqueradeUrl = _state.Hysteria2MasqueradeUrl,
            NaiveNetwork = _state.NaiveNetwork,
            NaiveQuicCongestionControl = _state.NaiveQuicCongestionControl,
            ShadowTlsCamouflageServer = _state.ShadowTlsCamouflageServer,
            ShadowTlsStrictMode = _state.ShadowTlsStrictMode,
            ShadowTlsWildcardSni = _state.ShadowTlsWildcardSni,
            OpenVpnNetwork = _state.OpenVpnNetwork,
            IpsecL2tpNetwork = _state.IpsecL2tpNetwork,
            GatewayDohEndpointsText = _state.GatewayDohEndpointsText,
            TunnelUser = _state.TunnelUser,
            TunnelAuthMethod = _state.TunnelAuthMethod,
            TunnelKeyPath = _state.TunnelKeyPath,
            EncryptedTunnelKeyPassphrase = GatewayStatePersistenceService.Protect(_state.TunnelKeyPassphrase),
            EncryptedTunnelPassword = GatewayStatePersistenceService.Protect(_state.TunnelPassword),
            EncryptedLicenseKey = GatewayStatePersistenceService.Protect(_state.LicenseKey)
        };

        var localProfile = new GatewayLocalUiProfileModel
        {
            OutgoingAdapterIfIndex = _state.OutgoingAdapter?.IfIndex,
            LocalGatewayProtocol = _state.LocalGatewayProtocol,
            LocalGatewayPortText = _state.LocalGatewayPortText,
            LocalGatewayBindAddress = _state.LocalGatewayBindAddress,
            LocalGatewayRemoteAddress = _state.LocalGatewayRemoteAddress,
            LocalGatewayRemark = _state.LocalGatewayRemark,
            LocalGatewayRuntimeEnabled = _state.LocalGatewayRuntimeEnabled,
            EncryptedLicenseKey = GatewayStatePersistenceService.Protect(_state.LicenseKey)
        };

        var snapshot = new GatewayUiStateModel
        {
            GatewayType = GatewayTypes.Normalize(_state.GatewayType),
            VpsAdapterIfIndex = _state.VpsAdapter?.IfIndex,
            OutgoingAdapterIfIndex = _state.OutgoingAdapter?.IfIndex,
            ProxyPortText = _state.ProxyPortText,
            BootstrapSocksLocalPortText = _state.BootstrapSocksLocalPortText,
            BootstrapSocksRemotePortText = _state.BootstrapSocksRemotePortText,
            BootstrapMode = GatewayBootstrapModes.Normalize(_state.BootstrapMode),
            TunnelHost = _state.TunnelHost,
            TunnelSshPortText = _state.TunnelSshPortText,
            TunnelRemotePortText = _state.TunnelRemotePortText,
            SelectedGatewayProtocol = _state.SelectedGatewayProtocol,
            GatewayPublicPortText = _state.GatewayPublicPortText,
            GatewayPanelPortText = _state.GatewayPanelPortText,
            GatewayPanelConfiguredUser = _state.GatewayPanelConfiguredUser,
            EncryptedGatewayPanelConfiguredPassword = GatewayStatePersistenceService.Protect(_state.GatewayPanelConfiguredPassword),
            GatewayPanelDomain = _state.GatewayPanelDomain,
            GatewayPanelDomainOnly = _state.GatewayPanelDomainOnly,
            GatewayPanelUseSsl = _state.GatewayPanelUseSsl,
            GatewayPanelSslMode = _state.GatewayPanelSslMode,
            // Local upload picks are intentionally not persisted across sessions.
            GatewayPanelUploadedCertPath = string.Empty,
            GatewayPanelUploadedKeyPath = string.Empty,
            GatewayBackendPortText = _state.GatewayBackendPortText,
            GatewaySni = _state.GatewaySni,
            GatewayTarget = _state.GatewayTarget,
            GatewayProtocolTlsEnabled = _state.GatewayProtocolTlsEnabled,
            GatewayProtocolTlsServerName = _state.GatewayProtocolTlsServerName,
            GatewayProtocolCertPath = _state.GatewayProtocolCertPath,
            GatewayProtocolKeyPath = _state.GatewayProtocolKeyPath,
            GatewayProtocolTlsMode = _state.GatewayProtocolTlsMode,
            GatewayProtocolAlpnCsv = _state.GatewayProtocolAlpnCsv,
            GatewayProxyUsername = _state.GatewayProxyUsername,
            GatewayProxyPassword = _state.GatewayProxyPassword,
            VlessTlsFlow = _state.VlessTlsFlow,
            Hysteria2UpMbpsText = _state.Hysteria2UpMbpsText,
            Hysteria2DownMbpsText = _state.Hysteria2DownMbpsText,
            Hysteria2ObfsPassword = _state.Hysteria2ObfsPassword,
            Hysteria2IgnoreClientBandwidth = _state.Hysteria2IgnoreClientBandwidth,
            Hysteria2MasqueradeUrl = _state.Hysteria2MasqueradeUrl,
            NaiveNetwork = _state.NaiveNetwork,
            NaiveQuicCongestionControl = _state.NaiveQuicCongestionControl,
            ShadowTlsCamouflageServer = _state.ShadowTlsCamouflageServer,
            ShadowTlsStrictMode = _state.ShadowTlsStrictMode,
            ShadowTlsWildcardSni = _state.ShadowTlsWildcardSni,
            OpenVpnNetwork = _state.OpenVpnNetwork,
            IpsecL2tpNetwork = _state.IpsecL2tpNetwork,
            GatewayDohEndpointsText = _state.GatewayDohEndpointsText,
            // Install result credentials are intentionally not persisted.
            GatewayPanelUrl = string.Empty,
            GatewayPanelUsername = string.Empty,
            GatewayInitialPanelPassword = string.Empty,
            TunnelUser = _state.TunnelUser,
            TunnelAuthMethod = _state.TunnelAuthMethod,
            TunnelKeyPath = _state.TunnelKeyPath,
            EncryptedTunnelKeyPassphrase = GatewayStatePersistenceService.Protect(_state.TunnelKeyPassphrase),
            EncryptedTunnelPassword = GatewayStatePersistenceService.Protect(_state.TunnelPassword),
            EncryptedLicenseKey = GatewayStatePersistenceService.Protect(_state.LicenseKey),
            RemoteProfile = remoteProfile,
            LocalProfile = localProfile
        };

        _statePersistence.Save(snapshot);
    }

    private static GatewayRemoteUiProfileModel ResolveRemoteProfile(GatewayUiStateModel state)
    {
        if (state.RemoteProfile is not null)
        {
            return state.RemoteProfile;
        }

        return new GatewayRemoteUiProfileModel
        {
            VpsAdapterIfIndex = state.VpsAdapterIfIndex,
            OutgoingAdapterIfIndex = state.OutgoingAdapterIfIndex,
            ProxyPortText = state.ProxyPortText,
            BootstrapSocksLocalPortText = state.BootstrapSocksLocalPortText,
            BootstrapSocksRemotePortText = state.BootstrapSocksRemotePortText,
            BootstrapMode = GatewayBootstrapModes.Normalize(state.BootstrapMode),
            TunnelHost = state.TunnelHost,
            TunnelSshPortText = state.TunnelSshPortText,
            TunnelRemotePortText = state.TunnelRemotePortText,
            SelectedGatewayProtocol = state.SelectedGatewayProtocol,
            GatewayPublicPortText = state.GatewayPublicPortText,
            GatewayPanelPortText = state.GatewayPanelPortText,
            GatewayPanelConfiguredUser = state.GatewayPanelConfiguredUser,
            EncryptedGatewayPanelConfiguredPassword = state.EncryptedGatewayPanelConfiguredPassword,
            GatewayPanelDomain = state.GatewayPanelDomain,
            GatewayPanelDomainOnly = state.GatewayPanelDomainOnly,
            GatewayPanelUseSsl = state.GatewayPanelUseSsl,
            GatewayPanelSslMode = state.GatewayPanelSslMode,
            GatewayBackendPortText = state.GatewayBackendPortText,
            GatewaySni = state.GatewaySni,
            GatewayTarget = state.GatewayTarget,
            GatewayProtocolTlsEnabled = state.GatewayProtocolTlsEnabled,
            GatewayProtocolTlsServerName = state.GatewayProtocolTlsServerName,
            GatewayProtocolCertPath = state.GatewayProtocolCertPath,
            GatewayProtocolKeyPath = state.GatewayProtocolKeyPath,
            GatewayProtocolTlsMode = state.GatewayProtocolTlsMode,
            GatewayProtocolAlpnCsv = state.GatewayProtocolAlpnCsv,
            GatewayProxyUsername = state.GatewayProxyUsername,
            GatewayProxyPassword = state.GatewayProxyPassword,
            VlessTlsFlow = state.VlessTlsFlow,
            Hysteria2UpMbpsText = state.Hysteria2UpMbpsText,
            Hysteria2DownMbpsText = state.Hysteria2DownMbpsText,
            Hysteria2ObfsPassword = state.Hysteria2ObfsPassword,
            Hysteria2IgnoreClientBandwidth = state.Hysteria2IgnoreClientBandwidth,
            Hysteria2MasqueradeUrl = state.Hysteria2MasqueradeUrl,
            NaiveNetwork = state.NaiveNetwork,
            NaiveQuicCongestionControl = state.NaiveQuicCongestionControl,
            ShadowTlsCamouflageServer = state.ShadowTlsCamouflageServer,
            ShadowTlsStrictMode = state.ShadowTlsStrictMode,
            ShadowTlsWildcardSni = state.ShadowTlsWildcardSni,
            OpenVpnNetwork = state.OpenVpnNetwork,
            IpsecL2tpNetwork = state.IpsecL2tpNetwork,
            GatewayDohEndpointsText = state.GatewayDohEndpointsText,
            TunnelUser = state.TunnelUser,
            TunnelAuthMethod = state.TunnelAuthMethod,
            TunnelKeyPath = state.TunnelKeyPath,
            EncryptedTunnelKeyPassphrase = state.EncryptedTunnelKeyPassphrase,
            EncryptedTunnelPassword = state.EncryptedTunnelPassword,
            EncryptedLicenseKey = state.EncryptedLicenseKey
        };
    }

    private static GatewayLocalUiProfileModel ResolveLocalProfile(GatewayUiStateModel state)
    {
        if (state.LocalProfile is not null)
        {
            return state.LocalProfile;
        }

        return new GatewayLocalUiProfileModel
        {
            OutgoingAdapterIfIndex = state.OutgoingAdapterIfIndex,
            LocalGatewayProtocol = LocalGatewayProtocols.VlessTcpPlain,
            LocalGatewayPortText = "443",
            LocalGatewayBindAddress = "0.0.0.0",
            LocalGatewayRemoteAddress = string.Empty,
            LocalGatewayRemark = "OmniRelay Local Gateway",
            LocalGatewayRuntimeEnabled = true,
            EncryptedLicenseKey = state.EncryptedLicenseKey
        };
    }

    private bool IsOpenVpnLocalProtocolSelected()
    {
        var protocol = LocalGatewayProtocols.Normalize(_state.LocalGatewayProtocol);
        return string.Equals(protocol, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<OperationResult> WaitForLocalGatewayActivationAsync(
        string operation,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(180));
        var token = timeoutCts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var response = await _gatewayClient.GetStatusAsync(token);
                if (response?.Success == true)
                {
                    var payload = IpcJson.Deserialize<StatusResponse>(response.JsonPayload);
                    var status = payload?.Status;
                    if (status is not null)
                    {
                        _state.Status = status;
                        _state.GatewayType = GatewayTypes.Normalize(status.GatewayType);

                        var phase = BuildLocalGatewayPhaseMessage(status);
                        progress?.Invoke(phase);

                        if (string.Equals(status.LocalGatewayState, "active", StringComparison.OrdinalIgnoreCase))
                        {
                            await RefreshStatusAsync(cancellationToken);
                            return SetAction(true, $"Local gateway {operation} completed.");
                        }

                        if (string.Equals(status.LocalGatewayState, "unhealthy", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(status.LocalGatewayState, "unsupported", StringComparison.OrdinalIgnoreCase))
                        {
                            await RefreshStatusAsync(cancellationToken);
                            var reason = string.IsNullOrWhiteSpace(status.LocalGatewayHealthReason)
                                ? "unknown reason"
                                : status.LocalGatewayHealthReason!;
                            return SetAction(false, $"Local gateway {operation} failed: {reason}");
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout branch below.
        }

        await RefreshStatusAsync(cancellationToken);
        return SetAction(false, $"Local gateway {operation} timed out after 180 seconds.");
    }

    private static string BuildLocalGatewayPhaseMessage(GatewayStatus status)
    {
        if (string.Equals(status.LocalGatewayState, "active", StringComparison.OrdinalIgnoreCase))
        {
            return "Local OpenVPN runtime is active.";
        }

        if (string.Equals(status.LocalGatewayState, "activating", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(status.LocalGatewayHealthReason, "openvpn_installing", StringComparison.OrdinalIgnoreCase))
            {
                return "Installing OpenVPN runtime/driver...";
            }

            if (string.Equals(status.LocalGatewayHealthReason, "openvpn_starting", StringComparison.OrdinalIgnoreCase))
            {
                return "Starting OpenVPN runtime...";
            }

            return "Activating local OpenVPN runtime...";
        }

        if (string.Equals(status.LocalGatewayState, "unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            return $"Local OpenVPN failed: {status.LocalGatewayHealthReason ?? "unknown reason"}";
        }

        return $"Local gateway state: {status.LocalGatewayState}";
    }

    private static string NormalizePanelSslModeOrDefault(string? value, string fallback)
    {
        var normalized = NormalizeOrDefault(value, fallback).Trim().ToLowerInvariant();
        return normalized is "letsencrypt" or "uploaded" ? normalized : fallback;
    }

    private void ApplyGatewayTypeSelections()
    {
        var selectedGatewayType = GatewayTypes.Normalize(_state.GatewayType);
        var persisted = _statePersistence.Load();
        var remoteProfile = ResolveRemoteProfile(persisted);
        var localProfile = ResolveLocalProfile(persisted);

        var targetOutgoingIfIndex = string.Equals(selectedGatewayType, GatewayTypes.Local, StringComparison.OrdinalIgnoreCase)
            ? localProfile.OutgoingAdapterIfIndex
            : remoteProfile.OutgoingAdapterIfIndex;
        if (targetOutgoingIfIndex.HasValue)
        {
            var adapter = _state.Adapters.FirstOrDefault(x => x.IfIndex == targetOutgoingIfIndex.Value);
            if (adapter is not null)
            {
                _state.OutgoingAdapter = adapter;
            }
        }

        var encryptedLicense = string.Equals(selectedGatewayType, GatewayTypes.Local, StringComparison.OrdinalIgnoreCase)
            ? localProfile.EncryptedLicenseKey
            : remoteProfile.EncryptedLicenseKey;
        if (string.IsNullOrWhiteSpace(encryptedLicense))
        {
            encryptedLicense = remoteProfile.EncryptedLicenseKey;
        }
        if (!string.IsNullOrWhiteSpace(encryptedLicense))
        {
            _state.LicenseKey = GatewayStatePersistenceService.Unprotect(encryptedLicense);
        }
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
            return (false, bindError ?? "Incoming Network (IC1) adapter has no usable IPv4 address.");
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

    private static bool IsMissingPolicyCapability(string? error, string commandName)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) &&
               error.Contains(commandName, StringComparison.OrdinalIgnoreCase);
    }

}
