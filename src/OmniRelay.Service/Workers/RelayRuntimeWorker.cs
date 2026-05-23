using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;
using OmniRelay.Core.Status;
using OmniRelay.Service.Runtime;

namespace OmniRelay.Service.Workers;

public sealed class RelayRuntimeWorker : BackgroundService
{
    private static readonly TimeSpan LoopDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LicenseCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LocalProbeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EndToEndProbeInterval = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan UnestablishedGracePeriod = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RemoteProbeTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan TunnelFlapWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TunnelRecentExitPenalty = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SchedulerSkewClamp = TimeSpan.FromMinutes(2);

    private const int Tier1FailureThreshold = 1;
    private const int Tier2FailureThreshold = 3;
    private const int Tier3FailureThreshold = 6;
    private const int TunnelFlapThreshold = 3;

    private readonly GatewayRuntime _runtime;
    private readonly LicenseValidator _licenseValidator;
    private readonly FileLogWriter _log;
    private readonly ILogger<RelayRuntimeWorker> _logger;
    private readonly Dictionary<string, RelaySession> _sessions = new(StringComparer.Ordinal);
    private DateTimeOffset _nextLicenseCheckUtc = DateTimeOffset.MinValue;

    public RelayRuntimeWorker(
        GatewayRuntime runtime,
        LicenseValidator licenseValidator,
        FileLogWriter log,
        ILogger<RelayRuntimeWorker> logger)
    {
        _runtime = runtime;
        _licenseValidator = licenseValidator;
        _log = log;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureLicenseAsync(stoppingToken);
                var config = _runtime.GetConfigSnapshot();
                var activeIds = config.Relays.Where(x => x.Enabled).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

                foreach (var stale in _sessions.Keys.Where(x => !activeIds.Contains(x)).ToArray())
                {
                    await StopSessionAsync(stale, "Relay disabled or deleted.");
                }

                var license = _runtime.GetStatusSnapshot();
                if (license.LicenseCheckedAtUtc is not null && !license.LicenseValid)
                {
                    foreach (var id in _sessions.Keys.ToArray())
                    {
                        await StopSessionAsync(id, license.LicenseReason ?? "License invalid.");
                    }

                    await Task.Delay(LoopDelay, stoppingToken);
                    continue;
                }

                foreach (var relay in config.Relays.Where(x => x.Enabled))
                {
                    await EnsureSessionAsync(relay, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relay runtime worker failure.");
                _log.Error("Relay runtime worker failure.", ex);
            }

            await Task.Delay(LoopDelay, stoppingToken);
        }

        foreach (var id in _sessions.Keys.ToArray())
        {
            await StopSessionAsync(id, "Relay worker stopped.");
        }
    }

