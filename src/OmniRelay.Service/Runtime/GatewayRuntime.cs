using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly object LegacyClientsMigrationSync = new();
    private static bool LegacyClientsMigrated;

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
        _localGatewayClients = LoadLocalGatewayClients(_config.LocalGateway.Protocol);
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
            var existingUsernames = _localGatewayClients
                .Select(x => x.Username)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var created = new LocalGatewayClient
            {
                Id = Guid.NewGuid().ToString(),
                Email = normalizedEmail,
                Enabled = true,
                Remark = string.IsNullOrWhiteSpace(remark) ? normalizedEmail : remark.Trim(),
                Protocol = protocol,
                Username = CreateLocalGatewayUsername(normalizedEmail, existingUsernames),
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
            if (string.IsNullOrWhiteSpace(existing.Username))
            {
                var existingUsernames = _localGatewayClients
                    .Where(x => !string.Equals(x.Id, id, StringComparison.Ordinal))
                    .Select(x => x.Username)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                existing.Username = CreateLocalGatewayUsername(existing.Email, existingUsernames);
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

    public bool TryBuildLocalGatewayClientConfig(string clientId, out LocalGatewayClientConfigPayload payload, out string? error)
    {
        lock (_sync)
        {
            var id = (clientId ?? string.Empty).Trim();
            var client = _localGatewayClients.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (client is null)
            {
                payload = LocalGatewayClientConfigPayload.Empty;
                error = "Client not found.";
                return false;
            }

            var host = ResolveLocalGatewayHostLocked();

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
                payload = new LocalGatewayClientConfigPayload
                {
                    Mode = "uri",
                    Uri = $"ss://{userInfo}@{host}:{port}#{Uri.EscapeDataString(display)}",
                    Title = "Shadowsocks Config"
                };
                error = null;
                return true;
            }
            if (string.Equals(protocol, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
            {
                var username = string.IsNullOrWhiteSpace(client.Username)
                    ? CreateLocalGatewayUsername(client.Email, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    : client.Username.Trim();
                if (string.IsNullOrWhiteSpace(username))
                {
                    payload = LocalGatewayClientConfigPayload.Empty;
                    error = "OpenVPN username is missing.";
                    return false;
                }

                if (!File.Exists(ServicePaths.LocalGatewayOpenVpnCaPath))
                {
                    payload = LocalGatewayClientConfigPayload.Empty;
                    error = "OpenVPN CA material was not generated yet. Start local OpenVPN runtime first.";
                    return false;
                }

                if (!File.Exists(ServicePaths.LocalGatewayOpenVpnTlsCryptKeyPath))
                {
                    payload = LocalGatewayClientConfigPayload.Empty;
                    error = "OpenVPN TLS key material was not generated yet. Start local OpenVPN runtime first.";
                    return false;
                }

                var ovpn = BuildOpenVpnClientProfile(host, port);
                var safeStem = ToSafeFileStem(display);
                payload = new LocalGatewayClientConfigPayload
                {
                    Mode = "openvpn_bundle",
                    Title = "OpenVPN Client Bundle",
                    Uri = ovpn.Trim(),
                    Username = username,
                    Password = client.Secret,
                    OvpnFileName = $"{safeStem}-{client.Id[..Math.Min(8, client.Id.Length)]}.ovpn",
                    OvpnContent = ovpn
                };
                error = null;
                return true;
            }

            var query = "type=tcp&security=none&encryption=none";
            payload = new LocalGatewayClientConfigPayload
            {
                Mode = "uri",
                Uri = $"vless://{client.Id}@{host}:{port}?{query}#{Uri.EscapeDataString(display)}",
                Title = "VLESS Config"
            };
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
            var previousProtocol = LocalGatewayProtocols.Normalize(previous.LocalGateway.Protocol);
            _config = CloneConfig(config);
            _config.GatewayType = GatewayTypes.Normalize(_config.GatewayType);
            _config.LocalGateway.Protocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);
            var currentProtocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);
            if (!string.Equals(previousProtocol, currentProtocol, StringComparison.OrdinalIgnoreCase))
            {
                _localGatewayClients = LoadLocalGatewayClients(currentProtocol);
            }
            _status.ProxyListenPort = _config.LocalProxyListenPort;
            _status.GatewayType = _config.GatewayType;
            _status.LocalGatewayProtocol = _config.LocalGateway.Protocol;
            _status.LocalGatewayPort = _config.LocalGateway.Port;
            _status.LocalGatewayClientsCount = _localGatewayClients.Count;
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

            if (RequiresLocalGatewayRestart(previous, _config))
            {
                _localGatewayRestartRequested = true;
                _localGatewayRequestVersion++;
            }

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
            var remoteAddress = (config.RemoteAddress ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(remoteAddress))
            {
                var hostType = Uri.CheckHostName(remoteAddress);
                if (hostType == UriHostNameType.Unknown)
                {
                    error = "Remote address must be empty, a hostname, or an IP address.";
                    return false;
                }
            }

            var previousProtocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);

            _config.GatewayType = GatewayTypes.Local;
            _config.LocalGateway.Protocol = protocol;
            _config.LocalGateway.Port = config.Port;
            _config.LocalGateway.BindAddress = bindAddress;
            _config.LocalGateway.RemoteAddress = remoteAddress;
            _config.LocalGateway.Remark = string.IsNullOrWhiteSpace(config.Remark) ? "OmniRelay Local Gateway" : config.Remark.Trim();
            _config.LocalGateway.RuntimeEnabled = config.RuntimeEnabled;
            if (!string.Equals(previousProtocol, protocol, StringComparison.OrdinalIgnoreCase))
            {
                _localGatewayClients = LoadLocalGatewayClients(protocol);
            }

            _localGatewayRequested = _config.LocalGateway.RuntimeEnabled;
            _localGatewayRestartRequested = true;
            _localGatewayRequestVersion++;
            _status.GatewayType = _config.GatewayType;
            _status.LocalGatewayProtocol = _config.LocalGateway.Protocol;
            _status.LocalGatewayPort = _config.LocalGateway.Port;
            _status.LocalGatewayClientsCount = _localGatewayClients.Count;
            _status.LastStatusUpdateUtc = DateTimeOffset.UtcNow;
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
                RemoteAddress = string.IsNullOrWhiteSpace(config.LocalGateway?.RemoteAddress) ? string.Empty : config.LocalGateway.RemoteAddress.Trim(),
                Remark = string.IsNullOrWhiteSpace(config.LocalGateway?.Remark) ? "OmniRelay Local Gateway" : config.LocalGateway.Remark.Trim(),
                RuntimeEnabled = config.LocalGateway?.RuntimeEnabled ?? true
            }
        };
    }

    private static bool RequiresLocalGatewayRestart(ServiceConfig previous, ServiceConfig current)
    {
        var previousGatewayType = GatewayTypes.Normalize(previous.GatewayType);
        var currentGatewayType = GatewayTypes.Normalize(current.GatewayType);
        if (!string.Equals(previousGatewayType, currentGatewayType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (previous.DefaultAdapterIfIndex != current.DefaultAdapterIfIndex)
        {
            return true;
        }

        var previousLocal = previous.LocalGateway ?? new LocalGatewayConfig();
        var currentLocal = current.LocalGateway ?? new LocalGatewayConfig();
        if (previousLocal.RuntimeEnabled != currentLocal.RuntimeEnabled)
        {
            return true;
        }

        if (!string.Equals(
                LocalGatewayProtocols.Normalize(previousLocal.Protocol),
                LocalGatewayProtocols.Normalize(currentLocal.Protocol),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (previousLocal.Port != currentLocal.Port)
        {
            return true;
        }

        if (!string.Equals(
                (previousLocal.BindAddress ?? string.Empty).Trim(),
                (currentLocal.BindAddress ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(
                (previousLocal.RemoteAddress ?? string.Empty).Trim(),
                (currentLocal.RemoteAddress ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private List<LocalGatewayClient> LoadLocalGatewayClients(string? protocol)
    {
        var normalizedProtocol = LocalGatewayProtocols.Normalize(protocol);
        EnsureLegacyLocalClientsMigrated();
        return LoadLocalGatewayClientsFromPath(ServicePaths.GetLocalGatewayClientsPath(normalizedProtocol), normalizedProtocol);
    }

    private static void EnsureLegacyLocalClientsMigrated()
    {
        lock (LegacyClientsMigrationSync)
        {
            if (LegacyClientsMigrated)
            {
                return;
            }

            LegacyClientsMigrated = true;
            if (!File.Exists(ServicePaths.LocalGatewayClientsPath))
            {
                return;
            }

            try
            {
                var raw = File.ReadAllText(ServicePaths.LocalGatewayClientsPath);
                var legacyClients = JsonSerializer.Deserialize<List<LocalGatewayClient>>(raw, JsonOptions) ?? [];
                var buckets = new Dictionary<string, List<LocalGatewayClient>>(StringComparer.OrdinalIgnoreCase);
                foreach (var client in legacyClients)
                {
                    var protocol = LocalGatewayProtocols.Normalize(client.Protocol);
                    if (!buckets.TryGetValue(protocol, out var list))
                    {
                        list = [];
                        buckets[protocol] = list;
                    }

                    list.Add(client);
                }

                ServicePaths.EnsureDirectories();
                foreach (var bucket in buckets)
                {
                    var path = ServicePaths.GetLocalGatewayClientsPath(bucket.Key);
                    if (File.Exists(path))
                    {
                        continue;
                    }

                    var migratedRaw = JsonSerializer.Serialize(bucket.Value, JsonOptions);
                    File.WriteAllText(path, migratedRaw);
                }

                var backupPath = $"{ServicePaths.LocalGatewayClientsPath}.migrated";
                if (!File.Exists(backupPath))
                {
                    File.Move(ServicePaths.LocalGatewayClientsPath, backupPath);
                }
                else
                {
                    File.Delete(ServicePaths.LocalGatewayClientsPath);
                }
            }
            catch
            {
                // Leave legacy file untouched; runtime can continue with current protocol defaults.
            }
        }
    }

    private List<LocalGatewayClient> LoadLocalGatewayClientsFromPath(string path, string normalizedProtocol)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var raw = File.ReadAllText(path);
            var clients = JsonSerializer.Deserialize<List<LocalGatewayClient>>(raw, JsonOptions) ?? [];
            var normalized = new List<LocalGatewayClient>(clients.Count);
            var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    Protocol = normalizedProtocol,
                    Username = CreateLocalGatewayUsername(client.Username, email, usernames),
                    Secret = string.IsNullOrWhiteSpace(client.Secret) ? CreateLocalGatewaySecret(normalizedProtocol) : client.Secret.Trim(),
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
            var protocol = LocalGatewayProtocols.Normalize(_config.LocalGateway.Protocol);
            foreach (var client in _localGatewayClients)
            {
                client.Protocol = protocol;
            }

            var raw = JsonSerializer.Serialize(_localGatewayClients, JsonOptions);
            File.WriteAllText(ServicePaths.GetLocalGatewayClientsPath(protocol), raw);
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
            Username = client.Username,
            Secret = client.Secret,
            CreatedAtUtc = client.CreatedAtUtc
        };
    }

    private static string CreateLocalGatewaySecret(string protocol)
    {
        var normalized = LocalGatewayProtocols.Normalize(protocol);
        if (string.Equals(normalized, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToHexString(Guid.NewGuid().ToByteArray());
        }
        if (string.Equals(normalized, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
        {
            return CreateRandomAlphaNum(24);
        }

        return Guid.NewGuid().ToString();
    }

    private string ResolveLocalGatewayHostLocked()
    {
        var host = (_config.LocalGateway.RemoteAddress ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(host))
        {
            return host;
        }

        host = (_status.WhitelistAdapterIp ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            host = (_status.DefaultAdapterIp ?? string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            host = "127.0.0.1";
        }

        return host;
    }

    private static string BuildOpenVpnClientProfile(string host, int port)
    {
        var caPem = File.ReadAllText(ServicePaths.LocalGatewayOpenVpnCaPath).Trim();
        var tlsCrypt = File.ReadAllText(ServicePaths.LocalGatewayOpenVpnTlsCryptKeyPath).Trim();
        var sb = new StringBuilder(2048);
        sb.AppendLine("client");
        sb.AppendLine("dev tun");
        sb.AppendLine("proto tcp-client");
        sb.AppendLine($"remote {host} {port}");
        sb.AppendLine("nobind");
        sb.AppendLine("persist-key");
        sb.AppendLine("persist-tun");
        sb.AppendLine("remote-cert-tls server");
        sb.AppendLine("setenv CLIENT_CERT 0");
        sb.AppendLine("auth-user-pass");
        sb.AppendLine("auth-nocache");
        sb.AppendLine("auth SHA256");
        sb.AppendLine("cipher AES-256-GCM");
        sb.AppendLine("data-ciphers AES-256-GCM:AES-128-GCM");
        sb.AppendLine("data-ciphers-fallback AES-256-GCM");
        sb.AppendLine("verb 3");
        sb.AppendLine("<ca>");
        sb.AppendLine(caPem);
        sb.AppendLine("</ca>");
        sb.AppendLine("<tls-crypt>");
        sb.AppendLine(tlsCrypt);
        sb.AppendLine("</tls-crypt>");
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string CreateLocalGatewayUsername(string preferred, string email, ISet<string> existingUsernames)
    {
        var normalizedPreferred = NormalizeUsernameSeed(preferred);
        if (!string.IsNullOrWhiteSpace(normalizedPreferred) && existingUsernames.Add(normalizedPreferred))
        {
            return normalizedPreferred;
        }

        var baseName = "ovpn_" + NormalizeUsernameSeed(email);
        if (string.IsNullOrWhiteSpace(baseName) || string.Equals(baseName, "ovpn_", StringComparison.Ordinal))
        {
            baseName = "ovpn_client";
        }

        if (existingUsernames.Add(baseName))
        {
            return baseName;
        }

        for (var i = 0; i < 100; i++)
        {
            var candidate = $"{baseName}{CreateRandomAlphaNum(4).ToLowerInvariant()}";
            if (existingUsernames.Add(candidate))
            {
                return candidate;
            }
        }

        var fallback = $"ovpn_{CreateRandomAlphaNum(10).ToLowerInvariant()}";
        existingUsernames.Add(fallback);
        return fallback;
    }

    private static string CreateLocalGatewayUsername(string email, ISet<string> existingUsernames)
    {
        return CreateLocalGatewayUsername(string.Empty, email, existingUsernames);
    }

    private static string NormalizeUsernameSeed(string? seed)
    {
        var raw = (seed ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var chars = raw.Where(char.IsAsciiLetterOrDigit).Take(18).ToArray();
        return new string(chars);
    }

    private static string CreateRandomAlphaNum(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(Math.Max(1, length));
        var chars = new char[Math.Max(1, length)];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }

    private static string ToSafeFileStem(string value)
    {
        var safe = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
            .Where(ch => ch != '\0')
            .ToArray())
            .Trim('-');
        if (string.IsNullOrWhiteSpace(safe))
        {
            return "openvpn-client";
        }

        return safe.Length <= 40 ? safe : safe[..40];
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
