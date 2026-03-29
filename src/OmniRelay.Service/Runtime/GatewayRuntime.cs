using System.Net;
using System.Text.Json;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Licensing;
using OmniRelay.Core.Policy;
using OmniRelay.Core.Status;

namespace OmniRelay.Service.Runtime;

public sealed class GatewayRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly ConfigStore _configStore;
    private readonly PolicyStore _policyStore;
    private readonly FileLogWriter _log;

    private ServiceConfig _config;
    private IReadOnlyList<string> _whitelistEntries;
    private IReadOnlyList<string> _blacklistEntries;
    private PolicyAddressIndex _whitelistIndex;
    private PolicyAddressIndex _blacklistIndex;
    private long _policyRevision;
    private DateTimeOffset _policyUpdatedAtUtc;
    private bool _proxyRequested;
    private bool _licenseTransferRequested;
    private GatewayStatus _status;
    private long _tunnelRestartRequestVersion;
    private bool _localGatewayRequested;
    private bool _localGatewayRestartRequested;
    private long _localGatewayRequestVersion;
    private List<LocalGatewayClient> _localGatewayClients = [];

    public GatewayRuntime(ConfigStore configStore, PolicyStore policyStore, FileLogWriter log)
    {
        _configStore = configStore;
        _policyStore = policyStore;
        _log = log;

        var persisted = _configStore.Load();
        _config = CloneConfig(persisted.Config);
        _whitelistEntries = [];
        _blacklistEntries = [];
        _whitelistIndex = PolicyAddressIndex.Build([]);
        _blacklistIndex = PolicyAddressIndex.Build([]);
        _status = new GatewayStatus();
        _localGatewayClients = LoadLocalGatewayClients();
        _localGatewayRequested = _config.LocalGateway.RuntimeEnabled;

        var policySnapshot = _policyStore.Load();
        if (policySnapshot.WhitelistEntries.Count == 0 && persisted.WhitelistEntries.Count > 0)
        {
            _policyStore.ImportLegacyWhitelistIfEmpty(persisted.WhitelistEntries);
            policySnapshot = _policyStore.Load();
        }

        ApplyPolicySnapshotLocked(policySnapshot);

        _status = new GatewayStatus
        {
            ServiceRunning = true,
            ProxyRunning = false,
            ProxyListenPort = _config.LocalProxyListenPort,
            WhitelistCount = _whitelistEntries.Count,
            BlacklistCount = _blacklistEntries.Count,
            GatewayType = GatewayTypes.Normalize(_config.GatewayType),
            LocalGatewayState = "inactive",
            LocalGatewayProtocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol),
            LocalGatewayPort = _config.LocalGateway.Port,
            LocalGatewayClientsCount = _localGatewayClients.Count
        };

        // Keep relay data-plane active by default after service startup.
        // ProxyCoordinatorWorker will still stop listeners if license is invalid.
        _proxyRequested = true;
    }

    public ServiceConfig GetConfigSnapshot()
    {
        lock (_sync)
        {
            return CloneConfig(_config);
        }
    }

    public IReadOnlyList<LocalGatewayClient> GetLocalGatewayClientsSnapshot()
    {
        lock (_sync)
        {
            return _localGatewayClients
                .Select(CloneLocalGatewayClient)
                .ToList();
        }
    }

    public bool TryAddLocalGatewayClient(string email, string? remark, out LocalGatewayClient? client, out string? error)
    {
        lock (_sync)
        {
            var normalizedEmail = (email ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                client = null;
                error = "Client email is required.";
                return false;
            }

            var exists = _localGatewayClients.Any(x =>
                string.Equals(x.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                client = null;
                error = "Client email already exists.";
                return false;
            }

            var protocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);
            var created = new LocalGatewayClient
            {
                Id = Guid.NewGuid().ToString(),
                Email = normalizedEmail,
                Enabled = true,
                Remark = string.IsNullOrWhiteSpace(remark) ? normalizedEmail : remark.Trim(),
                Protocol = protocol,
                Secret = CreateLocalGatewaySecret(protocol),
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            _localGatewayClients.Add(created);
            PersistLocalGatewayClientsLocked();
            _status.LocalGatewayClientsCount = _localGatewayClients.Count;
            _localGatewayRestartRequested = true;
            _localGatewayRequestVersion++;

            client = CloneLocalGatewayClient(created);
            error = null;
            return true;
        }
    }

    public bool TryUpdateLocalGatewayClient(LocalGatewayClient client, out string? error)
    {
        lock (_sync)
        {
            var id = (client.Id ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "Client id is required.";
                return false;
            }

            var existing = _localGatewayClients.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (existing is null)
            {
                error = "Client not found.";
                return false;
            }

            var normalizedEmail = (client.Email ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                error = "Client email is required.";
                return false;
            }

            var duplicate = _localGatewayClients.Any(x =>
                !string.Equals(x.Id, id, StringComparison.Ordinal) &&
                string.Equals(x.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                error = "Client email already exists.";
                return false;
            }

            existing.Email = normalizedEmail;
            existing.Enabled = client.Enabled;
            existing.Remark = string.IsNullOrWhiteSpace(client.Remark) ? normalizedEmail : client.Remark.Trim();
            if (!string.IsNullOrWhiteSpace(client.Secret))
            {
                existing.Secret = client.Secret.Trim();
            }

            PersistLocalGatewayClientsLocked();
            _localGatewayRestartRequested = true;
            _localGatewayRequestVersion++;
            error = null;
            return true;
        }
    }

    public bool TryDeleteLocalGatewayClient(string clientId, out string? error)
    {
        lock (_sync)
        {
            var id = (clientId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "Client id is required.";
                return false;
            }

            var removed = _localGatewayClients.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (removed <= 0)
            {
                error = "Client not found.";
                return false;
            }

            PersistLocalGatewayClientsLocked();
            _status.LocalGatewayClientsCount = _localGatewayClients.Count;
            _localGatewayRestartRequested = true;
            _localGatewayRequestVersion++;
            error = null;
            return true;
        }
    }

    public bool TryBuildLocalGatewayClientUri(string clientId, out string uri, out string title, out string? error)
    {
        lock (_sync)
        {
            var id = (clientId ?? string.Empty).Trim();
            var client = _localGatewayClients.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (client is null)
            {
                uri = string.Empty;
                title = string.Empty;
                error = "Client not found.";
                return false;
            }

            var host = (_status.WhitelistAdapterIp ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                host = (_status.DefaultAdapterIp ?? string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                host = "127.0.0.1";
            }

            var protocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);
            var port = _config.LocalGateway.Port;
            var display = string.IsNullOrWhiteSpace(client.Remark) ? client.Email : client.Remark;
            if (string.Equals(protocol, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
            {
                const string method = "aes-128-gcm";
                var userInfo = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{method}:{client.Secret}"))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                uri = $"ss://{userInfo}@{host}:{port}#{Uri.EscapeDataString(display)}";
                title = "Shadowsocks Config";
                error = null;
                return true;
            }

            var query = "type=tcp&security=none&encryption=none";
            uri = $"vless://{client.Id}@{host}:{port}?{query}#{Uri.EscapeDataString(display)}";
            title = "VLESS Config";
            error = null;
            return true;
        }
    }

    public IReadOnlyList<string> GetWhitelistEntriesSnapshot()
    {
        lock (_sync)
        {
            return _whitelistEntries.ToList();
        }
    }

    public IReadOnlyList<string> GetBlacklistEntriesSnapshot()
    {
        lock (_sync)
        {
            return _blacklistEntries.ToList();
        }
    }

    public PolicyListSnapshot GetPolicyListSnapshot(string listType)
    {
        lock (_sync)
        {
            var normalized = PolicyListTypes.Normalize(listType);
            var entries = normalized == PolicyListTypes.Blacklist
                ? _blacklistEntries
                : _whitelistEntries;
            return new PolicyListSnapshot(
                normalized,
                entries.ToList(),
                entries.Count,
                _policyRevision,
                _policyUpdatedAtUtc);
        }
    }

    public void SetConfig(ServiceConfig config)
    {
        lock (_sync)
        {
            var previous = CloneConfig(_config);
            _config = CloneConfig(config);
            _config.GatewayType = GatewayTypes.Normalize(_config.GatewayType);
            _config.LocalGateway.Protocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);
            _status.ProxyListenPort = _config.LocalProxyListenPort;
            _status.GatewayType = _config.GatewayType;
            _status.LocalGatewayProtocol = _config.LocalGateway.Protocol;
            _status.LocalGatewayPort = _config.LocalGateway.Port;
            _localGatewayRequested = _config.LocalGateway.RuntimeEnabled;

            if (string.Equals(_config.GatewayType, GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase) &&
                RequiresTunnelRestart(previous, _config, out var changeSummary))
            {
                RequestTunnelRestartLocked($"Tunnel restart requested after config change: {changeSummary}.");
            }
            else if (!string.Equals(_config.GatewayType, GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
            {
                _status.TunnelState = "LocalMode";
                _status.HealthState = "Healthy";
                _status.HealthReasonCode = null;
            }

            _localGatewayRestartRequested = true;
            _localGatewayRequestVersion++;

            PersistLocked();
        }
    }

    public void SetLicenseKey(string licenseKey)
    {
        lock (_sync)
        {
            var normalized = (licenseKey ?? string.Empty).Trim();
            if (!string.Equals(_config.LicenseKey ?? string.Empty, normalized, StringComparison.Ordinal))
            {
                // Clear pending transfer only when operator actually changes the key.
                _licenseTransferRequested = false;
            }

            _config.LicenseKey = normalized;
            PersistLocked();
        }
    }

    public bool TryApplyLocalGatewayConfig(LocalGatewayConfig config, out string? error)
    {
        lock (_sync)
        {
            var protocol = LocalGatewayProtocols.Normalize(config.Protocol);
            if (!LocalGatewayProtocols.IsSupportedInV1(protocol))
            {
                error = "Local protocol is not supported in v1.";
                return false;
            }

            if (config.Port <= 0 || config.Port > 65535)
            {
                error = "Local gateway port is invalid.";
                return false;
            }

            var bindAddress = string.IsNullOrWhiteSpace(config.BindAddress) ? "0.0.0.0" : config.BindAddress.Trim();
            if (!string.Equals(bindAddress, "0.0.0.0", StringComparison.OrdinalIgnoreCase) &&
                !IPAddress.TryParse(bindAddress, out _))
            {
                error = "Bind address must be 0.0.0.0 or a valid IPv4/IPv6 address.";
                return false;
            }

            _config.GatewayType = GatewayTypes.Local;
            _config.LocalGateway.Protocol = protocol;
            _config.LocalGateway.Port = config.Port;
            _config.LocalGateway.BindAddress = bindAddress;
            _config.LocalGateway.Remark = string.IsNullOrWhiteSpace(config.Remark) ? "OmniRelay Local Gateway" : config.Remark.Trim();
            _config.LocalGateway.RuntimeEnabled = config.RuntimeEnabled;

            foreach (var client in _localGatewayClients)
            {
                client.Protocol = protocol;
            }

            _localGatewayRequested = _config.LocalGateway.RuntimeEnabled;
            _localGatewayRestartRequested = true;
            _localGatewayRequestVersion++;
            _status.GatewayType = _config.GatewayType;
            _status.LocalGatewayProtocol = _config.LocalGateway.Protocol;
            _status.LocalGatewayPort = _config.LocalGateway.Port;
            _status.LocalGatewayClientsCount = _localGatewayClients.Count;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
            PersistLocalGatewayClientsLocked();
            PersistLocked();
            error = null;
            return true;
        }
    }

    public void RequestLicenseTransfer()
    {
        lock (_sync)
        {
            _licenseTransferRequested = true;
        }
    }

    public bool ConsumeLicenseTransferRequest()
    {
        lock (_sync)
        {
            var requested = _licenseTransferRequested;
            _licenseTransferRequested = false;
            return requested;
        }
    }

    public bool TryUpdateWhitelist(IEnumerable<string> entries, out string? error)
    {
        return TryApplyPolicyUpdate(
            PolicyListTypes.Whitelist,
            PolicyUpdateModes.Replace,
            entries.ToList(),
            out _,
            out error);
    }

    public bool TryApplyPolicyUpdate(
        string listType,
        string mode,
        IReadOnlyList<string> entries,
        out PolicyCommitResult? result,
        out string? error)
    {
        lock (_sync)
        {
            try
            {
                result = _policyStore.ApplyUpdate(listType, mode, entries);
                ApplyPolicySnapshotLocked(_policyStore.Load());
                PersistLocked();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                result = null;
                error = ex.Message;
                return false;
            }
        }
    }

    public bool ShouldUseWhitelistAdapter(IPAddress? destinationAddress)
    {
        lock (_sync)
        {
            return _whitelistIndex.Matches(destinationAddress);
        }
    }

    public bool ShouldBlockDestination(IPAddress? destinationAddress)
    {
        lock (_sync)
        {
            return _blacklistIndex.Matches(destinationAddress);
        }
    }

    public void RequestProxyStart()
    {
        lock (_sync)
        {
            _proxyRequested = true;
        }
    }

    public void RequestProxyStop()
    {
        lock (_sync)
        {
            _proxyRequested = false;
            _status.ProxyRunning = false;
        }
    }

    public void RequestLocalGatewayStart()
    {
        lock (_sync)
        {
            _localGatewayRequested = true;
            _config.LocalGateway.RuntimeEnabled = true;
            _localGatewayRestartRequested = false;
            _localGatewayRequestVersion++;
            PersistLocked();
        }
    }

    public void RequestLocalGatewayStop()
    {
        lock (_sync)
        {
            _localGatewayRequested = false;
            _config.LocalGateway.RuntimeEnabled = false;
            _localGatewayRestartRequested = false;
            _localGatewayRequestVersion++;
            PersistLocked();
        }
    }

    public void RequestLocalGatewayRestart()
    {
        lock (_sync)
        {
            _localGatewayRequested = true;
            _config.LocalGateway.RuntimeEnabled = true;
            _localGatewayRestartRequested = true;
            _localGatewayRequestVersion++;
            PersistLocked();
        }
    }

    public long GetLocalGatewayRequestVersion()
    {
        lock (_sync)
        {
            return _localGatewayRequestVersion;
        }
    }

    public bool IsLocalGatewayRequested()
    {
        lock (_sync)
        {
            return _localGatewayRequested;
        }
    }

    public bool ConsumeLocalGatewayRestartRequested()
    {
        lock (_sync)
        {
            var requested = _localGatewayRestartRequested;
            _localGatewayRestartRequested = false;
            return requested;
        }
    }

    public bool IsProxyRequested()
    {
        lock (_sync)
        {
            return _proxyRequested;
        }
    }

    public void SetProxyRunning(bool running, int port)
    {
        lock (_sync)
        {
            _status.ProxyRunning = running;
            _status.ProxyListenPort = port;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetAdapterIps(string? whitelistAdapterIp, string? defaultAdapterIp)
    {
        lock (_sync)
        {
            _status.WhitelistAdapterIp = whitelistAdapterIp;
            _status.DefaultAdapterIp = defaultAdapterIp;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetTunnelStatus(bool connected, DateTimeOffset? connectedAtUtc, int reconnectCount, string? error)
    {
        lock (_sync)
        {
            _status.TunnelConnected = connected;
            _status.TunnelLastConnectedAtUtc = connectedAtUtc;
            _status.TunnelReconnectCount = reconnectCount;
            _status.TunnelLastError = error;
            _status.TunnelState = connected ? "Healthy" : "Disconnected";
            _status.HealthState = connected ? "Healthy" : "Disconnected";
            _status.HealthReasonCode = error;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetBootstrapSocksStatus(bool listening, bool remoteForwardActive, string? error)
    {
        lock (_sync)
        {
            _status.BootstrapSocksListening = listening;
            _status.BootstrapSocksRemoteForwardActive = remoteForwardActive;
            _status.BootstrapSocksLastError = error;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetLocalGatewayStatus(string state, string? reason, bool autoRecovered)
    {
        lock (_sync)
        {
            _status.LocalGatewayState = string.IsNullOrWhiteSpace(state) ? "inactive" : state.Trim();
            _status.LocalGatewayHealthReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            _status.LocalGatewayAutoRecovered = autoRecovered;
            _status.LocalGatewayProtocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);
            _status.LocalGatewayPort = _config.LocalGateway.Port;
            _status.LocalGatewayClientsCount = _localGatewayClients.Count;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetResilienceStatus(
        string tunnelState,
        string healthState,
        string? healthReasonCode,
        int consecutiveFailures,
        int recoveryTier,
        string? recoveryAction,
        DateTimeOffset? lastLocalProbeUtc,
        DateTimeOffset? lastEndToEndProbeUtc,
        DateTimeOffset? lastHealthyUtc,
        bool tunnelConnected,
        bool bootstrapSocksListening,
        bool bootstrapSocksRemoteForwardActive,
        string? tunnelLastError,
        string? bootstrapSocksLastError,
        int tunnelReconnectCount,
        DateTimeOffset? tunnelLastConnectedAtUtc,
        IReadOnlyList<string>? resilienceEvents)
    {
        lock (_sync)
        {
            _status.GatewayType = GatewayTypes.Normalize(_config.GatewayType);
            _status.TunnelState = tunnelState;
            _status.HealthState = healthState;
            _status.HealthReasonCode = healthReasonCode;
            _status.ConsecutiveFailures = Math.Max(0, consecutiveFailures);
            _status.RecoveryTier = Math.Max(0, recoveryTier);
            _status.RecoveryAction = recoveryAction;
            _status.LastLocalProbeUtc = lastLocalProbeUtc;
            _status.LastEndToEndProbeUtc = lastEndToEndProbeUtc;
            _status.LastHealthyUtc = lastHealthyUtc;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;

            _status.TunnelConnected = tunnelConnected;
            _status.BootstrapSocksListening = bootstrapSocksListening;
            _status.BootstrapSocksRemoteForwardActive = bootstrapSocksRemoteForwardActive;
            _status.TunnelLastError = tunnelLastError;
            _status.BootstrapSocksLastError = bootstrapSocksLastError;
            _status.TunnelReconnectCount = tunnelReconnectCount;
            _status.TunnelLastConnectedAtUtc = tunnelLastConnectedAtUtc;
            _status.ResilienceEvents = resilienceEvents?.ToArray() ?? [];
        }
    }

    public void RequestTunnelRestart(string reason)
    {
        lock (_sync)
        {
            RequestTunnelRestartLocked(reason);
        }
    }

    public long GetTunnelRestartRequestVersion()
    {
        lock (_sync)
        {
            return _tunnelRestartRequestVersion;
        }
    }

    public void SetLicenseStatus(LicenseValidationResult result)
    {
        lock (_sync)
        {
            _status.LicenseValid = result.IsValid;
            _status.LicenseFromCache = result.FromCache;
            _status.LicenseCheckedAtUtc = result.CheckedAtUtc;
            _status.LicenseExpiresAtUtc = result.ExpiresAtUtc;
            _status.LicenseReason = result.Reason;
            _status.LicenseTransferRequired = result.TransferRequired;
            _status.LicenseTransferLimitPerRollingYear = result.TransferLimitPerRollingYear;
            _status.LicenseTransfersUsedInWindow = result.TransfersUsedInWindow;
            _status.LicenseTransfersRemainingInWindow = result.TransfersRemainingInWindow;
            _status.LicenseTransferWindowStartAt = result.TransferWindowStartAt;
            _status.LicenseActiveDeviceHint = result.ActiveDeviceIdHint;
            _status.LastError = result.Error;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetError(string? message)
    {
        lock (_sync)
        {
            _status.LastError = message;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public GatewayStatus GetStatusSnapshot()
    {
        lock (_sync)
        {
            return new GatewayStatus
            {
                GatewayType = _status.GatewayType,
                TunnelState = _status.TunnelState,
                HealthState = _status.HealthState,
                HealthReasonCode = _status.HealthReasonCode,
                ConsecutiveFailures = _status.ConsecutiveFailures,
                RecoveryTier = _status.RecoveryTier,
                RecoveryAction = _status.RecoveryAction,
                LastLocalProbeUtc = _status.LastLocalProbeUtc,
                LastEndToEndProbeUtc = _status.LastEndToEndProbeUtc,
                LastHealthyUtc = _status.LastHealthyUtc,
                LastStatusUpdateUtc = _status.LastStatusUpdateUtc,
                ResilienceEvents = _status.ResilienceEvents?.ToArray() ?? [],
                ServiceRunning = _status.ServiceRunning,
                ProxyRunning = _status.ProxyRunning,
                ProxyListenPort = _status.ProxyListenPort,
                TunnelConnected = _status.TunnelConnected,
                TunnelLastConnectedAtUtc = _status.TunnelLastConnectedAtUtc,
                TunnelReconnectCount = _status.TunnelReconnectCount,
                TunnelLastError = _status.TunnelLastError,
                BootstrapSocksListening = _status.BootstrapSocksListening,
                BootstrapSocksRemoteForwardActive = _status.BootstrapSocksRemoteForwardActive,
                BootstrapSocksLastError = _status.BootstrapSocksLastError,
                LicenseValid = _status.LicenseValid,
                LicenseFromCache = _status.LicenseFromCache,
                LicenseCheckedAtUtc = _status.LicenseCheckedAtUtc,
                LicenseExpiresAtUtc = _status.LicenseExpiresAtUtc,
                LicenseReason = _status.LicenseReason,
                LicenseTransferRequired = _status.LicenseTransferRequired,
                LicenseTransferLimitPerRollingYear = _status.LicenseTransferLimitPerRollingYear,
                LicenseTransfersUsedInWindow = _status.LicenseTransfersUsedInWindow,
                LicenseTransfersRemainingInWindow = _status.LicenseTransfersRemainingInWindow,
                LicenseTransferWindowStartAt = _status.LicenseTransferWindowStartAt,
                LicenseActiveDeviceHint = _status.LicenseActiveDeviceHint,
                WhitelistAdapterIp = _status.WhitelistAdapterIp,
                DefaultAdapterIp = _status.DefaultAdapterIp,
                WhitelistCount = _status.WhitelistCount,
                BlacklistCount = _status.BlacklistCount,
                LastError = _status.LastError,
                LocalGatewayState = _status.LocalGatewayState,
                LocalGatewayHealthReason = _status.LocalGatewayHealthReason,
                LocalGatewayProtocol = _status.LocalGatewayProtocol,
                LocalGatewayPort = _status.LocalGatewayPort,
                LocalGatewayClientsCount = _status.LocalGatewayClientsCount,
                LocalGatewayAutoRecovered = _status.LocalGatewayAutoRecovered
            };
        }
    }

    private void PersistLocked()
    {
        _configStore.Save(_config);
    }

    private void RequestTunnelRestartLocked(string reason)
    {
        _tunnelRestartRequestVersion++;
        _status.TunnelConnected = false;
        _status.BootstrapSocksRemoteForwardActive = false;
        _status.TunnelLastError = reason;
        _status.BootstrapSocksLastError = reason;
        _status.TunnelState = "RecoveringTier1";
        _status.HealthState = "Degraded";
        _status.HealthReasonCode = reason;
        _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
    }

    private static bool RequiresTunnelRestart(ServiceConfig previous, ServiceConfig current, out string summary)
    {
        var changed = new List<string>(8);

        if (previous.WhitelistAdapterIfIndex != current.WhitelistAdapterIfIndex)
        {
            changed.Add("IC1 adapter");
        }

        if (!string.Equals((previous.TunnelHost ?? string.Empty).Trim(), (current.TunnelHost ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            changed.Add("tunnel host");
        }

        if (previous.TunnelSshPort != current.TunnelSshPort)
        {
            changed.Add("SSH port");
        }

        if (previous.TunnelRemotePort != current.TunnelRemotePort)
        {
            changed.Add("remote data port");
        }

        if (previous.BootstrapSocksRemotePort != current.BootstrapSocksRemotePort)
        {
            changed.Add("remote bootstrap port");
        }

        if (previous.LocalProxyListenPort != current.LocalProxyListenPort)
        {
            changed.Add("local proxy port");
        }

        if (previous.BootstrapSocksLocalPort != current.BootstrapSocksLocalPort)
        {
            changed.Add("local bootstrap port");
        }

        if (!string.Equals((previous.TunnelUser ?? string.Empty).Trim(), (current.TunnelUser ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            changed.Add("tunnel user");
        }

        if (!string.Equals(TunnelAuthMethods.Normalize(previous.TunnelAuthMethod), TunnelAuthMethods.Normalize(current.TunnelAuthMethod), StringComparison.Ordinal))
        {
            changed.Add("auth method");
        }

        if (!string.Equals((previous.TunnelPrivateKeyPath ?? string.Empty).Trim(), (current.TunnelPrivateKeyPath ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            changed.Add("private key path");
        }

        if (!string.Equals(previous.TunnelPrivateKeyPassphrase ?? string.Empty, current.TunnelPrivateKeyPassphrase ?? string.Empty, StringComparison.Ordinal))
        {
            changed.Add("private key passphrase");
        }

        if (!string.Equals(previous.TunnelPassword ?? string.Empty, current.TunnelPassword ?? string.Empty, StringComparison.Ordinal))
        {
            changed.Add("password");
        }

        summary = changed.Count == 0 ? string.Empty : string.Join(", ", changed);
        return changed.Count > 0;
    }

    private static ServiceConfig CloneConfig(ServiceConfig config)
    {
        return new ServiceConfig
        {
            SchemaVersion = config.SchemaVersion,
            GatewayType = GatewayTypes.Normalize(config.GatewayType),
            LocalProxyListenPort = config.LocalProxyListenPort,
            BootstrapSocksLocalPort = config.BootstrapSocksLocalPort,
            BootstrapSocksRemotePort = config.BootstrapSocksRemotePort,
            GatewayOnlineInstallEnabled = config.GatewayOnlineInstallEnabled,
            WhitelistAdapterIfIndex = config.WhitelistAdapterIfIndex,
            DefaultAdapterIfIndex = config.DefaultAdapterIfIndex,
            TunnelHost = config.TunnelHost,
            TunnelSshPort = config.TunnelSshPort,
            TunnelRemotePort = config.TunnelRemotePort,
            TunnelUser = config.TunnelUser,
            TunnelAuthMethod = config.TunnelAuthMethod,
            TunnelPrivateKeyPath = config.TunnelPrivateKeyPath,
            TunnelPrivateKeyPassphrase = config.TunnelPrivateKeyPassphrase,
            TunnelPassword = config.TunnelPassword,
            LicenseKey = config.LicenseKey,
            LocalGateway = new LocalGatewayConfig
            {
                Protocol = LocalGatewayProtocols.Normalize(config.LocalGateway?.Protocol),
                Port = config.LocalGateway?.Port is > 0 and <= 65535 ? config.LocalGateway.Port : 443,
                BindAddress = string.IsNullOrWhiteSpace(config.LocalGateway?.BindAddress) ? "0.0.0.0" : config.LocalGateway.BindAddress.Trim(),
                Remark = string.IsNullOrWhiteSpace(config.LocalGateway?.Remark) ? "OmniRelay Local Gateway" : config.LocalGateway.Remark.Trim(),
                RuntimeEnabled = config.LocalGateway?.RuntimeEnabled ?? true
            }
        };
    }

    private List<LocalGatewayClient> LoadLocalGatewayClients()
    {
        try
        {
            if (!File.Exists(ServicePaths.LocalGatewayClientsPath))
            {
                return [];
            }

            var raw = File.ReadAllText(ServicePaths.LocalGatewayClientsPath);
            var clients = JsonSerializer.Deserialize<List<LocalGatewayClient>>(raw, JsonOptions) ?? [];
            var normalized = new List<LocalGatewayClient>(clients.Count);
            foreach (var client in clients)
            {
                var id = (client.Id ?? string.Empty).Trim();
                var email = (client.Email ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(email))
                {
                    continue;
                }

                normalized.Add(new LocalGatewayClient
                {
                    Id = id,
                    Email = email,
                    Enabled = client.Enabled,
                    Remark = string.IsNullOrWhiteSpace(client.Remark) ? email : client.Remark.Trim(),
                    Protocol = LocalGatewayProtocols.Normalize(client.Protocol),
                    Secret = string.IsNullOrWhiteSpace(client.Secret) ? CreateLocalGatewaySecret(LocalGatewayProtocols.Normalize(client.Protocol)) : client.Secret.Trim(),
                    CreatedAtUtc = client.CreatedAtUtc == default ? DateTimeOffset.UtcNow : client.CreatedAtUtc
                });
            }

            return normalized;
        }
        catch (Exception ex)
        {
            _log.Error("Failed loading local gateway clients.", ex);
            return [];
        }
    }

    private void PersistLocalGatewayClientsLocked()
    {
        try
        {
            ServicePaths.EnsureDirectories();
            var raw = JsonSerializer.Serialize(_localGatewayClients, JsonOptions);
            File.WriteAllText(ServicePaths.LocalGatewayClientsPath, raw);
        }
        catch (Exception ex)
        {
            _log.Error("Failed persisting local gateway clients.", ex);
        }
    }

    private static LocalGatewayClient CloneLocalGatewayClient(LocalGatewayClient client)
    {
        return new LocalGatewayClient
        {
            Id = client.Id,
            Email = client.Email,
            Enabled = client.Enabled,
            Remark = client.Remark,
            Protocol = client.Protocol,
            Secret = client.Secret,
            CreatedAtUtc = client.CreatedAtUtc
        };
    }

    private static string CreateLocalGatewaySecret(string protocol)
    {
        if (string.Equals(LocalGatewayProtocols.Normalize(protocol), LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToHexString(Guid.NewGuid().ToByteArray());
        }

        return Guid.NewGuid().ToString();
    }

    private void ApplyPolicySnapshotLocked(PolicyStoreSnapshot snapshot)
    {
        _policyRevision = snapshot.Revision;
        _policyUpdatedAtUtc = snapshot.UpdatedAtUtc;
        _whitelistEntries = snapshot.WhitelistEntries
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _blacklistEntries = snapshot.BlacklistEntries
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _whitelistIndex = BuildIndex(_whitelistEntries, PolicyListTypes.Whitelist);
        _blacklistIndex = BuildIndex(_blacklistEntries, PolicyListTypes.Blacklist);
        _status.WhitelistCount = _whitelistEntries.Count;
        _status.BlacklistCount = _blacklistEntries.Count;
    }

    private PolicyAddressIndex BuildIndex(IReadOnlyList<string> entries, string listType)
    {
        var rules = new List<NetworkRule>(entries.Count);
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            if (NetworkRule.TryParse(entry, out var rule, out var error) && rule is not null)
            {
                rules.Add(rule);
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
        {
            _log.Warn($"Invalid persisted {listType} entries ignored: {string.Join("; ", errors.Take(12))}");
        }

        return PolicyAddressIndex.Build(rules);
    }
}