    private async Task EnsureLicenseAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextLicenseCheckUtc)
        {
            return;
        }

        var config = _runtime.GetConfigSnapshot();
        var result = await _licenseValidator.ValidateAsync(
            config,
            forceOnline: false,
            transferRequested: false,
            cancellationToken: cancellationToken);
        _runtime.SetLicenseStatus(result);
        _nextLicenseCheckUtc = DateTimeOffset.UtcNow.Add(LicenseCheckInterval);
    }

    private async Task EnsureSessionAsync(RelayConfig relay, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(relay.Id, out var session))
        {
            session = new RelaySession(relay, _runtime, _log);
            _sessions[relay.Id] = session;
            await session.StartAsync(cancellationToken);
            return;
        }

        if (!session.Matches(relay))
        {
            await StopSessionAsync(relay.Id, "Relay configuration changed.");
            session = new RelaySession(relay, _runtime, _log);
            _sessions[relay.Id] = session;
            await session.StartAsync(cancellationToken);
            return;
        }

        await session.PulseAsync(cancellationToken);
    }

    private async Task StopSessionAsync(string relayId, string reason)
    {
        if (!_sessions.Remove(relayId, out var session))
        {
            return;
        }

        await session.StopAsync(reason);
    }

    private sealed class RelaySession
    {
        private readonly RelayConfig _relay;
        private readonly GatewayRuntime _runtime;
        private readonly FileLogWriter _log;
        private readonly RelaySocks5ProxyEngine _dataPlane;
        private readonly RelayBootstrapSocksEngine _bootstrap;
        private readonly Queue<string> _events = new();
        private readonly Queue<DateTimeOffset> _recentTunnelExitTimestamps = new();
        private Process? _sshProcess;
        private readonly SemaphoreSlim _tunnelLifecycleLock = new(1, 1);
        private Mutex? _remoteTunnelMutex;
        private bool _remoteTunnelMutexHeld;
        private Process? _localGatewayProcess;
        private Process? _omniPanelProcess;
        private DateTimeOffset? _processStartedAtUtc;
        private DateTimeOffset? _lastConnectedAtUtc;
        private DateTimeOffset? _lastLocalProbeUtc;
        private DateTimeOffset? _lastEndToEndProbeUtc;
        private DateTimeOffset? _lastHealthyUtc;
        private DateTimeOffset? _lastTunnelExitAtUtc;
        private DateTimeOffset _nextLocalProbeAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _nextEndToEndProbeAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _nextRecoveryAllowedAtUtc = DateTimeOffset.MinValue;
        private int _reconnectCount;
        private int _forwardConflictCount;
        private int _consecutiveFailures;
        private int _currentRecoveryTier;
        private bool _localProbeOk;
        private bool _endToEndProbeOk;
        private bool _remoteProbeModuleAvailable = true;
        private bool _remoteProbeMissingLogged;
        private bool _bootstrapSocksListening;
        private bool _tunnelConnected;
        private bool _stopping;
        private string _localGatewayState = "inactive";
        private string? _localGatewayHealthReason;
        private string? _localGatewayRuntimeFingerprint;
        private string? _localOpenVpnStaticFingerprint;
        private string _omniPanelState = "inactive";
        private string? _omniPanelLastError;
        private string? _omniPanelRuntimeFingerprint;
        private string _tunnelState = "Disconnected";
        private string _healthState = "Disconnected";
        private string? _healthReasonCode;
        private string? _recoveryAction;
        private string? _lastTunnelError;
        private string? _lastBootstrapError;
        private string? _lastLoggedProbeFailureSignature;
        private DateTimeOffset? _lastLoggedProbeFailureUtc;
        private string? _activeTunnelConnectionId;
        private DateTimeOffset _nextTunnelctlCompatCheckUtc = DateTimeOffset.MinValue;
        private bool _tunnelctlCompatVerified;

        public RelaySession(RelayConfig relay, GatewayRuntime runtime, FileLogWriter log)
        {
            _relay = Clone(relay);
            _runtime = runtime;
            _log = log;
            _dataPlane = new RelaySocks5ProxyEngine(
                _relay,
                runtime,
                log);
            _bootstrap = new RelayBootstrapSocksEngine(_relay, log);
        }

        public bool Matches(RelayConfig relay)
        {
            return relay.DataPlaneLocalPort == _relay.DataPlaneLocalPort &&
                   relay.BootstrapSocksLocalPort == _relay.BootstrapSocksLocalPort &&
                   relay.BootstrapSocksRemotePort == _relay.BootstrapSocksRemotePort &&
                   string.Equals(relay.IncomingAdapterId, _relay.IncomingAdapterId, StringComparison.OrdinalIgnoreCase) &&
                   relay.IncomingAdapterIfIndex == _relay.IncomingAdapterIfIndex &&
                   string.Equals(relay.OutgoingAdapterId, _relay.OutgoingAdapterId, StringComparison.OrdinalIgnoreCase) &&
                   relay.OutgoingAdapterIfIndex == _relay.OutgoingAdapterIfIndex &&
                   string.Equals(GatewayTypes.Normalize(relay.GatewayType), GatewayTypes.Normalize(_relay.GatewayType), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(relay.RemoteGateway?.TunnelHost, _relay.RemoteGateway.TunnelHost, StringComparison.OrdinalIgnoreCase) &&
                   relay.RemoteGateway?.TunnelSshPort == _relay.RemoteGateway.TunnelSshPort &&
                   relay.RemoteGateway?.TunnelRemotePort == _relay.RemoteGateway.TunnelRemotePort &&
                   string.Equals(relay.RemoteGateway?.TunnelUser, _relay.RemoteGateway.TunnelUser, StringComparison.Ordinal) &&
                   string.Equals(TunnelAuthMethods.Normalize(relay.RemoteGateway?.TunnelAuthMethod), TunnelAuthMethods.Normalize(_relay.RemoteGateway.TunnelAuthMethod), StringComparison.Ordinal) &&
                   string.Equals(relay.RemoteGateway?.TunnelPrivateKeyPath, _relay.RemoteGateway.TunnelPrivateKeyPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(relay.RemoteGateway?.TunnelPrivateKeyPassphrase, _relay.RemoteGateway.TunnelPrivateKeyPassphrase, StringComparison.Ordinal) &&
                   string.Equals(relay.RemoteGateway?.TunnelPassword, _relay.RemoteGateway.TunnelPassword, StringComparison.Ordinal) &&
                   string.Equals(relay.RemoteGateway?.OpenVpnNetwork, _relay.RemoteGateway.OpenVpnNetwork, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(relay.RemoteGateway?.OpenVpnSharedCaCertPath, _relay.RemoteGateway.OpenVpnSharedCaCertPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(relay.RemoteGateway?.OpenVpnSharedClientCertPath, _relay.RemoteGateway.OpenVpnSharedClientCertPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(relay.RemoteGateway?.OpenVpnSharedClientKeyPath, _relay.RemoteGateway.OpenVpnSharedClientKeyPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(relay.RemoteGateway?.OpenVpnSharedTlsCryptKeyPath, _relay.RemoteGateway.OpenVpnSharedTlsCryptKeyPath, StringComparison.OrdinalIgnoreCase) &&
                   (relay.OmniPanel?.Port ?? 2054) == (_relay.OmniPanel?.Port ?? 2054) &&
                   string.Equals(relay.OmniPanel?.Username ?? string.Empty, _relay.OmniPanel?.Username ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(relay.OmniPanel?.Password ?? string.Empty, _relay.OmniPanel?.Password ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(relay.OmniPanel?.Domain ?? string.Empty, _relay.OmniPanel?.Domain ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _dataPlane.StartAsync(cancellationToken);
                await _bootstrap.StartAsync(cancellationToken);
                await PulseAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _lastTunnelError = ex.Message;
                _healthReasonCode = "relay_runtime_start_failed";
                _tunnelState = "Failed";
                _healthState = "Unhealthy";
                PublishStatus();
                _log.Error($"Relay '{_relay.Name}' failed to start.", ex);
            }
        }

        public async Task PulseAsync(CancellationToken cancellationToken)
        {
            var config = ToServiceConfig(_relay);
            UpdateAdapterStatus();
            var now = DateTimeOffset.UtcNow;
            NormalizeScheduledTimes(now);

            if (!string.Equals(GatewayTypes.Normalize(_relay.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
            {
                await StopTunnelProcessAsync();
                await EnsureLocalGatewayRuntimeAsync(cancellationToken);
                await EnsureLocalOmniPanelRuntimeAsync(cancellationToken);
                _consecutiveFailures = 0;
                _currentRecoveryTier = 0;
                _recoveryAction = null;
                _tunnelConnected = false;
                _bootstrapSocksListening = _bootstrap.Running;
                _localProbeOk = true;
                _endToEndProbeOk = true;
                _tunnelState = "LocalMode";
                _healthState = string.Equals(_localGatewayState, "active", StringComparison.OrdinalIgnoreCase) ? "Healthy" : "Unhealthy";
                _healthReasonCode = _localGatewayHealthReason;
                _lastTunnelError = null;
                _lastBootstrapError = null;
                if (string.Equals(_localGatewayState, "active", StringComparison.OrdinalIgnoreCase))
                {
                    _lastHealthyUtc = DateTimeOffset.UtcNow;
                }
                PublishStatus();
                return;
            }

            await StopLocalGatewayRuntimeAsync();
            await StopLocalOmniPanelRuntimeAsync();
            _localGatewayState = "inactive";
            _localGatewayHealthReason = null;
            _omniPanelState = "configured";
            _omniPanelLastError = null;

            if (_sshProcess is null || _sshProcess.HasExited)
            {
                if (DateTimeOffset.UtcNow >= _nextRecoveryAllowedAtUtc)
                {
                    await StartTunnelProcessAsync(config, cancellationToken);
                }
            }

            var probeCycleExecuted = false;
            if (now >= _nextLocalProbeAtUtc)
            {
                _nextLocalProbeAtUtc = now.Add(LocalProbeInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(50, 450)));
                probeCycleExecuted = true;
                await RunLocalProbeAsync(config, cancellationToken);
            }

            if (now >= _nextEndToEndProbeAtUtc)
            {
                _nextEndToEndProbeAtUtc = now.Add(EndToEndProbeInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(75, 700)));
                probeCycleExecuted = true;
                if (_localProbeOk)
                {
                    await RunEndToEndProbeAsync(config, cancellationToken);
                }
                else
                {
                    _endToEndProbeOk = false;
                    _lastEndToEndProbeUtc = DateTimeOffset.UtcNow;
                    if (string.IsNullOrWhiteSpace(_healthReasonCode))
                    {
                        _healthReasonCode = "local_probe_failed";
                    }
                }
            }

            var overallHealthy = _localProbeOk && _endToEndProbeOk && _tunnelConnected;
            if (overallHealthy && IsTunnelFlapping())
            {
                overallHealthy = false;
                _healthReasonCode = "ssh_tunnel_flapping";
                _lastTunnelError ??= "SSH tunnel session is unstable (frequent exits).";
                _lastBootstrapError = "local_probe_failed:ssh_tunnel_flapping";
            }

            if (probeCycleExecuted)
            {
                if (overallHealthy)
                {
                    if (!string.Equals(_healthState, "Healthy", StringComparison.Ordinal))
                    {
                        RecordEvent("info", $"relay '{_relay.Name}' dual probe health restored");
                    }

                    _consecutiveFailures = 0;
                    _currentRecoveryTier = 0;
                    _recoveryAction = null;
                    _healthState = "Healthy";
                    _tunnelState = "Healthy";
                    _healthReasonCode = null;
                    _lastHealthyUtc = DateTimeOffset.UtcNow;
                    _lastTunnelError = null;
                    _lastBootstrapError = null;
                }
                else
                {
                    if (IsStartupGraceActive())
                    {
                        _healthState = "Degraded";
                        _tunnelState = "Connecting";
                        _recoveryAction = null;
                        _currentRecoveryTier = 0;
                        if (string.IsNullOrWhiteSpace(_healthReasonCode))
                        {
                            _healthReasonCode = "ssh_session_not_established";
                        }

                        PublishStatus();
                        return;
                    }

                    _consecutiveFailures++;
                    _healthState = "Degraded";
                    if (!string.Equals(_tunnelState, "RecoveringTier1", StringComparison.Ordinal) &&
                        !string.Equals(_tunnelState, "RecoveringTier2", StringComparison.Ordinal) &&
                        !string.Equals(_tunnelState, "RecoveringTier3", StringComparison.Ordinal))
                    {
                        _tunnelState = "Degraded";
                    }

                    await TryRecoverAsync(config, cancellationToken);
                }
            }

            PublishStatus();
        }

        public async Task StopAsync(string reason)
        {
            _stopping = true;
            await StopTunnelProcessAsync();
            await StopLocalGatewayRuntimeAsync();
            await StopLocalOmniPanelRuntimeAsync();
            await _dataPlane.StopAsync();
            await _bootstrap.StopAsync();
            _tunnelState = "Stopped";
            _healthState = "Disconnected";
            _healthReasonCode = "relay_stopped";
            _lastTunnelError = reason;
            _lastBootstrapError = reason;
            _tunnelConnected = false;
            _bootstrapSocksListening = false;
            PublishStatus(enabled: false);
        }

        private void PublishStatus(bool enabled = true)
        {
            if (_stopping && enabled)
            {
                return;
            }

            _bootstrapSocksListening = _bootstrap.Running;
            _runtime.SetRelayRuntimeStatus(BuildStatus(enabled));
        }

        private bool IsStartupGraceActive()
        {
            if (_sshProcess is null || _sshProcess.HasExited || _tunnelConnected || !_processStartedAtUtc.HasValue)
            {
                return false;
            }

            return DateTimeOffset.UtcNow - _processStartedAtUtc.Value < UnestablishedGracePeriod;
        }

        private async Task RunLocalProbeAsync(ServiceConfig config, CancellationToken cancellationToken)
        {
            _lastLocalProbeUtc = DateTimeOffset.UtcNow;
            _tunnelConnected = false;

            if (_sshProcess is null || _sshProcess.HasExited)
            {
                _localProbeOk = false;
                if (string.IsNullOrWhiteSpace(_healthReasonCode))
                {
                    _healthReasonCode = "tunnel_process_not_running";
                }

                if (string.IsNullOrWhiteSpace(_lastTunnelError))
                {
                    _lastTunnelError = "Tunnel process is not running.";
                }
                return;
            }

            _tunnelConnected = await HasEstablishedSshSessionAsync(_sshProcess.Id, config, cancellationToken);
            if (_tunnelConnected)
            {
                _lastConnectedAtUtc = DateTimeOffset.UtcNow;
            }
            else if (_processStartedAtUtc.HasValue &&
                     DateTimeOffset.UtcNow - _processStartedAtUtc.Value >= UnestablishedGracePeriod &&
                     DateTimeOffset.UtcNow >= _nextRecoveryAllowedAtUtc)
            {
                _lastTunnelError = "SSH session not established within grace period.";
                RecordEvent("warn", _lastTunnelError);
                await StopTunnelProcessAsync();
            }

            var backendProbe = await ProbeBackendEndpointAsync(config.LocalProxyListenPort, cancellationToken);
            _localProbeOk = _tunnelConnected && backendProbe.Success;
            _bootstrapSocksListening = await IsLoopbackTcpListeningAsync(config.BootstrapSocksLocalPort, cancellationToken);

            if (!_localProbeOk)
            {
                _healthReasonCode = !_tunnelConnected ? "ssh_session_not_established" : backendProbe.ReasonCode;
                _lastTunnelError = !_tunnelConnected
                    ? "SSH tunnel session is not established."
                    : $"Local backend probe failed: {backendProbe.ReasonCode}";
                _lastBootstrapError = $"local_probe_failed:{_healthReasonCode}";
                LogProbeFailure("local_probe", _healthReasonCode ?? "unknown", _lastTunnelError ?? "Local probe failed.");
                return;
            }

            _lastTunnelError = null;
            _lastBootstrapError = null;
        }

        private async Task RunEndToEndProbeAsync(ServiceConfig config, CancellationToken cancellationToken)
        {
            _lastEndToEndProbeUtc = DateTimeOffset.UtcNow;
            var remote = await RunRemoteWatchdogProbeAsync(config, cancellationToken);

            if (string.Equals(remote.ReasonCode, "remote_probe_module_missing", StringComparison.Ordinal))
            {
                _remoteProbeModuleAvailable = false;
                _endToEndProbeOk = true;
                _lastBootstrapError = "end_to_end_probe_skipped:module_missing";
                _lastTunnelError = null;
                if (!_remoteProbeMissingLogged)
                {
                    RecordEvent("warn", "remote tunnelctl module missing; skipping end-to-end probe until installed");
                    _remoteProbeMissingLogged = true;
                }
                return;
            }

            _remoteProbeModuleAvailable = true;
            _remoteProbeMissingLogged = false;
            _endToEndProbeOk = remote.Success;
            if (!remote.Success)
            {
                _healthReasonCode = remote.ReasonCode;
                _lastBootstrapError = $"end_to_end_probe_failed:{remote.ReasonCode}";
                _lastTunnelError = remote.Message;
                LogProbeFailure("end_to_end_probe", remote.ReasonCode, remote.Message);
                return;
            }

            _lastBootstrapError = null;
            _lastTunnelError = null;
        }

        private async Task TryRecoverAsync(ServiceConfig config, CancellationToken cancellationToken)
        {
            if (DateTimeOffset.UtcNow < _nextRecoveryAllowedAtUtc)
            {
                return;
            }

            var tier = ResolveRecoveryTier(_consecutiveFailures);
            _currentRecoveryTier = tier;
            _tunnelState = $"RecoveringTier{tier}";
            _recoveryAction = $"tier{tier}";
            var localPathFailure = !_localProbeOk || !_tunnelConnected;
            var attemptedRecovery = false;

            try
            {
                if (!localPathFailure)
                {
                    var requiresLocalRestart = ShouldRestartLocalTunnelForRemoteFailure(_healthReasonCode);
                    if (tier == 1)
                    {
                        if (requiresLocalRestart && _remoteProbeModuleAvailable && _tunnelConnected)
                        {
                            RecordEvent("warn", $"relay '{_relay.Name}' recovery tier1: remote soft remediation");
                            await RunRemoteWatchdogRemediationAsync(config, "soft", cancellationToken);
                            attemptedRecovery = true;
                        }

                        _tunnelState = "Degraded";
                        _recoveryAction = null;
                        _currentRecoveryTier = 0;
                    }
                    else if (tier == 2)
                    {
                        if (requiresLocalRestart)
                        {
                            if (_remoteProbeModuleAvailable && _tunnelConnected)
                            {
                                await RunRemoteWatchdogRemediationAsync(config, "soft", cancellationToken);
                            }

                            await StopTunnelProcessAsync();
                            attemptedRecovery = true;
                        }
                        else
                        {
                            _tunnelState = "Degraded";
                            _recoveryAction = null;
                            _currentRecoveryTier = 0;
                        }
                    }
                    else
                    {
                        if (_remoteProbeModuleAvailable && _tunnelConnected)
                        {
                            await RunRemoteWatchdogRemediationAsync(config, "hard", cancellationToken);
                            attemptedRecovery = true;
                        }

                        if (requiresLocalRestart)
                        {
                            await StopTunnelProcessAsync();
                            await CleanupOrphanTunnelProcessesAsync(config, cancellationToken);
                            attemptedRecovery = true;
                        }
                    }

                    _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(GetRecoveryCooldown(tier));
                    return;
                }

                if (tier == 1)
                {
                    RecordEvent("warn", $"relay '{_relay.Name}' recovery tier1: restarting tunnel only");
                    await StopTunnelProcessAsync();
                    attemptedRecovery = true;
                }
                else if (tier == 2)
                {
                    RecordEvent("warn", $"relay '{_relay.Name}' recovery tier2: tunnel recycle with stale-ssh cleanup");
                    await StopTunnelProcessAsync();
                    await CleanupOrphanTunnelProcessesAsync(config, cancellationToken);
                    attemptedRecovery = true;
                }
                else
                {
                    RecordEvent("warn", $"relay '{_relay.Name}' recovery tier3: hard local cleanup");
                    if (_remoteProbeModuleAvailable && _tunnelConnected)
                    {
                        await RunRemoteWatchdogRemediationAsync(config, "hard", cancellationToken);
                    }

                    await _dataPlane.StopAsync();
                    await _bootstrap.StopAsync();
                    await StopTunnelProcessAsync();
                    await CleanupOrphanTunnelProcessesAsync(config, cancellationToken);
                    await _dataPlane.StartAsync(cancellationToken);
                    await _bootstrap.StartAsync(cancellationToken);
                    attemptedRecovery = true;
                }
            }
            catch (Exception ex)
            {
                _runtime.SetError(ex.Message);
                _lastTunnelError = ex.Message;
                RecordEvent("error", $"relay '{_relay.Name}' recovery tier{tier} failed: {ex.Message}");
                _log.Error($"Relay '{_relay.Name}' recovery tier{tier} failed.", ex);
            }

            if (attemptedRecovery && !string.Equals(_healthState, "Healthy", StringComparison.Ordinal))
            {
                _tunnelState = "Degraded";
                _recoveryAction = null;
                _currentRecoveryTier = 0;
            }

            _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(GetRecoveryCooldown(tier));
        }

        private async Task StartTunnelProcessAsync(ServiceConfig config, CancellationToken cancellationToken)
        {
            await _tunnelLifecycleLock.WaitAsync(cancellationToken);
            try
            {
                await StopTunnelProcessCoreAsync();
                await CleanupOrphanTunnelProcessesAsync(config, cancellationToken);

                if (string.IsNullOrWhiteSpace(config.TunnelHost))
                {
                    _lastTunnelError = "Tunnel host is not configured.";
                    _healthReasonCode = "tunnel_host_missing";
                    RecordEvent("warn", _lastTunnelError);
                    return;
                }

                var (routeProbeOk, routeProbeError) = await ProbeSshReachabilityRouteOnlyAsync(
                    config.TunnelHost,
                    config.TunnelSshPort,
                    cancellationToken);

                if (!routeProbeOk)
                {
                    _lastTunnelError = $"Route-only probe cannot reach {config.TunnelHost}:{config.TunnelSshPort}. {routeProbeError}";
                    _healthReasonCode = "ssh_reachability_failed";
                    RecordEvent("warn", _lastTunnelError);
                    _log.Warn(_lastTunnelError);
                    return;
                }

                var connectionId = Guid.NewGuid().ToString("N")[..12];
                _activeTunnelConnectionId = connectionId;
                _log.Info($"Starting relay '{_relay.Name}' tunnel target={config.TunnelHost}:{config.TunnelSshPort}. connectionId={connectionId} remotePorts={config.TunnelRemotePort}/{config.BootstrapSocksRemotePort}");

            if (!SshTunnelProcessFactory.TryCreateReverseTunnelStartInfo(
                    config,
                    out var processInfo,
                    out var error) || processInfo is null)
            {
                _lastTunnelError = error ?? "Tunnel configuration is invalid.";
                _healthReasonCode = "ssh_start_info_invalid";
                RecordEvent("error", _lastTunnelError);
                return;
            }

            if (!TryAcquireRemoteTunnelMutex(config))
            {
                _lastTunnelError = "Another OmniRelay instance is already owning this relay tunnel slot.";
                _healthReasonCode = "tunnel_slot_in_use";
                _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(30));
                RecordEvent("warn", _lastTunnelError);
                _log.Warn($"Relay '{_relay.Name}' tunnel slot is already owned by another process. Backing off for 30s. connectionId={connectionId}. {GetTunnelOwnerDiagnostic(config)}");
                return;
            }

            _sshProcess = Process.Start(processInfo);
            if (_sshProcess is null)
            {
                ReleaseRemoteTunnelMutex();
                _lastTunnelError = "Failed to start ssh process.";
                _healthReasonCode = "ssh_process_start_failed";
                RecordEvent("error", _lastTunnelError);
                    return;
                }

                _sshProcess.StandardInput.Close();
                _processStartedAtUtc = DateTimeOffset.UtcNow;
                _reconnectCount++;
                _forwardConflictCount = 0;
                _lastTunnelError = null;
                _healthReasonCode = null;
                _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow;
                RecordEvent("info", $"relay '{_relay.Name}' tunnel process started pid={_sshProcess.Id} reconnectCount={_reconnectCount} connectionId={connectionId}");
                _log.Info($"Relay '{_relay.Name}' tunnel process started. pid={_sshProcess.Id} reconnectCount={_reconnectCount} connectionId={connectionId}");

                var processRef = _sshProcess;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stderr = await processRef.StandardError.ReadToEndAsync(CancellationToken.None);
                        var stdout = await processRef.StandardOutput.ReadToEndAsync(CancellationToken.None);
                        await processRef.WaitForExitAsync(CancellationToken.None);

                        var exitCode = processRef.ExitCode;
                        var cleaned = string.IsNullOrWhiteSpace(stderr) ? string.Empty : stderr.Trim();
                        if (exitCode != 0)
                        {
                            RegisterTunnelExit();
                        }

                        if (!string.IsNullOrWhiteSpace(cleaned))
                        {
                            _lastTunnelError = cleaned;
                            _healthReasonCode = "ssh_process_exited_with_error";
                            _log.Warn($"Relay '{_relay.Name}' tunnel process exited. connectionId={connectionId} exitCode={exitCode} error={cleaned}");
                            RecordEvent("warn", $"ssh process exited: connectionId={connectionId} {cleaned}");

                            if (TryGetRemoteForwardConflictPort(cleaned, out var conflictPort))
                            {
                                var portText = conflictPort > 0 ? conflictPort.ToString() : config.TunnelRemotePort.ToString();
                                _forwardConflictCount++;
                                var cooldown = GetForwardConflictCooldown(_forwardConflictCount);
                                _healthReasonCode = "remote_forward_port_in_use";
                                _lastTunnelError =
                                    $"Gateway remote-forward port {portText} is already in use by another SSH session/relay. " +
                                    "Stop the other relay/session or use a different Tunnel Remote Port.";
                                _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(cooldown);
                                RecordEvent("warn", $"{_lastTunnelError} conflictAttempt={_forwardConflictCount} nextRetryIn={cooldown.TotalSeconds:0}s");
                                await TryScheduleRemoteForwardCleanupAsync(config, CancellationToken.None, connectionId);
                            }

                            if (HasRemoteForwardFailure(cleaned) && !TryGetRemoteForwardConflictPort(cleaned, out _))
                            {
                                await TryScheduleRemoteForwardCleanupAsync(config, CancellationToken.None, connectionId);
                            }
                        }
                        else if (exitCode == -1)
                        {
                            _lastTunnelError = "ssh exited with code -1.";
                            _healthReasonCode = "ssh_process_exited";
                            _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(15));
                            _log.Warn($"Relay '{_relay.Name}' tunnel process exited. connectionId={connectionId} exitCode=-1");
                            RecordEvent("warn", _lastTunnelError);
                        }
                        else if (exitCode != 0)
                        {
                            var message = string.IsNullOrWhiteSpace(stdout)
                                ? $"ssh exited with code {exitCode}."
                                : $"ssh exited with code {exitCode}: {stdout.Trim()}";
                            _lastTunnelError = message;
                            _healthReasonCode = "ssh_process_exited";
                            _log.Warn($"Relay '{_relay.Name}' tunnel process exited. connectionId={connectionId} {message}");
                            RecordEvent("warn", message);
                        }

                        _tunnelConnected = false;
                        _localProbeOk = false;
                        _endToEndProbeOk = false;
                        if (!string.Equals(_healthState, "Healthy", StringComparison.Ordinal))
                        {
                            _healthState = "Degraded";
                        }

                        if (!string.Equals(_tunnelState, "Stopped", StringComparison.Ordinal))
                        {
                            _tunnelState = "Disconnected";
                    }

                    PublishStatus();
                    if (string.Equals(_activeTunnelConnectionId, connectionId, StringComparison.Ordinal))
                    {
                        _activeTunnelConnectionId = null;
                    }
                    ReleaseRemoteTunnelMutex();
                }
                catch
                {
                    if (string.Equals(_activeTunnelConnectionId, connectionId, StringComparison.Ordinal))
                    {
                        _activeTunnelConnectionId = null;
                    }
                    ReleaseRemoteTunnelMutex();
                }
            });
            }
            finally
            {
                _tunnelLifecycleLock.Release();
            }
        }

        private async Task StopTunnelProcessAsync()
        {
            await _tunnelLifecycleLock.WaitAsync();
            try
            {
                await StopTunnelProcessCoreAsync();
            }
            finally
            {
                _tunnelLifecycleLock.Release();
            }
        }

        private async Task StopTunnelProcessCoreAsync()
        {
            if (_sshProcess is null)
            {
                return;
            }

            try
            {
                if (!_sshProcess.HasExited)
                {
                    _sshProcess.Kill(entireProcessTree: true);
                    await _sshProcess.WaitForExitAsync();
                }
            }
            catch
            {
            }
            finally
            {
                _sshProcess.Dispose();
                _sshProcess = null;
                _processStartedAtUtc = null;
                _tunnelConnected = false;
                _activeTunnelConnectionId = null;
                ReleaseRemoteTunnelMutex();
            }
        }

        private bool TryAcquireRemoteTunnelMutex(ServiceConfig config)
        {
            var name = BuildRemoteTunnelMutexName(config);
            try
            {
                _remoteTunnelMutex ??= new Mutex(false, name);
                if (_remoteTunnelMutexHeld)
                {
                    return true;
                }

                if (!_remoteTunnelMutex.WaitOne(0))
                {
                    return false;
                }

                _remoteTunnelMutexHeld = true;
                return true;
            }
            catch (AbandonedMutexException)
            {
                _remoteTunnelMutexHeld = true;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private void ReleaseRemoteTunnelMutex()
        {
            if (!_remoteTunnelMutexHeld || _remoteTunnelMutex is null)
            {
                return;
            }

            try
            {
                _remoteTunnelMutex.ReleaseMutex();
            }
            catch
            {
            }
            finally
            {
                _remoteTunnelMutexHeld = false;
            }
        }

        private static string BuildRemoteTunnelMutexName(ServiceConfig config)
        {
            var key = $"{(config.TunnelHost ?? string.Empty).Trim().ToLowerInvariant()}|" +
                      $"{(config.TunnelUser ?? string.Empty).Trim().ToLowerInvariant()}|" +
                      $"{config.TunnelSshPort}|{config.TunnelRemotePort}|{config.BootstrapSocksRemotePort}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var token = Convert.ToHexString(bytes[..12]);
            return $"Global\\OmniRelay.RemoteTunnel.{token}";
        }

        private async Task<(bool Success, string Protocol, string ReasonCode)> ProbeBackendEndpointAsync(int port, CancellationToken cancellationToken)
        {
            if (!await IsLoopbackTcpListeningAsync(port, cancellationToken))
            {
                return (false, "unknown", "backend_listener_down");
            }

            if (await ProbeSocks5EndpointAsync(port, cancellationToken))
            {
                return (true, "socks5", string.Empty);
            }

            return (false, "unknown", "backend_protocol_not_socks5");
        }

        private static async Task<bool> ProbeSocks5EndpointAsync(int port, CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token);
                await using var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, timeoutCts.Token);

                var reply = new byte[2];
                await ReadExactAsync(stream, reply, timeoutCts.Token);
                return reply[0] == 0x05 && reply[1] == 0x00;
            }
            catch
            {
                return false;
            }
        }

        private async Task<(bool Success, string ReasonCode, string Message)> RunRemoteWatchdogProbeAsync(
            ServiceConfig config,
            CancellationToken cancellationToken)
        {
            await EnsureTunnelctlCompatibilityAsync(config, cancellationToken);
            var probeCommand = BuildTunnelctlRemoteCommand(config, "probe --json");
            _log.Info($"Relay '{_relay.Name}' effective probe target: backend=127.0.0.1:{config.TunnelRemotePort} connectionId={_activeTunnelConnectionId ?? "none"}");
            var (ok, stdout, stderr, error) = await ExecuteRemoteGatewayctlCommandAsync(
                config,
                probeCommand,
                RemoteProbeTimeout,
                cancellationToken);
            if (!ok)
            {
                var raw = FirstNonEmpty(stderr, stdout, error) ?? "Remote probe failed.";
                if (raw.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "remote_probe_timeout", raw);
                }

                if (raw.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "remote_probe_module_missing", raw);
                }

                return (false, "remote_probe_ssh_failed", raw);
            }

            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                var result = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind is JsonValueKind.True;
                if (result)
                {
                    return (true, string.Empty, "ok");
                }

                var reason = root.TryGetProperty("reasonCode", out var reasonProp)
                    ? reasonProp.GetString()
                    : root.TryGetProperty("reason", out var reasonFallback)
                        ? reasonFallback.GetString()
                        : "remote_probe_failed";
                return (false, string.IsNullOrWhiteSpace(reason) ? "remote_probe_failed" : reason!, stdout.Trim());
            }
            catch (Exception ex)
            {
                return (false, "remote_probe_invalid_json", $"Remote tunnel probe JSON parse failed: {ex.Message}");
            }
        }

        private async Task RunRemoteWatchdogRemediationAsync(
            ServiceConfig config,
            string level,
            CancellationToken cancellationToken)
        {
            await EnsureTunnelctlCompatibilityAsync(config, cancellationToken);
            var command = BuildTunnelctlRemoteCommand(config, $"remediate --level {level} --json");
            var (ok, stdout, stderr, error) = await ExecuteRemoteGatewayctlCommandAsync(
                config,
                command,
                TimeSpan.FromSeconds(25),
                cancellationToken);

            if (!ok)
            {
                var message = FirstNonEmpty(stderr, stdout, error) ?? "remote remediation failed";
                RecordEvent("warn", $"remote remediation failed: {message}");
                return;
            }

            RecordEvent("info", $"remote remediation completed: {stdout.Trim()}");
        }

        private string BuildTunnelctlRemoteCommand(ServiceConfig config, string verbArgs)
        {
            var backendPort = config.TunnelRemotePort.ToString();
            var tunnelCtlPath = GetRelayScopedTunnelctlPath();
            var tunnelCtlConfigDir = GetRelayScopedTunnelctlConfigDirectory();
            var wrapped =
                $"TUNNELCTL_CONFIG_DIR={ShellSingleQuote(tunnelCtlConfigDir)} " +
                $"TUNNEL_BACKEND_HOST=127.0.0.1 TUNNEL_BACKEND_PORT={backendPort} " +
                $"{ShellSingleQuote(tunnelCtlPath)} {verbArgs} --backend-host 127.0.0.1 --backend-port {backendPort}";
            return $"bash -lc {ShellSingleQuote(wrapped)}";
        }

        private async Task EnsureTunnelctlCompatibilityAsync(ServiceConfig config, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            if (_tunnelctlCompatVerified && now < _nextTunnelctlCompatCheckUtc)
            {
                return;
            }

            var expectedPort = config.TunnelRemotePort;
            var verifyCommand = BuildTunnelctlRemoteCommand(config, "probe --json");
            var (ok, stdout, stderr, error) = await ExecuteRemoteGatewayctlCommandAsync(
                config,
                verifyCommand,
                TimeSpan.FromSeconds(20),
                cancellationToken);

            if (ok && TryReadBackendPort(stdout, out var actualPort) && actualPort == expectedPort)
            {
                _tunnelctlCompatVerified = true;
                _nextTunnelctlCompatCheckUtc = now.AddMinutes(10);
                return;
            }

            _log.Warn($"Relay '{_relay.Name}' tunnelctl compatibility drift detected; attempting auto-heal. expectedBackendPort={expectedPort} actualBackendPort={(TryReadBackendPort(stdout, out actualPort) ? actualPort : -1)}");
            var healed = await TryAutoHealTunnelctlAsync(config, expectedPort, cancellationToken);
            _log.Info($"Relay '{_relay.Name}' tunnelctl auto-heal {(healed ? "succeeded" : "failed")} for backend port {expectedPort}.");
            _tunnelctlCompatVerified = healed;
            _nextTunnelctlCompatCheckUtc = now.Add(healed ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1));
        }

        private async Task<bool> TryAutoHealTunnelctlAsync(ServiceConfig config, int expectedPort, CancellationToken cancellationToken)
        {
            var tunnelCtlPath = GetRelayScopedTunnelctlPath();
            var script = string.Join(" && ", new[]
            {
                $"f={ShellSingleQuote(tunnelCtlPath)}",
                "[ -f \"$f\" ]",
                "chmod 0755 \"$f\"",
                "sed -i 's/\\r$//' \"$f\"",
                "true"
            });
            var healCommand = $"bash -lc {ShellSingleQuote(script)}";
            _ = await ExecuteRemoteGatewayctlCommandAsync(config, healCommand, TimeSpan.FromSeconds(20), cancellationToken);

            var recheck = BuildTunnelctlRemoteCommand(config, "probe --json");
            var (ok, stdout, _, _) = await ExecuteRemoteGatewayctlCommandAsync(
                config,
                recheck,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            return ok && TryReadBackendPort(stdout, out var actualPort) && actualPort == expectedPort;
        }

        private static bool TryReadBackendPort(string? json, out int backendPort)
        {
            backendPort = 0;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("backendPort", out var backendPortProp))
                {
                    return false;
                }

                backendPort = backendPortProp.GetInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string GetTunnelOwnerDiagnostic(ServiceConfig config)
        {
            try
            {
                var signatureA = $":{config.TunnelRemotePort}:127.0.0.1:{config.LocalProxyListenPort}";
                var signatureB = $":{config.BootstrapSocksRemotePort}:127.0.0.1:{config.BootstrapSocksLocalPort}";
                var owners = Process.GetProcessesByName("ssh")
                    .Select(p =>
                    {
                        try
                        {
                            return (p.Id, Cmd: p.MainWindowTitle);
                        }
                        catch
                        {
                            return (p.Id, Cmd: string.Empty);
                        }
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Cmd) && (x.Cmd.Contains(signatureA, StringComparison.Ordinal) || x.Cmd.Contains(signatureB, StringComparison.Ordinal)))
                    .Select(x => x.Id.ToString())
                    .ToArray();
                return owners.Length == 0 ? "ownerPid=unknown" : $"ownerPid={string.Join(",", owners)}";
            }
            catch
            {
                return "ownerPid=unknown";
            }
        }

        private async Task<(bool Success, string Stdout, string Stderr, string? Error)> ExecuteRemoteGatewayctlCommandAsync(
            ServiceConfig config,
            string remoteCommand,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (!SshTunnelProcessFactory.TryCreateRemoteCommandStartInfo(
                    config,
                    remoteCommand,
                    out var startInfo,
                    out var createError) || startInfo is null)
            {
                return (false, string.Empty, string.Empty, createError ?? "failed to create ssh command");
            }

            Process? process = null;
            try
            {
                process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return (false, string.Empty, string.Empty, "failed to start ssh process");
                }

                process.StandardInput.Close();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                var stdout = (await stdoutTask).Trim();
                var stderr = (await stderrTask).Trim();
                if (process.ExitCode != 0)
                {
                    var detail = FirstNonEmpty(stderr, stdout);
                    var message = string.IsNullOrWhiteSpace(detail)
                        ? $"ssh exited with code {process.ExitCode}"
                        : $"ssh exited with code {process.ExitCode}: {detail}";
                    return (false, stdout, stderr, message);
                }

                return (true, stdout, stderr, null);
            }
            catch (OperationCanceledException ex)
            {
                return (false, string.Empty, string.Empty, ex.Message);
            }
            catch (Exception ex)
            {
                return (false, string.Empty, string.Empty, ex.Message);
            }
            finally
            {
                try
                {
                    if (process is { HasExited: false })
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                process?.Dispose();
            }
        }

        private async Task CleanupOrphanTunnelProcessesAsync(ServiceConfig config, CancellationToken cancellationToken)
        {
            var tunnelForwardSignature = $"127.0.0.1:{config.TunnelRemotePort}:127.0.0.1:{config.LocalProxyListenPort}";
            var bootstrapForwardSignature = $"127.0.0.1:{config.BootstrapSocksRemotePort}:127.0.0.1:{config.BootstrapSocksLocalPort}";
            var tunnelHost = (config.TunnelHost ?? string.Empty).Trim();
            var tunnelUser = (config.TunnelUser ?? string.Empty).Trim();
            var command =
                "$killed = @(); " +
                "$matched = @(); " +
                "$forwardA = '" + EscapePowerShellSingleQuoted(tunnelForwardSignature) + "'; " +
                "$forwardB = '" + EscapePowerShellSingleQuoted(bootstrapForwardSignature) + "'; " +
                "$forwardALegacy = ':" + config.TunnelRemotePort + ":127.0.0.1:" + config.LocalProxyListenPort + "'; " +
                "$forwardBLegacy = ':" + config.BootstrapSocksRemotePort + ":127.0.0.1:" + config.BootstrapSocksLocalPort + "'; " +
                "$targetHost = '" + EscapePowerShellSingleQuoted(tunnelHost) + "'; " +
                "$targetUser = '" + EscapePowerShellSingleQuoted(tunnelUser) + "'; " +
                "Get-CimInstance Win32_Process -Filter \"Name = 'ssh.exe'\" | " +
                "Where-Object { " +
                "  $_.CommandLine -and " +
                "  $_.CommandLine -like ('*' + $targetUser + '@' + $targetHost + '*') -and " +
                "  (" +
                "    $_.CommandLine -like ('*' + $forwardA + '*') -or " +
                "    $_.CommandLine -like ('*' + $forwardB + '*') -or " +
                "    $_.CommandLine -like ('*' + $forwardALegacy + '*') -or " +
                "    $_.CommandLine -like ('*' + $forwardBLegacy + '*')" +
                "  )" +
                "} | " +
                "ForEach-Object { $matched += $_.ProcessId; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; if ($?) { $killed += $_.ProcessId } }; " +
                "Write-Output ('Relay stale ssh cleanup: matched=' + $matched.Count + ', killed=' + $killed.Count)";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            try
            {
                using var process = new Process { StartInfo = psi };
                if (!process.Start())
                {
                    return;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var stdout = (await stdoutTask).Trim();
                var stderr = (await stderrTask).Trim();
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    _log.Info(stdout);
                }

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _log.Warn($"Relay stale tunnel cleanup stderr: {stderr}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to cleanup stale relay tunnel ssh processes: {ex.Message}");
            }
        }

        private async Task TryScheduleRemoteForwardCleanupAsync(ServiceConfig config, CancellationToken cancellationToken, string? connectionId = null)
        {
            if (config.TunnelRemotePort <= 0 || config.BootstrapSocksRemotePort <= 0)
            {
                return;
            }

            var ports = $"{config.TunnelRemotePort} {config.BootstrapSocksRemotePort}";
            var remoteCommand =
                "ports=" + ShellSingleQuote(ports) + "; " +
                "killed=0; " +
                "for p in $ports; do " +
                "for pid in $(ss -lntp \"( sport = :$p )\" 2>/dev/null | sed -n \"s/.*pid=\\([0-9]\\+\\).*/\\1/p\" | sort -u); do " +
                "kill -KILL \"$pid\" >/dev/null 2>&1 && killed=$((killed+1)) || true; " +
                "done; " +
                "done; " +
                "remaining=0; " +
                "for p in $ports; do " +
                "c=$(ss -lntp \"( sport = :$p )\" 2>/dev/null | sed -n \"s/.*pid=\\([0-9]\\+\\).*/\\1/p\" | wc -l); " +
                "remaining=$((remaining+c)); " +
                "done; " +
                "echo \"Remote forward listener cleanup attempted for ports: $ports; killed=$killed; remaining_listeners=$remaining\"";

            var (ok, stdout, stderr, error) = await ExecuteRemoteGatewayctlCommandAsync(
                config,
                remoteCommand,
                TimeSpan.FromSeconds(12),
                cancellationToken);
            if (!ok)
            {
                _log.Warn($"Remote relay tunnel cleanup request failed: {FirstNonEmpty(stderr, stdout, error) ?? "unknown error"} connectionId={connectionId ?? _activeTunnelConnectionId ?? "none"}");
            }
            else if (!string.IsNullOrWhiteSpace(stdout))
            {
                _log.Info($"{stdout} connectionId={connectionId ?? _activeTunnelConnectionId ?? "none"}");
            }
        }

        private async Task EnsureLocalGatewayRuntimeAsync(CancellationToken cancellationToken)
        {
            var protocol = LocalGatewayProtocols.Normalize(_relay.LocalGateway?.Protocol);
            if (string.Equals(protocol, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
            {
                await EnsureLocalOpenVpnRuntimeAsync(cancellationToken);
                return;
            }

            if (_localGatewayProcess is { HasExited: true })
            {
                var exitCode = -1;
                try
                {
                    exitCode = _localGatewayProcess.ExitCode;
                }
                catch
                {
                }

                _log.Warn($"Relay '{_relay.Name}' local connector process exited between pulses. exitCode={exitCode}");
                try
                {
                    _localGatewayProcess.Dispose();
                }
                catch
                {
                }

                _localGatewayProcess = null;
                _localGatewayRuntimeFingerprint = null;
            }

            if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(_relay.OutgoingAdapterId, _relay.OutgoingAdapterIfIndex, out var ic2Ip, out _) || ic2Ip is null)
            {
                await StopLocalGatewayRuntimeAsync();
                _localGatewayState = "unhealthy";
                _localGatewayHealthReason = "ic2_adapter_unavailable";
                return;
            }

            var binaryPath = ServicePaths.ResolveConnectorCoreExecutablePath();
            if (!File.Exists(binaryPath))
            {
                await StopLocalGatewayRuntimeAsync();
                _localGatewayState = "unhealthy";
                _localGatewayHealthReason = "connector_core_missing";
                return;
            }

            var clients = EnsureDefaultLocalClientForProtocol(
                protocol,
                _runtime.GetLocalGatewayClientsSnapshot(_relay.Id)
                    .Where(x => x.Enabled &&
                                string.Equals(LocalGatewayProtocols.Normalize(x.Protocol), protocol, StringComparison.OrdinalIgnoreCase))
                    .ToArray());
            var localConfig = _relay.LocalGateway ?? new LocalGatewayConfig();
            var fingerprint = BuildLocalGatewayRuntimeFingerprint(protocol, ic2Ip, clients);
            if (_localGatewayProcess is not null &&
                !_localGatewayProcess.HasExited &&
                string.Equals(_localGatewayRuntimeFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _localGatewayState = "active";
                _localGatewayHealthReason = null;
                return;
            }

            if (_localGatewayProcess is null || _localGatewayProcess.HasExited)
            {
                await CleanupOrphanLocalGatewayProcessesAsync(
                    ServicePaths.GetRelayLocalGatewayConnectorMetadataPath(_relay.Id),
                    "connector-core.exe",
                    cancellationToken);
            }

            var resolvedIfIndex = NetworkAdapterCatalog.TryResolveIfIndex(_relay.OutgoingAdapterId, _relay.OutgoingAdapterIfIndex, out var outIfIndex)
                ? outIfIndex
                : _relay.OutgoingAdapterIfIndex;
            var gatewayText = NetworkAdapterCatalog.TryGetPrimaryIpv4Gateway(resolvedIfIndex, out var outGateway) && outGateway is not null
                ? outGateway.ToString()
                : "n/a";
            _log.Info($"Relay '{_relay.Name}' local runtime egress bind: onAdapter='{_relay.OutgoingAdapterId}' ifIndex={resolvedIfIndex} bindIp={ic2Ip} gateway={gatewayText}");

            await StopLocalGatewayRuntimeAsync();
            if (!TryValidateLocalListenPort(localConfig.Port, out var localPortReason))
            {
                _localGatewayState = "unhealthy";
                _localGatewayHealthReason = localPortReason;
                return;
            }
            WriteRelayLocalSingBoxConfig(localConfig, protocol, clients, ic2Ip);
            WriteRelayLocalConnectorMetadata(protocol);
            EnsureRelayLocalFirewallRule(localConfig.Port, protocol);

            var startInfo = new ProcessStartInfo
            {
                FileName = binaryPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(ServicePaths.GetRelayLocalGatewaySingBoxConfigPath(_relay.Id));
            startInfo.ArgumentList.Add("--metadata");
            startInfo.ArgumentList.Add(ServicePaths.GetRelayLocalGatewayConnectorMetadataPath(_relay.Id));
            startInfo.ArgumentList.Add("--accounting-db");
            startInfo.ArgumentList.Add(ServicePaths.GetRelayLocalGatewayAccountingDbPath(_relay.Id));
            startInfo.ArgumentList.Add("--state-file");
            startInfo.ArgumentList.Add(ServicePaths.GetRelayLocalGatewayConnectorStatePath(_relay.Id));
            startInfo.ArgumentList.Add("--lock-file");
            startInfo.ArgumentList.Add(ServicePaths.GetRelayLocalGatewayConnectorLockPath(_relay.Id));
            startInfo.ArgumentList.Add("--sync-command");
            startInfo.ArgumentList.Add(string.Empty);

            _localGatewayProcess = Process.Start(startInfo);
            if (_localGatewayProcess is null)
            {
                _localGatewayState = "unhealthy";
                _localGatewayHealthReason = "connector_core_start_failed";
                _localGatewayRuntimeFingerprint = null;
                return;
            }

            _ = PumpStreamAsync(_localGatewayProcess.StandardOutput, ServicePaths.GetRelayLocalGatewaySingBoxStdoutLogPath(_relay.Id), cancellationToken);
            _ = PumpStreamAsync(_localGatewayProcess.StandardError, ServicePaths.GetRelayLocalGatewaySingBoxStderrLogPath(_relay.Id), cancellationToken);

            await Task.Delay(700, cancellationToken);
            if (_localGatewayProcess.HasExited)
            {
                _localGatewayState = "unhealthy";
                _localGatewayHealthReason = $"connector_core_exited_{_localGatewayProcess.ExitCode}";
                _localGatewayRuntimeFingerprint = null;
                return;
            }

            _localGatewayState = "active";
            _localGatewayHealthReason = null;
            _localGatewayRuntimeFingerprint = fingerprint;
        }

        private async Task EnsureLocalOpenVpnRuntimeAsync(CancellationToken cancellationToken)
        {
            var binaryPath = ServicePaths.ResolveOpenVpnExecutablePath();
            if (!File.Exists(binaryPath))
            {
                _localGatewayState = "degraded";
                _localGatewayHealthReason = "openvpn_installing";
                var installOk = await TryInstallLocalOpenVpnRuntimeAsync(cancellationToken);
                binaryPath = ServicePaths.ResolveOpenVpnExecutablePath();
                if (!installOk || !File.Exists(binaryPath))
                {
                    await StopLocalGatewayRuntimeAsync();
                    _localGatewayState = "unhealthy";
                    _localGatewayHealthReason = "openvpn_binary_missing";
                    return;
                }
            }

            var localConfig = _relay.LocalGateway ?? new LocalGatewayConfig();
            var clients = EnsureDefaultLocalClientForProtocol(
                LocalGatewayProtocols.OpenVpnTcp,
                _runtime.GetLocalGatewayClientsSnapshot(_relay.Id)
                    .Where(x => x.Enabled &&
                                string.Equals(LocalGatewayProtocols.Normalize(x.Protocol), LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
                    .ToArray());
            var staticFingerprint = BuildRelayOpenVpnStaticFingerprint(localConfig);
            var fingerprint = BuildLocalGatewayRuntimeFingerprint(LocalGatewayProtocols.OpenVpnTcp, IPAddress.Loopback, clients);
            if (_localGatewayProcess is not null &&
                !_localGatewayProcess.HasExited &&
                string.Equals(_localGatewayRuntimeFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _localGatewayState = "active";
                _localGatewayHealthReason = null;
                return;
            }

            if (_localGatewayProcess is not null &&
                !_localGatewayProcess.HasExited &&
                string.Equals(_localOpenVpnStaticFingerprint, staticFingerprint, StringComparison.Ordinal))
            {
                if (TryHotSyncRelayOpenVpnClients(clients))
                {
                    _localGatewayState = "active";
                    _localGatewayHealthReason = null;
                    _localGatewayRuntimeFingerprint = fingerprint;
                    return;
                }
            }

            if (_localGatewayProcess is null || _localGatewayProcess.HasExited)
            {
                await CleanupOrphanLocalGatewayProcessesAsync(
                    ServicePaths.GetRelayLocalGatewayOpenVpnServerConfigPath(_relay.Id),
                    "openvpn.exe",
                    cancellationToken);
            }

            await StopLocalGatewayRuntimeAsync();
            if (!TryValidateLocalListenPort(localConfig.Port, out var localPortReason))
            {
                _localGatewayState = "unhealthy";
                _localGatewayHealthReason = localPortReason;
                return;
            }
            WriteRelayOpenVpnAuthFiles(clients);
            if (!EnsureRelayOpenVpnBundleFiles())
            {
                _localGatewayState = "unhealthy";
                _localGatewayHealthReason = "openvpn_bundle_missing_or_invalid";
                return;
            }

            WriteRelayOpenVpnServerConfig(localConfig);
            EnsureRelayLocalFirewallRule(localConfig.Port, LocalGatewayProtocols.OpenVpnTcp);

            var startInfo = new ProcessStartInfo
            {
                FileName = binaryPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(ServicePaths.GetRelayLocalGatewayOpenVpnServerConfigPath(_relay.Id));

            _localGatewayProcess = Process.Start(startInfo);
            if (_localGatewayProcess is null)
            {
                _localGatewayState = "unhealthy";
                _localGatewayHealthReason = "openvpn_start_failed";
                _localGatewayRuntimeFingerprint = null;
                return;
            }

            _ = PumpStreamAsync(_localGatewayProcess.StandardOutput, ServicePaths.GetRelayLocalGatewayOpenVpnStdoutLogPath(_relay.Id), cancellationToken);
            _ = PumpStreamAsync(_localGatewayProcess.StandardError, ServicePaths.GetRelayLocalGatewayOpenVpnStderrLogPath(_relay.Id), cancellationToken);

            await Task.Delay(1200, cancellationToken);
            if (_localGatewayProcess.HasExited)
            {
                _localGatewayState = "unhealthy";
                _localGatewayHealthReason = $"openvpn_exited_{_localGatewayProcess.ExitCode}";
                _localGatewayRuntimeFingerprint = null;
                return;
            }

            _localGatewayState = "active";
            _localGatewayHealthReason = null;
            _localOpenVpnStaticFingerprint = staticFingerprint;
            _localGatewayRuntimeFingerprint = fingerprint;
        }

        private async Task<bool> TryInstallLocalOpenVpnRuntimeAsync(CancellationToken cancellationToken)
        {
            var installerPath = ServicePaths.ResolveOpenVpnDriverInstallerPath();
            if (!File.Exists(installerPath))
            {
                _log.Warn($"Local OpenVPN installer payload not found: {installerPath}");
                return false;
            }

            try
            {
                var extension = Path.GetExtension(installerPath).ToLowerInvariant();
                if (string.Equals(extension, ".msi", StringComparison.Ordinal))
                {
                    var msiInfo = new ProcessStartInfo
                    {
                        FileName = "msiexec.exe",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    msiInfo.ArgumentList.Add("/i");
                    msiInfo.ArgumentList.Add(installerPath);
                    msiInfo.ArgumentList.Add("/qn");
                    msiInfo.ArgumentList.Add("/norestart");
                    return await RunLocalInstallerProcessAsync(msiInfo, cancellationToken);
                }

                var exeSilentInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                exeSilentInfo.ArgumentList.Add("/S");
                if (await RunLocalInstallerProcessAsync(exeSilentInfo, cancellationToken))
                {
                    return true;
                }

                var exeQuietInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                exeQuietInfo.ArgumentList.Add("/quiet");
                exeQuietInfo.ArgumentList.Add("/norestart");
                return await RunLocalInstallerProcessAsync(exeQuietInfo, cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Warn($"Local OpenVPN installer execution failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> RunLocalInstallerProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return false;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(8));
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                if (process.ExitCode != 0)
                {
                    var detail = FirstNonEmpty(stderr?.Trim(), stdout?.Trim());
                    _log.Warn($"Local OpenVPN installer failed (exit={process.ExitCode}). {detail}");
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                _log.Warn("Local OpenVPN installer timed out.");
                return false;
            }
            catch (Exception ex)
            {
                _log.Warn($"Local OpenVPN installer error: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (process is { HasExited: false })
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                process?.Dispose();
            }
        }

        private async Task StopLocalGatewayRuntimeAsync()
        {
            try
            {
                if (_localGatewayProcess is { HasExited: false })
                {
                    _localGatewayProcess.Kill(entireProcessTree: true);
                    await _localGatewayProcess.WaitForExitAsync();
                }
            }
            catch
            {
            }
            finally
            {
                _localGatewayProcess?.Dispose();
                _localGatewayProcess = null;
                _localOpenVpnStaticFingerprint = null;
                _localGatewayRuntimeFingerprint = null;
                RemoveRelayLocalFirewallRule();
            }
        }

        private async Task EnsureLocalOmniPanelRuntimeAsync(CancellationToken cancellationToken)
        {
            var nodePath = ServicePaths.ResolveNodeExecutablePath();
            var serverJsPath = ServicePaths.ResolveOmniPanelServerJsPath();
            if (!File.Exists(nodePath))
            {
                await StopLocalOmniPanelRuntimeAsync();
                _omniPanelState = "unhealthy";
                _omniPanelLastError = "omnipanel_node_missing";
                _log.Warn($"Relay '{_relay.Name}' OmniPanel node executable missing: {nodePath}");
                return;
            }

            if (!File.Exists(serverJsPath))
            {
                await StopLocalOmniPanelRuntimeAsync();
                _omniPanelState = "unhealthy";
                _omniPanelLastError = "omnipanel_server_missing";
                _log.Warn($"Relay '{_relay.Name}' OmniPanel server.js missing: {serverJsPath}");
                return;
            }
            var panelPort = _relay.OmniPanel?.Port is > 0 and <= 65535 ? _relay.OmniPanel.Port : 2054;
            var protocol = LocalGatewayProtocols.Normalize(_relay.LocalGateway?.Protocol);
            var relayDir = ServicePaths.GetRelayLocalGatewayDirectory(_relay.Id);
            Directory.CreateDirectory(relayDir);

            var fingerprint = $"{nodePath}|{serverJsPath}|{panelPort}|{protocol}|{_relay.OmniPanel?.Username}|{_relay.OmniPanel?.Domain}";
            if (_omniPanelProcess is not null &&
                !_omniPanelProcess.HasExited &&
                string.Equals(_omniPanelRuntimeFingerprint, fingerprint, StringComparison.Ordinal) &&
                await IsLoopbackTcpListeningAsync(panelPort, cancellationToken))
            {
                _omniPanelState = "active";
                _omniPanelLastError = null;
                return;
            }

            if (_omniPanelProcess is null || _omniPanelProcess.HasExited)
            {
                await CleanupOrphanOmniPanelProcessByPortAsync(panelPort, cancellationToken);
            }

            await StopLocalOmniPanelRuntimeAsync();
            if (!TryValidateLocalListenPort(panelPort, out var panelPortReason))
            {
                _omniPanelState = "unhealthy";
                _omniPanelLastError = panelPortReason;
                _log.Warn($"Relay '{_relay.Name}' OmniPanel cannot start: {panelPortReason} (port {panelPort})");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                WorkingDirectory = Path.GetDirectoryName(serverJsPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(serverJsPath);
            startInfo.Environment["HOSTNAME"] = "0.0.0.0";
            startInfo.Environment["PORT"] = panelPort.ToString();
            startInfo.Environment["OMNIRELAY_LOCAL_RELAY_MODE"] = "true";
            startInfo.Environment["OMNIRELAY_LOCAL_PROTOCOL_ID"] = protocol;
            startInfo.Environment["OMNIRELAY_ACTIVE_PROTOCOL"] = MapLocalProtocolToPanelProtocol(protocol);
            var localPublicPort = _relay.LocalGateway?.Port is > 0 and <= 65535 ? _relay.LocalGateway.Port : 2443;
            var localAdvertisedHost = (_relay.LocalGateway?.RemoteAddress ?? string.Empty).Trim();
            startInfo.Environment["SINGBOX_PUBLIC_PORT"] = localPublicPort.ToString();
            startInfo.Environment["OPENVPN_PUBLIC_PORT"] = localPublicPort.ToString();
            if (!string.IsNullOrWhiteSpace(localAdvertisedHost))
            {
                startInfo.Environment["PANEL_PUBLIC_HOST"] = localAdvertisedHost;
                startInfo.Environment["OPENVPN_PUBLIC_HOST"] = localAdvertisedHost;
            }
            startInfo.Environment["SINGBOX_RELOAD_COMMAND"] = string.Empty;
            startInfo.Environment["SINGBOX_ACCOUNTING_DB"] = ServicePaths.GetRelayLocalGatewayAccountingDbPath(_relay.Id);
            startInfo.Environment["SINGBOX_VLESS_PLAIN_CLIENTS_FILE"] = ServicePaths.GetRelayLocalGatewayClientsPath(_relay.Id, LocalGatewayProtocols.VlessTcpPlain);
            startInfo.Environment["SINGBOX_SHADOWSOCKS_CLIENTS_FILE"] = ServicePaths.GetRelayLocalGatewayClientsPath(_relay.Id, LocalGatewayProtocols.Shadowsocks);
            startInfo.Environment["OPENVPN_CLIENTS_FILE"] = ServicePaths.GetRelayLocalGatewayClientsPath(_relay.Id, LocalGatewayProtocols.OpenVpnTcp);
            startInfo.Environment["OPENVPN_EXPORT_DIR"] = Path.Combine(relayDir, "openvpn-exports");
            var sqlite3Path = ServicePaths.ResolveSqlite3ExecutablePath();
            if (File.Exists(sqlite3Path))
            {
                startInfo.Environment["OMNIRELAY_SQLITE3_BIN"] = sqlite3Path;
                var sqliteDir = Path.GetDirectoryName(sqlite3Path) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sqliteDir))
                {
                    var existingPath = startInfo.Environment["PATH"] ?? string.Empty;
                    startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(existingPath)
                        ? sqliteDir
                        : $"{sqliteDir};{existingPath}";
                }
            }
            startInfo.Environment["OMNIPANEL_AUTH_FILE"] = ServicePaths.GetRelayOmniPanelAuthPath(_relay.Id);
            startInfo.Environment["OMNIPANEL_AUTH_USERNAME"] = string.IsNullOrWhiteSpace(_relay.OmniPanel?.Username) ? "admin" : _relay.OmniPanel.Username.Trim();
            startInfo.Environment["OMNIPANEL_AUTH_PASSWORD"] = string.IsNullOrWhiteSpace(_relay.OmniPanel?.Password) ? "admin123" : _relay.OmniPanel.Password;

            var omniStdoutPath = ServicePaths.GetRelayOmniPanelStdoutLogPath(_relay.Id);
            var omniStderrPath = ServicePaths.GetRelayOmniPanelStderrLogPath(_relay.Id);
            try
            {
                var stdoutDir = Path.GetDirectoryName(omniStdoutPath);
                if (!string.IsNullOrWhiteSpace(stdoutDir))
                {
                    Directory.CreateDirectory(stdoutDir);
                }

                var stderrDir = Path.GetDirectoryName(omniStderrPath);
                if (!string.IsNullOrWhiteSpace(stderrDir))
                {
                    Directory.CreateDirectory(stderrDir);
                }

                if (!File.Exists(omniStdoutPath))
                {
                    File.WriteAllText(omniStdoutPath, string.Empty);
                }

                if (!File.Exists(omniStderrPath))
                {
                    File.WriteAllText(omniStderrPath, string.Empty);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Relay '{_relay.Name}' OmniPanel log file precreate failed: {ex.Message}");
            }

            _log.Info($"Relay '{_relay.Name}' starting OmniPanel: node={nodePath} server={serverJsPath} port={panelPort}");

            _omniPanelProcess = Process.Start(startInfo);
            if (_omniPanelProcess is null)
            {
                _omniPanelState = "unhealthy";
                _omniPanelLastError = "omnipanel_start_failed";
                _omniPanelRuntimeFingerprint = null;
                _log.Warn($"Relay '{_relay.Name}' OmniPanel process start returned null.");
                return;
            }

            EnsureRelayOmniPanelFirewallRule(panelPort);

            _ = PumpStreamAsync(_omniPanelProcess.StandardOutput, omniStdoutPath, cancellationToken);
            _ = PumpStreamAsync(_omniPanelProcess.StandardError, omniStderrPath, cancellationToken);

            await Task.Delay(1000, cancellationToken);
            if (_omniPanelProcess.HasExited)
            {
                _omniPanelState = "unhealthy";
                _omniPanelLastError = $"omnipanel_exited_{_omniPanelProcess.ExitCode}";
                _omniPanelRuntimeFingerprint = null;
                _log.Warn($"Relay '{_relay.Name}' OmniPanel exited immediately with code {_omniPanelProcess.ExitCode}.");
                return;
            }

            _omniPanelRuntimeFingerprint = fingerprint;
            _omniPanelState = "active";
            _omniPanelLastError = null;
            _log.Info($"Relay '{_relay.Name}' OmniPanel is active on port {panelPort}.");
        }

        private async Task CleanupOrphanOmniPanelProcessByPortAsync(int port, CancellationToken cancellationToken)
        {
            if (port <= 0 || port > 65535)
            {
                return;
            }

            try
            {
                var command =
                    "$k=0; " +
                    "$p=" + port + "; " +
                    "netstat -ano -p tcp | Select-String (':'+$p+'\\s+.*LISTENING\\s+\\d+$') | ForEach-Object { " +
                    "  $m=[regex]::Match($_.Line,'\\s(\\d+)\\s*$'); " +
                    "  if(-not $m.Success){ return } " +
                    "  $ownerPid=[int]$m.Groups[1].Value; " +
                    "  $proc=Get-CimInstance Win32_Process -Filter ('ProcessId='+$ownerPid) -ErrorAction SilentlyContinue; " +
                    "  if($null -eq $proc){ return } " +
                    "  $name=if($null -ne $proc.Name){ $proc.Name.ToLowerInvariant() } else { '' }; " +
                    "  $cmd=if($null -ne $proc.CommandLine){ $proc.CommandLine.ToLowerInvariant() } else { '' }; " +
                    "  if(($name -eq 'node.exe' -or $name -eq 'node') -and $cmd.Contains('omnipanel')){ " +
                    "    Stop-Process -Id $ownerPid -Force -ErrorAction SilentlyContinue; if($?) { $k++ } " +
                    "  } " +
                    "}; " +
                    "Write-Output $k";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);

                using var process = Process.Start(psi);
                if (process is null)
                {
                    return;
                }

                await process.WaitForExitAsync(cancellationToken);
                var stdout = (await process.StandardOutput.ReadToEndAsync()).Trim();
                var stderr = (await process.StandardError.ReadToEndAsync()).Trim();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _log.Warn($"Relay '{_relay.Name}' orphan OmniPanel cleanup stderr: {stderr}");
                }

                if (int.TryParse(stdout, out var killed) && killed > 0)
                {
                    _log.Info($"Relay '{_relay.Name}' orphan OmniPanel cleanup killed {killed} process(es) on port {port}.");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Relay '{_relay.Name}' orphan OmniPanel cleanup failed: {ex.Message}");
            }
        }

        private async Task StopLocalOmniPanelRuntimeAsync()
        {
            try
            {
                if (_omniPanelProcess is { HasExited: false })
                {
                    _omniPanelProcess.Kill(entireProcessTree: true);
                    await _omniPanelProcess.WaitForExitAsync();
                }
            }
            catch
            {
            }
            finally
            {
                _omniPanelProcess?.Dispose();
                _omniPanelProcess = null;
                _omniPanelRuntimeFingerprint = null;
                RemoveRelayOmniPanelFirewallRule();
            }
        }

        private static string MapLocalProtocolToPanelProtocol(string? localProtocol)
        {
            var normalized = LocalGatewayProtocols.Normalize(localProtocol);
            if (string.Equals(normalized, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
            {
                return "shadowsocks_singbox";
            }

            if (string.Equals(normalized, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
            {
                return "openvpn_tcp_singbox";
            }

            return "vless_plain_singbox";
        }

        private string BuildLocalGatewayRuntimeFingerprint(
            string protocol,
            IPAddress ic2Ip,
            IReadOnlyList<LocalGatewayClient> clients)
        {
            var userSignature = string.Join(
                "|",
                clients
                    .OrderBy(x => x.Id, StringComparer.Ordinal)
                    .Select(x => $"{x.Id}:{x.Secret}:{x.Enabled}:{x.Email}"));
            return $"{protocol}|{_relay.LocalGateway.Port}|{_relay.LocalGateway.BindAddress}|{ic2Ip}|{userSignature}";
        }

        private LocalGatewayClient[] EnsureDefaultLocalClientForProtocol(
            string protocol,
            LocalGatewayClient[] clients)
        {
            static bool IsUsable(LocalGatewayClient client, string normalizedProtocol)
            {
                if (!client.Enabled)
                {
                    return false;
                }

                if (string.Equals(normalizedProtocol, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
                {
                    return !string.IsNullOrWhiteSpace(client.Secret);
                }

                if (string.Equals(normalizedProtocol, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
                {
                    var user = string.IsNullOrWhiteSpace(client.Username) ? client.Email : client.Username;
                    return !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(client.Secret);
                }

                return !string.IsNullOrWhiteSpace(client.Id);
            }

            if (clients.Any(x => IsUsable(x, protocol)))
            {
                return clients;
            }

            var idPrefix = _relay.Id.Length > 8 ? _relay.Id[..8] : _relay.Id;
            var defaultEmail = $"first-user-{idPrefix}@local.relay";
            if (_runtime.TryAddLocalGatewayClient(defaultEmail, "omni-admin", _relay.Id, out _, out var error))
            {
                _log.Info($"Relay '{_relay.Name}' auto-created default local client for protocol '{protocol}'.");
            }
            else if (!string.IsNullOrWhiteSpace(error) &&
                     !error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn($"Relay '{_relay.Name}' could not auto-create default local client for '{protocol}': {error}");
            }

            return _runtime.GetLocalGatewayClientsSnapshot(_relay.Id)
                .Where(x => x.Enabled &&
                            string.Equals(LocalGatewayProtocols.Normalize(x.Protocol), protocol, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private void WriteRelayLocalSingBoxConfig(
            LocalGatewayConfig localConfig,
            string protocol,
            IReadOnlyList<LocalGatewayClient> clients,
            IPAddress ic2Ip)
        {
            var directory = ServicePaths.GetRelayLocalGatewayDirectory(_relay.Id);
            Directory.CreateDirectory(directory);

            object inbound;
            if (string.Equals(protocol, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
            {
                inbound = new
                {
                    type = "shadowsocks",
                    tag = "local-shadowsocks-in",
                    listen = localConfig.BindAddress,
                    listen_port = localConfig.Port,
                    method = "aes-128-gcm",
                    users = clients
                        .Where(x => !string.IsNullOrWhiteSpace(x.Secret))
                        .Select(x => new
                        {
                            name = x.Email,
                            password = x.Secret
                        })
                        .ToArray()
                };
            }
            else
            {
                inbound = new
                {
                    type = "vless",
                    tag = "local-vless-in",
                    listen = localConfig.BindAddress,
                    listen_port = localConfig.Port,
                    users = clients.Select(x => new
                    {
                        name = x.Email,
                        uuid = x.Id
                    }).ToArray()
                };
            }

            var singBoxConfig = new
            {
                log = new
                {
                    level = "warn"
                },
                inbounds = new[] { inbound },
                outbounds = new object[]
                {
                    new
                    {
                        type = "direct",
                        tag = "direct",
                        inet4_bind_address = ic2Ip.ToString()
                    },
                    new
                    {
                        type = "block",
                        tag = "blocked"
                    }
                },
                route = new
                {
                    final = "direct"
                }
            };

            var raw = JsonSerializer.Serialize(singBoxConfig, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            File.WriteAllText(ServicePaths.GetRelayLocalGatewaySingBoxConfigPath(_relay.Id), raw);
        }

        private void WriteRelayOpenVpnAuthFiles(IReadOnlyList<LocalGatewayClient> clients)
        {
            var dir = ServicePaths.GetRelayLocalGatewayOpenVpnDirectory(_relay.Id);
            Directory.CreateDirectory(dir);

            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var lines = clients
                .Where(x => x.Enabled)
                .Select(x =>
                {
                    var username = NormalizeOpenVpnUsername(x.Username, x.Email);
                    var password = (x.Secret ?? string.Empty).Trim();
                    return string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)
                        ? null
                        : $"{username}:{password}";
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            File.WriteAllLines(ServicePaths.GetRelayLocalGatewayOpenVpnAuthFilePath(_relay.Id), lines, utf8NoBom);

            var authScript = $@"param([string]$authFilePath)
$authFile = '{ServicePaths.GetRelayLocalGatewayOpenVpnAuthFilePath(_relay.Id)}'
if (-not (Test-Path -LiteralPath $authFile)) {{ exit 1 }}
$lines = Get-Content -LiteralPath $authFile -ErrorAction SilentlyContinue
if ($null -eq $lines) {{ exit 1 }}
$content = Get-Content -LiteralPath $authFilePath -ErrorAction SilentlyContinue
if ($null -eq $content -or $content.Count -lt 2) {{ exit 1 }}
$username = ($content[0] ?? '').Trim()
$password = ($content[1] ?? '').Trim()
foreach ($line in $lines) {{
  $pair = $line.Split(':',2)
  if ($pair.Count -eq 2 -and $pair[0] -eq $username -and $pair[1] -eq $password) {{ exit 0 }}
}}
exit 1";
            File.WriteAllText(ServicePaths.GetRelayLocalGatewayOpenVpnAuthScriptPath(_relay.Id), authScript, utf8NoBom);

            var wrapper = $@"@echo off
setlocal
set ""PSH=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe""
""%PSH%"" -NoProfile -ExecutionPolicy Bypass -File ""{ServicePaths.GetRelayLocalGatewayOpenVpnAuthScriptPath(_relay.Id)}"" ""%~1""
exit /b %ERRORLEVEL%
";
            File.WriteAllText(ServicePaths.GetRelayLocalGatewayOpenVpnAuthCmdPath(_relay.Id), wrapper, utf8NoBom);
        }

        private void WriteRelayLocalConnectorMetadata(string localProtocol)
        {
            var protocol = MapLocalProtocolToPanelProtocol(localProtocol);
            var clientsFile = ServicePaths.GetRelayLocalGatewayClientsPath(_relay.Id, localProtocol);
            var payload = new
            {
                active_protocol = protocol,
                accounting = new
                {
                    source = "connector_tracker",
                    clientsFile
                }
            };

            var raw = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            File.WriteAllText(ServicePaths.GetRelayLocalGatewayConnectorMetadataPath(_relay.Id), raw);
        }

        private bool EnsureRelayOpenVpnBundleFiles()
        {
            var remote = _relay.RemoteGateway ?? new RemoteGatewayConfig();
            var configuredCa = (remote.OpenVpnSharedCaCertPath ?? string.Empty).Trim();
            var configuredCert = (remote.OpenVpnSharedClientCertPath ?? string.Empty).Trim();
            var configuredKey = (remote.OpenVpnSharedClientKeyPath ?? string.Empty).Trim();
            var configuredTlsCrypt = (remote.OpenVpnSharedTlsCryptKeyPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(configuredCa) ||
                string.IsNullOrWhiteSpace(configuredCert) ||
                string.IsNullOrWhiteSpace(configuredKey) ||
                string.IsNullOrWhiteSpace(configuredTlsCrypt))
            {
                return false;
            }

            var caPath = ServicePaths.GetRelayLocalGatewayOpenVpnCaPath(_relay.Id);
            var certPath = ServicePaths.GetRelayLocalGatewayOpenVpnServerCertPath(_relay.Id);
            var keyPath = ServicePaths.GetRelayLocalGatewayOpenVpnServerKeyPath(_relay.Id);
            var tlsCryptPath = ServicePaths.GetRelayLocalGatewayOpenVpnTlsCryptKeyPath(_relay.Id);

            if (!File.Exists(configuredCa) ||
                !File.Exists(configuredCert) ||
                !File.Exists(configuredKey) ||
                !File.Exists(configuredTlsCrypt))
            {
                return false;
            }

            try
            {
                File.Copy(configuredCa, caPath, overwrite: true);
                File.Copy(configuredCert, certPath, overwrite: true);
                File.Copy(configuredKey, keyPath, overwrite: true);
                File.Copy(configuredTlsCrypt, tlsCryptPath, overwrite: true);
            }
            catch
            {
                return false;
            }

            return
                new FileInfo(caPath).Length > 0 &&
                new FileInfo(certPath).Length > 0 &&
                new FileInfo(keyPath).Length > 0 &&
                new FileInfo(tlsCryptPath).Length > 0;
        }

        private void WriteRelayOpenVpnServerConfig(LocalGatewayConfig config)
        {
            static string Quote(string path) => $"\"{path.Replace("\\", "/")}\"";
            var authVerifyCommand = ServicePaths.GetRelayLocalGatewayOpenVpnAuthCmdPath(_relay.Id).Replace("\\", "/");
            var statusPath = Path.Combine(ServicePaths.GetRelayLocalGatewayOpenVpnDirectory(_relay.Id), "status.log");
            var managementPort = GetRelayOpenVpnManagementPort();

            var sb = new StringBuilder(4096);
            sb.AppendLine($"port {config.Port}");
            sb.AppendLine("proto tcp-server");
            sb.AppendLine("dev tun");
            sb.AppendLine("topology subnet");
            sb.AppendLine("server 10.66.0.0 255.255.255.0");
            sb.AppendLine("keepalive 10 60");
            sb.AppendLine($"ca {Quote(ServicePaths.GetRelayLocalGatewayOpenVpnCaPath(_relay.Id))}");
            sb.AppendLine($"cert {Quote(ServicePaths.GetRelayLocalGatewayOpenVpnServerCertPath(_relay.Id))}");
            sb.AppendLine($"key {Quote(ServicePaths.GetRelayLocalGatewayOpenVpnServerKeyPath(_relay.Id))}");
            sb.AppendLine($"tls-crypt {Quote(ServicePaths.GetRelayLocalGatewayOpenVpnTlsCryptKeyPath(_relay.Id))}");
            sb.AppendLine("verify-client-cert require");
            sb.AppendLine("username-as-common-name");
            sb.AppendLine($"auth-user-pass-verify {Quote(authVerifyCommand)} via-file");
            sb.AppendLine("script-security 2");
            sb.AppendLine("persist-key");
            sb.AppendLine("persist-tun");
            sb.AppendLine("push \"redirect-gateway def1 bypass-dhcp\"");
            sb.AppendLine("push \"dhcp-option DNS 1.1.1.1\"");
            sb.AppendLine("push \"dhcp-option DNS 8.8.8.8\"");
            sb.AppendLine($"status {Quote(statusPath)}");
            sb.AppendLine("cipher AES-256-GCM");
            sb.AppendLine("data-ciphers AES-256-GCM:AES-128-GCM");
            sb.AppendLine("auth SHA256");
            sb.AppendLine($"management 127.0.0.1 {managementPort}");
            sb.AppendLine("verb 3");
            File.WriteAllText(ServicePaths.GetRelayLocalGatewayOpenVpnServerConfigPath(_relay.Id), sb.ToString(), Encoding.UTF8);
        }

        private string BuildRelayOpenVpnStaticFingerprint(LocalGatewayConfig config)
        {
            return string.Join(
                "|",
                LocalGatewayProtocols.OpenVpnTcp,
                config.Port,
                config.BindAddress ?? string.Empty,
                ServicePaths.GetRelayLocalGatewayOpenVpnServerConfigPath(_relay.Id));
        }

        private int GetRelayOpenVpnManagementPort()
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(_relay.Id ?? string.Empty));
            var bucket = ((bytes[0] << 8) | bytes[1]) % 1000;
            var normalized = Math.Abs(bucket);
            return 21195 + normalized;
        }

        private bool TryHotSyncRelayOpenVpnClients(IReadOnlyList<LocalGatewayClient> enabledClients)
        {
            try
            {
                WriteRelayOpenVpnAuthFiles(enabledClients);
                DisconnectIneligibleRelayOpenVpnClients(enabledClients);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"Relay '{_relay.Name}' OpenVPN hot client sync failed: {ex.Message}");
                return false;
            }
        }

        private sealed record OpenVpnSession(string CommonName, string Username, string ClientId);

        private void DisconnectIneligibleRelayOpenVpnClients(IReadOnlyList<LocalGatewayClient> enabledClients)
        {
            var allowedUsernames = enabledClients
                .Select(x => NormalizeOpenVpnUsername(x.Username, x.Email))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var session in ReadConnectedRelayOpenVpnSessions())
            {
                var user = (session.Username ?? string.Empty).Trim();
                var cn = (session.CommonName ?? string.Empty).Trim();
                var identity = !string.IsNullOrWhiteSpace(user) ? user : cn;
                if (string.IsNullOrWhiteSpace(identity))
                {
                    continue;
                }

                if (allowedUsernames.Contains(identity))
                {
                    continue;
                }

                try
                {
                    using var client = new TcpClient();
                    client.Connect(IPAddress.Loopback, GetRelayOpenVpnManagementPort());
                    using var stream = client.GetStream();
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    if (!string.IsNullOrWhiteSpace(session.ClientId))
                    {
                        writer.Write($"client-kill {session.ClientId}\n");
                    }
                    if (!string.IsNullOrWhiteSpace(cn))
                    {
                        writer.Write($"kill {cn}\n");
                    }
                    if (!string.IsNullOrWhiteSpace(user))
                    {
                        writer.Write($"kill {user}\n");
                    }
                    writer.Write("quit\n");
                }
                catch
                {
                }
            }
        }

        private IReadOnlyList<OpenVpnSession> ReadConnectedRelayOpenVpnSessions()
        {
            var sessions = ReadConnectedRelayOpenVpnSessionsFromManagement();
            if (sessions.Count > 0)
            {
                return sessions;
            }
            return ReadConnectedRelayOpenVpnSessionsFromStatusFile();
        }

        private IReadOnlyList<OpenVpnSession> ReadConnectedRelayOpenVpnSessionsFromManagement()
        {
            var list = new List<OpenVpnSession>();
            try
            {
                using var client = new TcpClient();
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                client.Connect(IPAddress.Loopback, GetRelayOpenVpnManagementPort());
                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                writer.Write("status 3\n");
                Thread.Sleep(1000);
                writer.Write("quit\n");
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var raw = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return list;
                }

                foreach (var rawLine in raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Trim();
                    if (!line.StartsWith("CLIENT_LIST", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (line.Contains(','))
                    {
                        var parts = line.Split(',', StringSplitOptions.None);
                        var cn = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                        var user = parts.Length > 9 ? parts[9].Trim() : string.Empty;
                        var cid = parts.Length > 10 ? parts[10].Trim() : string.Empty;
                        if (!string.IsNullOrWhiteSpace(cn) || !string.IsNullOrWhiteSpace(user) || !string.IsNullOrWhiteSpace(cid))
                        {
                            list.Add(new OpenVpnSession(cn, user, cid));
                        }
                        continue;
                    }

                    var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var wsCn = fields.Length > 1 ? fields[1].Trim() : string.Empty;
                    var wsUser = fields.Length > 9 ? fields[9].Trim() : string.Empty;
                    var wsCid = fields.Length > 10 ? fields[10].Trim() : string.Empty;
                    if (!string.IsNullOrWhiteSpace(wsCn) || !string.IsNullOrWhiteSpace(wsUser) || !string.IsNullOrWhiteSpace(wsCid))
                    {
                        list.Add(new OpenVpnSession(wsCn, wsUser, wsCid));
                    }
                }
            }
            catch
            {
                return list;
            }
            return list;
        }

        private IReadOnlyList<OpenVpnSession> ReadConnectedRelayOpenVpnSessionsFromStatusFile()
        {
            var statusPath = Path.Combine(ServicePaths.GetRelayLocalGatewayOpenVpnDirectory(_relay.Id), "status.log");
            var list = new List<OpenVpnSession>();
            if (!File.Exists(statusPath))
            {
                return list;
            }

            foreach (var rawLine in File.ReadLines(statusPath))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                var line = rawLine.Trim();
                if (!line.StartsWith("CLIENT_LIST", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.Contains(','))
                {
                    var parts = line.Split(',', StringSplitOptions.None);
                    var cn = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    var user = parts.Length > 9 ? parts[9].Trim() : string.Empty;
                    var cid = parts.Length > 10 ? parts[10].Trim() : string.Empty;
                    if (!string.IsNullOrWhiteSpace(cn) || !string.IsNullOrWhiteSpace(user) || !string.IsNullOrWhiteSpace(cid))
                    {
                        list.Add(new OpenVpnSession(cn, user, cid));
                    }
                    continue;
                }

                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var wsCn = fields.Length > 1 ? fields[1].Trim() : string.Empty;
                var wsUser = fields.Length > 9 ? fields[9].Trim() : string.Empty;
                var wsCid = fields.Length > 10 ? fields[10].Trim() : string.Empty;
                if (!string.IsNullOrWhiteSpace(wsCn) || !string.IsNullOrWhiteSpace(wsUser) || !string.IsNullOrWhiteSpace(wsCid))
                {
                    list.Add(new OpenVpnSession(wsCn, wsUser, wsCid));
                }
            }
            return list;
        }

        private static string NormalizeOpenVpnUsername(string? username, string? email)
        {
            var candidate = (username ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                var seed = (email ?? string.Empty).Trim().ToLowerInvariant();
                var safe = new string(seed.Where(char.IsAsciiLetterOrDigit).Take(18).ToArray());
                candidate = $"ovpn_{safe}";
            }

            candidate = candidate.Trim();
            return candidate.Length == 0 ? "ovpn_client" : candidate;
        }

        private async Task CleanupOrphanLocalGatewayProcessesAsync(string configPath, string processName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(processName))
            {
                return;
            }

            var command =
                "$killed=0; " +
                "$cfg='" + EscapePowerShellSingleQuoted(configPath) + "'; " +
                "$name='" + EscapePowerShellSingleQuoted(processName) + "'; " +
                "Get-CimInstance Win32_Process | " +
                "Where-Object { $_.Name -and $_.CommandLine -and $_.Name -ieq $name -and $_.CommandLine -like ('*' + $cfg + '*') } | " +
                "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; if ($?) { $killed++ } }; " +
                "Write-Output $killed";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            try
            {
                using var process = new Process { StartInfo = psi };
                if (!process.Start())
                {
                    return;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var stdout = (await stdoutTask).Trim();
                var stderr = (await stderrTask).Trim();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _log.Warn($"Relay '{_relay.Name}' orphan local runtime cleanup stderr: {stderr}");
                }

                if (int.TryParse(stdout, out var killed) && killed > 0)
                {
                    _log.Info($"Relay '{_relay.Name}' cleaned orphan local runtime processes. process={processName} killed={killed}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Relay '{_relay.Name}' failed orphan local runtime cleanup: {ex.Message}");
            }
        }

        private static bool TryValidateLocalListenPort(int port, out string reasonCode)
        {
            reasonCode = string.Empty;
            if (port <= 0 || port > 65535)
            {
                reasonCode = "invalid_port";
                return false;
            }

            try
            {
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                if (listeners.Any(x => x.Port == port))
                {
                    reasonCode = $"port_in_use_{port}";
                    return false;
                }
            }
            catch
            {
            }

            if (IsTcpPortExcludedByWindows(port))
            {
                reasonCode = $"port_excluded_by_windows_{port}";
                return false;
            }

            return true;
        }

        private static bool IsTcpPortExcludedByWindows(int port)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "interface ipv4 show excludedportrange protocol=tcp",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process is null)
                {
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return false;
                }

                foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || !char.IsDigit(trimmed[0]))
                    {
                        continue;
                    }

                    var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        continue;
                    }

                    if (int.TryParse(parts[0], out var start) &&
                        int.TryParse(parts[1], out var end) &&
                        port >= start &&
                        port <= end)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private void EnsureRelayLocalFirewallRule(int port, string protocol)
        {
            RemoveRelayLocalFirewallRule();
            var baseName = GetRelayLocalFirewallRuleName();
            RunFirewallCommand($@"advfirewall firewall add rule name=""{baseName} TCP"" dir=in action=allow protocol=TCP localport={port} profile=any");
            if (string.Equals(protocol, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
            {
                RunFirewallCommand($@"advfirewall firewall add rule name=""{baseName} UDP"" dir=in action=allow protocol=UDP localport={port} profile=any");
            }
        }

        private void RemoveRelayLocalFirewallRule()
        {
            var baseName = GetRelayLocalFirewallRuleName();
            RunFirewallCommand($@"advfirewall firewall delete rule name=""{baseName} TCP""");
            RunFirewallCommand($@"advfirewall firewall delete rule name=""{baseName} UDP""");
        }

        private string GetRelayLocalFirewallRuleName()
        {
            return $"OmniRelay Local Relay {_relay.Id}";
        }

        private void EnsureRelayOmniPanelFirewallRule(int panelPort)
        {
            RemoveRelayOmniPanelFirewallRule();
            RunFirewallCommand($@"advfirewall firewall add rule name=""{GetRelayOmniPanelFirewallRuleName()}"" dir=in action=allow protocol=TCP localport={panelPort} profile=any");
        }

        private void RemoveRelayOmniPanelFirewallRule()
        {
            RunFirewallCommand($@"advfirewall firewall delete rule name=""{GetRelayOmniPanelFirewallRuleName()}""");
        }

        private string GetRelayOmniPanelFirewallRuleName()
        {
            return $"OmniRelay OmniPanel {_relay.Id}";
        }

        private void RunFirewallCommand(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                process?.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                _log.Error($"Relay '{_relay.Name}' firewall command failed: netsh {args}", ex);
            }
        }

        private static async Task PumpStreamAsync(StreamReader reader, string filePath, CancellationToken cancellationToken)
        {
            var parent = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await using var writer = new StreamWriter(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                await writer.WriteLineAsync($"{DateTimeOffset.UtcNow:O} {line}");
                await writer.FlushAsync(cancellationToken);
            }
        }

        private void UpdateAdapterStatus()
        {
            NetworkAdapterCatalog.TryGetPrimaryIpv4(_relay.IncomingAdapterId, _relay.IncomingAdapterIfIndex, out _, out _);
            NetworkAdapterCatalog.TryGetPrimaryIpv4(_relay.OutgoingAdapterId, _relay.OutgoingAdapterIfIndex, out _, out _);
        }

        private void NormalizeScheduledTimes(DateTimeOffset now)
        {
            if (_nextRecoveryAllowedAtUtc - now > SchedulerSkewClamp)
            {
                _nextRecoveryAllowedAtUtc = now;
                RecordEvent("warn", "clock_shift_detected_reset_recovery_window");
            }

            if (_nextLocalProbeAtUtc - now > SchedulerSkewClamp)
            {
                _nextLocalProbeAtUtc = now;
            }

            if (_nextEndToEndProbeAtUtc - now > SchedulerSkewClamp)
            {
                _nextEndToEndProbeAtUtc = now;
            }
        }

        private static async Task<bool> IsLoopbackTcpListeningAsync(int port, CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RecordEvent(string level, string message)
        {
            var normalizedLevel = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim().ToLowerInvariant();
            var line = $"{DateTimeOffset.UtcNow:O} [{normalizedLevel}] {message}";
            _events.Enqueue(line);
            while (_events.Count > 40)
            {
                _events.Dequeue();
            }
        }

        private void RegisterTunnelExit()
        {
            var now = DateTimeOffset.UtcNow;
            _lastTunnelExitAtUtc = now;
            _recentTunnelExitTimestamps.Enqueue(now);
            TrimTunnelExitHistory(now);
        }

        private bool IsTunnelFlapping()
        {
            var now = DateTimeOffset.UtcNow;
            TrimTunnelExitHistory(now);

            if (_lastTunnelExitAtUtc.HasValue &&
                now - _lastTunnelExitAtUtc.Value <= TunnelRecentExitPenalty)
            {
                return true;
            }

            return _recentTunnelExitTimestamps.Count >= TunnelFlapThreshold;
        }

        private void TrimTunnelExitHistory(DateTimeOffset now)
        {
            while (_recentTunnelExitTimestamps.Count > 0 &&
                   now - _recentTunnelExitTimestamps.Peek() > TunnelFlapWindow)
            {
                _recentTunnelExitTimestamps.Dequeue();
            }
        }

        private void LogProbeFailure(string phase, string reasonCode, string detail)
        {
            var now = DateTimeOffset.UtcNow;
            var normalizedPhase = string.IsNullOrWhiteSpace(phase) ? "probe" : phase.Trim();
            var normalizedReason = string.IsNullOrWhiteSpace(reasonCode) ? "unknown" : reasonCode.Trim();
            var normalizedDetail = string.IsNullOrWhiteSpace(detail) ? "no detail" : detail.Trim();
            var signature = $"{normalizedPhase}|{normalizedReason}|{normalizedDetail}";
            var shouldLog = !string.Equals(_lastLoggedProbeFailureSignature, signature, StringComparison.Ordinal) ||
                            !_lastLoggedProbeFailureUtc.HasValue ||
                            now - _lastLoggedProbeFailureUtc.Value >= TimeSpan.FromSeconds(30);
            if (!shouldLog)
            {
                return;
            }

            _lastLoggedProbeFailureSignature = signature;
            _lastLoggedProbeFailureUtc = now;
            _log.Warn($"Relay '{_relay.Name}' {normalizedPhase} failed. reason={normalizedReason} detail={normalizedDetail}");
        }

        private static async Task<(bool Success, string Error)> ProbeSshReachabilityRouteOnlyAsync(
            string tunnelHost,
            int tunnelPort,
            CancellationToken cancellationToken)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                await client.ConnectAsync(tunnelHost, tunnelPort, cts.Token);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static async Task<bool> HasEstablishedSshSessionAsync(
            int processId,
            ServiceConfig config,
            CancellationToken cancellationToken)
        {
            var hostCandidates = await ResolveTunnelHostCandidatesAsync(config.TunnelHost, cancellationToken);
            var lines = await ReadNetstatTcpLinesAsync(cancellationToken);

            foreach (var line in lines)
            {
                var parts = SplitColumns(line);
                if (parts.Length < 5 ||
                    !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(parts[^1], out var pid) ||
                    pid != processId ||
                    !parts[^2].Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) ||
                    !TryParseEndpoint(parts[^3], out var remoteHost, out var remotePort) ||
                    remotePort != config.TunnelSshPort)
                {
                    continue;
                }

                if (HostMatchesCandidates(remoteHost, hostCandidates))
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task<IReadOnlyList<string>> ReadNetstatTcpLinesAsync(CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-ano");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("tcp");

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return [];
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            _ = await stderrTask;

            return stdout
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToArray();
        }

        private static string[] SplitColumns(string line)
        {
            return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static bool TryParseEndpoint(string token, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var value = token.Trim();
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                var end = value.LastIndexOf(']');
                if (end <= 1 || end + 2 >= value.Length || value[end + 1] != ':')
                {
                    return false;
                }

                host = value[1..end];
                return int.TryParse(value[(end + 2)..], out port);
            }

            var lastColon = value.LastIndexOf(':');
            if (lastColon <= 0 || lastColon >= value.Length - 1)
            {
                return false;
            }

            host = value[..lastColon];
            return int.TryParse(value[(lastColon + 1)..], out port);
        }

        private static async Task<HashSet<string>> ResolveTunnelHostCandidatesAsync(string host, CancellationToken cancellationToken)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = (host ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return set;
            }

            set.Add(normalized);
            if (IPAddress.TryParse(normalized, out var ip))
            {
                set.Add(ip.ToString());
                return set;
            }

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(normalized, cancellationToken);
                foreach (var address in addresses)
                {
                    set.Add(address.ToString());
                }
            }
            catch
            {
            }

            return set;
        }

        private static bool HostMatchesCandidates(string remoteHost, HashSet<string> candidates)
        {
            var normalizedRemote = (remoteHost ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedRemote))
            {
                return false;
            }

            if (candidates.Contains(normalizedRemote))
            {
                return true;
            }

            if (!IPAddress.TryParse(normalizedRemote, out var remoteIp))
            {
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (IPAddress.TryParse(candidate, out var candidateIp) && candidateIp.Equals(remoteIp))
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
                if (read <= 0)
                {
                    throw new IOException("read EOF");
                }

                offset += read;
            }
        }

        private static int ResolveRecoveryTier(int consecutiveFailures)
        {
            if (consecutiveFailures >= Tier3FailureThreshold)
            {
                return 3;
            }

            if (consecutiveFailures >= Tier2FailureThreshold)
            {
                return 2;
            }

            if (consecutiveFailures >= Tier1FailureThreshold)
            {
                return 1;
            }

            return 0;
        }

        private static TimeSpan GetRecoveryCooldown(int tier)
        {
            return tier switch
            {
                1 => TimeSpan.FromSeconds(15 + Random.Shared.Next(1, 5)),
                2 => TimeSpan.FromSeconds(30 + Random.Shared.Next(2, 8)),
                3 => TimeSpan.FromSeconds(60 + Random.Shared.Next(3, 12)),
                _ => TimeSpan.FromSeconds(10)
            };
        }

        private static TimeSpan GetForwardConflictCooldown(int conflictCount)
        {
            return conflictCount switch
            {
                <= 1 => TimeSpan.FromSeconds(15),
                2 => TimeSpan.FromSeconds(30),
                _ => TimeSpan.FromSeconds(45)
            };
        }

        private static bool HasRemoteForwardFailure(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            return error.Contains("remote port forwarding failed", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("forwarding failed", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("administratively prohibited", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetRemoteForwardConflictPort(string? error, out int port)
        {
            port = 0;
            if (string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            var match = Regex.Match(
                error,
                @"remote port forwarding failed for listen port\s+(\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return false;
            }

            return int.TryParse(match.Groups[1].Value, out port);
        }

        private static bool ShouldRestartLocalTunnelForRemoteFailure(string? reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
            {
                return false;
            }

            return reasonCode.Equals("backend_protocol_not_socks5", StringComparison.OrdinalIgnoreCase) ||
                   reasonCode.Equals("backend_protocol_unknown", StringComparison.OrdinalIgnoreCase) ||
                   reasonCode.Equals("backend_unreachable", StringComparison.OrdinalIgnoreCase) ||
                   reasonCode.Equals("backend_listener_down", StringComparison.OrdinalIgnoreCase) ||
                   reasonCode.Equals("backend_endpoint_unresponsive", StringComparison.OrdinalIgnoreCase) ||
                   reasonCode.Equals("remote_probe_timeout", StringComparison.OrdinalIgnoreCase);
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

        private static string EscapePowerShellSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
        }

        private string GetRelayScopedTunnelctlPath()
        {
            return $"/usr/local/sbin/omnirelay-tunnelctl-{GetSafeRelayPathToken()}";
        }

        private string GetRelayScopedTunnelctlConfigDirectory()
        {
            return $"/etc/omnirelay/relays/{GetSafeRelayPathToken()}/tunnelctl";
        }

        private string GetSafeRelayPathToken()
        {
            var raw = (_relay.Id ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "default";
            }

            var filtered = new string(raw.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
            return string.IsNullOrWhiteSpace(filtered) ? "default" : filtered;
        }

        private static string ShellSingleQuote(string value)
        {
            return "'" + EscapeForSingleQuotedShell(value) + "'";
        }

        private static string EscapeForSingleQuotedShell(string value)
        {
            return (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal);
        }

        private RelayStatus BuildStatus(bool enabled = true)
        {
            var policy = _runtime.GetRelayPolicySnapshot(_relay.Id);
            NetworkAdapterCatalog.TryGetPrimaryIpv4(_relay.IncomingAdapterId, _relay.IncomingAdapterIfIndex, out var incomingIp, out _);
            NetworkAdapterCatalog.TryGetPrimaryIpv4(_relay.OutgoingAdapterId, _relay.OutgoingAdapterIfIndex, out var outgoingIp, out _);
            var now = DateTimeOffset.UtcNow;
            return new RelayStatus
            {
                RelayId = _relay.Id,
                Name = _relay.Name,
                Enabled = enabled && _relay.Enabled,
                GatewayType = GatewayTypes.Normalize(_relay.GatewayType),
                StatusStale = false,
                TunnelState = _tunnelState,
                HealthState = _healthState,
                HealthReasonCode = _healthReasonCode,
                ConsecutiveFailures = _consecutiveFailures,
                RecoveryTier = _currentRecoveryTier,
                RecoveryAction = _recoveryAction,
                LastLocalProbeUtc = _lastLocalProbeUtc,
                LastEndToEndProbeUtc = _lastEndToEndProbeUtc,
                LastHealthyUtc = _lastHealthyUtc,
                LastStatusUpdateUtc = now,
                TunnelConnected = _tunnelConnected,
                TunnelLastConnectedAtUtc = _lastConnectedAtUtc,
                TunnelReconnectCount = _reconnectCount,
                TunnelLastError = _lastTunnelError,
                DataPlaneListening = _dataPlane.Running,
                BootstrapSocksListening = _bootstrapSocksListening,
                BootstrapSocksRemoteForwardActive = _tunnelConnected && _localProbeOk,
                BootstrapSocksLastError = _lastBootstrapError,
                IncomingAdapterIp = incomingIp?.ToString(),
                OutgoingAdapterIp = outgoingIp?.ToString(),
                WhitelistCount = policy.WhitelistEntries.Count,
                BlacklistCount = policy.BlacklistEntries.Count,
                LastError = _lastTunnelError ?? _lastBootstrapError,
                LocalGatewayState = string.Equals(GatewayTypes.Normalize(_relay.GatewayType), GatewayTypes.Local, StringComparison.OrdinalIgnoreCase)
                    ? _localGatewayState
                    : "inactive",
                LocalGatewayHealthReason = string.Equals(GatewayTypes.Normalize(_relay.GatewayType), GatewayTypes.Local, StringComparison.OrdinalIgnoreCase)
                    ? _localGatewayHealthReason
                    : null,
                LocalGatewayProtocol = LocalGatewayProtocols.Normalize(_relay.LocalGateway.Protocol),
                LocalGatewayPort = _relay.LocalGateway.Port,
                LocalGatewayClientsCount = string.Equals(GatewayTypes.Normalize(_relay.GatewayType), GatewayTypes.Local, StringComparison.OrdinalIgnoreCase)
                    ? _runtime.GetLocalGatewayClientsSnapshot(_relay.Id).Count
                    : 0,
                OmniPanelState = string.Equals(GatewayTypes.Normalize(_relay.GatewayType), GatewayTypes.Local, StringComparison.OrdinalIgnoreCase)
                    ? _omniPanelState
                    : (string.IsNullOrWhiteSpace(_relay.OmniPanel?.Domain) ? "not_configured" : "configured"),
                OmniPanelUrl = BuildOmniPanelUrl(),
                OmniPanelLastError = string.Equals(GatewayTypes.Normalize(_relay.GatewayType), GatewayTypes.Local, StringComparison.OrdinalIgnoreCase)
                    ? _omniPanelLastError
                    : (string.IsNullOrWhiteSpace(_relay.OmniPanel?.LastError) ? null : _relay.OmniPanel.LastError)
            };
        }

        private string BuildOmniPanelUrl()
        {
            var domain = (_relay.OmniPanel?.Domain ?? string.Empty).Trim();
            var host = !string.IsNullOrWhiteSpace(domain)
                ? domain
                : string.Equals(GatewayTypes.Normalize(_relay.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase)
                    ? (_relay.RemoteGateway?.TunnelHost ?? string.Empty).Trim()
                    : ResolveLocalPanelHost();
            if (string.IsNullOrWhiteSpace(host))
            {
                return string.Empty;
            }

            var port = _relay.OmniPanel?.Port is > 0 and <= 65535 ? _relay.OmniPanel.Port : 2054;
            var secure = (_relay.OmniPanel?.UseSsl ?? false) || (_relay.OmniPanel?.DomainOnly ?? false);
            return $"{(secure ? "https" : "http")}://{host}:{port}/panel";
        }

        private string ResolveLocalPanelHost()
        {
            var remoteAddress = (_relay.LocalGateway?.RemoteAddress ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(remoteAddress))
            {
                return remoteAddress;
            }

            if (NetworkAdapterCatalog.TryGetPrimaryIpv4(_relay.OutgoingAdapterId, _relay.OutgoingAdapterIfIndex, out var outgoingIp, out _) &&
                outgoingIp is not null)
            {
                return outgoingIp.ToString();
            }

            return "127.0.0.1";
        }

        private static ServiceConfig ToServiceConfig(RelayConfig relay)
        {
            return new ServiceConfig
            {
                GatewayType = GatewayTypes.Normalize(relay.GatewayType),
                LocalProxyListenPort = relay.DataPlaneLocalPort,
                BootstrapSocksLocalPort = relay.BootstrapSocksLocalPort,
                BootstrapSocksRemotePort = relay.BootstrapSocksRemotePort,
                WhitelistAdapterIfIndex = NetworkAdapterCatalog.TryResolveIfIndex(relay.IncomingAdapterId, relay.IncomingAdapterIfIndex, out var incomingIfIndex) ? incomingIfIndex : relay.IncomingAdapterIfIndex,
                DefaultAdapterIfIndex = NetworkAdapterCatalog.TryResolveIfIndex(relay.OutgoingAdapterId, relay.OutgoingAdapterIfIndex, out var outgoingIfIndex) ? outgoingIfIndex : relay.OutgoingAdapterIfIndex,
                TunnelHost = relay.RemoteGateway.TunnelHost,
                TunnelSshPort = relay.RemoteGateway.TunnelSshPort,
                TunnelRemotePort = relay.RemoteGateway.TunnelRemotePort,
                TunnelUser = relay.RemoteGateway.TunnelUser,
                TunnelAuthMethod = relay.RemoteGateway.TunnelAuthMethod,
                TunnelPrivateKeyPath = relay.RemoteGateway.TunnelPrivateKeyPath,
                TunnelPrivateKeyPassphrase = relay.RemoteGateway.TunnelPrivateKeyPassphrase,
                TunnelPassword = relay.RemoteGateway.TunnelPassword
            };
        }

        private static RelayConfig Clone(RelayConfig relay)
        {
            return new RelayConfig
            {
                Id = relay.Id,
                Name = relay.Name,
                GatewayType = relay.GatewayType,
                Enabled = relay.Enabled,
                IncomingAdapterId = relay.IncomingAdapterId ?? string.Empty,
                IncomingAdapterIfIndex = relay.IncomingAdapterIfIndex,
                OutgoingAdapterId = relay.OutgoingAdapterId ?? string.Empty,
                OutgoingAdapterIfIndex = relay.OutgoingAdapterIfIndex,
                DataPlaneLocalPort = relay.DataPlaneLocalPort,
                BootstrapSocksLocalPort = relay.BootstrapSocksLocalPort,
                BootstrapSocksRemotePort = relay.BootstrapSocksRemotePort,
                OmniPanel = new RelayOmniPanelConfig
                {
                    Port = relay.OmniPanel?.Port is > 0 and <= 65535
                        ? relay.OmniPanel.Port
                        : (relay.RemoteGateway?.PanelPort ?? 2054),
                    Username = relay.OmniPanel?.Username ?? relay.RemoteGateway?.PanelUser ?? string.Empty,
                    Password = relay.OmniPanel?.Password ?? relay.RemoteGateway?.PanelPassword ?? string.Empty,
                    Domain = relay.OmniPanel?.Domain ?? relay.RemoteGateway?.PanelDomain ?? string.Empty,
                    DomainOnly = relay.OmniPanel?.DomainOnly ?? relay.RemoteGateway?.PanelDomainOnly ?? false,
                    UseSsl = relay.OmniPanel?.UseSsl ?? relay.RemoteGateway?.PanelUseSsl ?? false,
                    SslMode = string.IsNullOrWhiteSpace(relay.OmniPanel?.SslMode)
                        ? (relay.RemoteGateway?.PanelSslMode ?? "letsencrypt")
                        : relay.OmniPanel.SslMode,
                    UploadedCertPath = relay.OmniPanel?.UploadedCertPath ?? relay.RemoteGateway?.PanelUploadedCertPath ?? string.Empty,
                    UploadedKeyPath = relay.OmniPanel?.UploadedKeyPath ?? relay.RemoteGateway?.PanelUploadedKeyPath ?? string.Empty,
                    PublicUrl = relay.OmniPanel?.PublicUrl ?? string.Empty,
                    LastError = relay.OmniPanel?.LastError ?? string.Empty
                },
                RemoteGateway = new RemoteGatewayConfig
                {
                    TunnelHost = relay.RemoteGateway?.TunnelHost ?? string.Empty,
                    TunnelSshPort = relay.RemoteGateway?.TunnelSshPort ?? 22,
                    TunnelRemotePort = relay.RemoteGateway?.TunnelRemotePort ?? 0,
                    TunnelUser = relay.RemoteGateway?.TunnelUser ?? "OmniRelay",
                    TunnelAuthMethod = TunnelAuthMethods.Normalize(relay.RemoteGateway?.TunnelAuthMethod),
                    TunnelPrivateKeyPath = relay.RemoteGateway?.TunnelPrivateKeyPath ?? string.Empty,
                    TunnelPrivateKeyPassphrase = relay.RemoteGateway?.TunnelPrivateKeyPassphrase ?? string.Empty,
                    TunnelPassword = relay.RemoteGateway?.TunnelPassword ?? string.Empty,
                    BootstrapMode = relay.RemoteGateway?.BootstrapMode ?? "tunnel",
                    Protocol = relay.RemoteGateway?.Protocol ?? "vless_tls_singbox",
                    PublicPort = relay.RemoteGateway?.PublicPort ?? 443,
                    PanelPort = relay.OmniPanel?.Port is > 0 and <= 65535 ? relay.OmniPanel.Port : (relay.RemoteGateway?.PanelPort ?? 2054),
                    PanelUser = relay.OmniPanel?.Username ?? relay.RemoteGateway?.PanelUser ?? string.Empty,
                    PanelPassword = relay.OmniPanel?.Password ?? relay.RemoteGateway?.PanelPassword ?? string.Empty,
                    PanelDomain = relay.OmniPanel?.Domain ?? relay.RemoteGateway?.PanelDomain ?? string.Empty,
                    PanelDomainOnly = relay.OmniPanel?.DomainOnly ?? relay.RemoteGateway?.PanelDomainOnly ?? false,
                    PanelUseSsl = relay.OmniPanel?.UseSsl ?? relay.RemoteGateway?.PanelUseSsl ?? false,
                    PanelSslMode = relay.OmniPanel?.SslMode ?? relay.RemoteGateway?.PanelSslMode ?? "letsencrypt",
                    PanelUploadedCertPath = relay.OmniPanel?.UploadedCertPath ?? relay.RemoteGateway?.PanelUploadedCertPath ?? string.Empty,
                    PanelUploadedKeyPath = relay.OmniPanel?.UploadedKeyPath ?? relay.RemoteGateway?.PanelUploadedKeyPath ?? string.Empty,
                    ShadowTlsCamouflageServer = relay.RemoteGateway?.ShadowTlsCamouflageServer ?? string.Empty,
                    OpenVpnNetwork = relay.RemoteGateway?.OpenVpnNetwork ?? "10.29.0.0/24",
                    OpenVpnSharedCaCertPath = relay.RemoteGateway?.OpenVpnSharedCaCertPath ?? string.Empty,
                    OpenVpnSharedClientCertPath = relay.RemoteGateway?.OpenVpnSharedClientCertPath ?? string.Empty,
                    OpenVpnSharedClientKeyPath = relay.RemoteGateway?.OpenVpnSharedClientKeyPath ?? string.Empty,
                    OpenVpnSharedTlsCryptKeyPath = relay.RemoteGateway?.OpenVpnSharedTlsCryptKeyPath ?? string.Empty,
                    DohEndpoints = relay.RemoteGateway?.DohEndpoints ?? string.Empty,
                },
                LocalGateway = new LocalGatewayConfig
                {
                    Protocol = LocalGatewayProtocols.Normalize(relay.LocalGateway?.Protocol),
                    Port = relay.LocalGateway?.Port ?? 443,
                    BindAddress = relay.LocalGateway?.BindAddress ?? "0.0.0.0",
                    RemoteAddress = relay.LocalGateway?.RemoteAddress ?? string.Empty,
                    Remark = relay.LocalGateway?.Remark ?? "OmniRelay Local Gateway",
                    RuntimeEnabled = relay.LocalGateway?.RuntimeEnabled ?? true
                }
            };
        }
    }
}
