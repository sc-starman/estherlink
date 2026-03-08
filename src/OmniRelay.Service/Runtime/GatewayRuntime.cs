using System.Net;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Licensing;
using OmniRelay.Core.Policy;
using OmniRelay.Core.Status;

namespace OmniRelay.Service.Runtime;

public sealed class GatewayRuntime
{
    private readonly object _sync = new();
    private readonly ConfigStore _configStore;
    private readonly FileLogWriter _log;

    private ServiceConfig _config;
    private IReadOnlyList<string> _whitelistEntries;
    private WhitelistSet _whitelist;
    private bool _proxyRequested;
    private bool _licenseTransferRequested;
    private GatewayStatus _status;
    private long _tunnelRestartRequestVersion;

    public GatewayRuntime(ConfigStore configStore, FileLogWriter log)
    {
        _configStore = configStore;
        _log = log;

        var persisted = _configStore.Load();
        _config = CloneConfig(persisted.Config);
        _whitelistEntries = persisted.WhitelistEntries.ToList();

        if (!WhitelistSet.TryCreate(_whitelistEntries, out _whitelist, out var errors))
        {
            _whitelist = WhitelistSet.Empty;
            _whitelistEntries = [];
            _log.Warn($"Invalid persisted whitelist ignored: {string.Join("; ", errors)}");
        }

        _status = new GatewayStatus
        {
            ServiceRunning = true,
            ProxyRunning = false,
            ProxyListenPort = _config.LocalProxyListenPort,
            WhitelistCount = _whitelist.Rules.Count
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

    public WhitelistSet GetWhitelistSnapshot()
    {
        lock (_sync)
        {
            return _whitelist;
        }
    }

    public IReadOnlyList<string> GetWhitelistEntriesSnapshot()
    {
        lock (_sync)
        {
            return _whitelistEntries.ToList();
        }
    }

    public void SetConfig(ServiceConfig config)
    {
        lock (_sync)
        {
            var previous = CloneConfig(_config);
            _config = CloneConfig(config);
            _status.ProxyListenPort = _config.LocalProxyListenPort;

            if (RequiresTunnelRestart(previous, _config, out var changeSummary))
            {
                RequestTunnelRestartLocked($"Tunnel restart requested after config change: {changeSummary}.");
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
        var normalized = entries
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (!WhitelistSet.TryCreate(normalized, out var parsed, out var errors))
        {
            error = string.Join("; ", errors);
            return false;
        }

        lock (_sync)
        {
            _whitelist = parsed;
            _whitelistEntries = normalized;
            _status.WhitelistCount = parsed.Rules.Count;
            PersistLocked();
        }

        error = null;
        return true;
    }

    public bool ShouldUseWhitelistAdapter(IPAddress? destinationAddress)
    {
        lock (_sync)
        {
            return _whitelist.MatchesDestination(destinationAddress);
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
        }
    }

    public void SetAdapterIps(string? whitelistAdapterIp, string? defaultAdapterIp)
    {
        lock (_sync)
        {
            _status.WhitelistAdapterIp = whitelistAdapterIp;
            _status.DefaultAdapterIp = defaultAdapterIp;
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
        }
    }

    public void SetBootstrapSocksStatus(bool listening, bool remoteForwardActive, string? error)
    {
        lock (_sync)
        {
            _status.BootstrapSocksListening = listening;
            _status.BootstrapSocksRemoteForwardActive = remoteForwardActive;
            _status.BootstrapSocksLastError = error;
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
        }
    }

    public void SetError(string? message)
    {
        lock (_sync)
        {
            _status.LastError = message;
        }
    }

    public GatewayStatus GetStatusSnapshot()
    {
        lock (_sync)
        {
            return new GatewayStatus
            {
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
                LastError = _status.LastError
            };
        }
    }

    private void PersistLocked()
    {
        _configStore.Save(_config, _whitelistEntries);
    }

    private void RequestTunnelRestartLocked(string reason)
    {
        _tunnelRestartRequestVersion++;
        _status.TunnelConnected = false;
        _status.BootstrapSocksRemoteForwardActive = false;
        _status.TunnelLastError = reason;
        _status.BootstrapSocksLastError = reason;
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
            LicenseKey = config.LicenseKey
        };
    }
}
