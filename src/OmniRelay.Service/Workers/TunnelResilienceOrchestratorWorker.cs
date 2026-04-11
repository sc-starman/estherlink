using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;
using OmniRelay.Service.Runtime;

namespace OmniRelay.Service.Workers;

public sealed class TunnelResilienceOrchestratorWorker : BackgroundService
{
    private static readonly TimeSpan LoopDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LicenseCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LocalProbeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EndToEndProbeInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UnestablishedGracePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RemoteProbeTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan TunnelFlapWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TunnelRecentExitPenalty = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan AdapterMissingRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SchedulerSkewClamp = TimeSpan.FromMinutes(2);

    private const int Tier1FailureThreshold = 1;
    private const int Tier2FailureThreshold = 3;
    private const int Tier3FailureThreshold = 6;
    private const int TunnelFlapThreshold = 3;

    private readonly GatewayRuntime _runtime;
    private readonly LicenseValidator _licenseValidator;
    private readonly HttpConnectProxyEngine _proxyEngine;
    private readonly Socks5BootstrapProxyEngine _socksEngine;
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<TunnelResilienceOrchestratorWorker> _logger;

    private readonly Queue<string> _events = new();
    private readonly Queue<DateTimeOffset> _recentTunnelExitTimestamps = new();

    private Process? _process;
    private DateTimeOffset? _processStartedAtUtc;
    private DateTimeOffset? _lastConnectedAtUtc;
    private DateTimeOffset? _lastLocalProbeUtc;
    private DateTimeOffset? _lastEndToEndProbeUtc;
    private DateTimeOffset? _lastHealthyUtc;
    private DateTimeOffset? _lastTunnelExitAtUtc;
    private DateTimeOffset _nextLocalProbeAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextEndToEndProbeAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextLicenseCheckAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRecoveryAllowedAtUtc = DateTimeOffset.MinValue;

    private int _consecutiveFailures;
    private int _currentRecoveryTier;
    private int _reconnectCount;
    private bool _localProbeOk;
    private bool _endToEndProbeOk;
    private bool _remoteProbeModuleAvailable = true;
    private bool _remoteProbeMissingLogged;
    private bool _sshSourceBindEnabled = false;
    private bool _bootstrapSocksListening;
    private bool _tunnelConnected;
    private string _tunnelState = "Disconnected";
    private string _healthState = "Disconnected";
    private string _backendProtocol = "unknown";
    private string? _healthReasonCode;
    private string? _recoveryAction;
    private string? _lastTunnelError;
    private string? _lastBootstrapError;
    private string? _lastLoggedProbeFailureSignature;
    private DateTimeOffset? _lastLoggedProbeFailureUtc;

    public TunnelResilienceOrchestratorWorker(
        GatewayRuntime runtime,
        LicenseValidator licenseValidator,
        HttpConnectProxyEngine proxyEngine,
        Socks5BootstrapProxyEngine socksEngine,
        FileLogWriter fileLog,
        ILogger<TunnelResilienceOrchestratorWorker> logger)
    {
        _runtime = runtime;
        _licenseValidator = licenseValidator;
        _proxyEngine = proxyEngine;
        _socksEngine = socksEngine;
        _fileLog = fileLog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RecordEvent("info", "tunnel resilience orchestrator started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = _runtime.GetConfigSnapshot();
                UpdateAdapterStatus(config);
                var now = DateTimeOffset.UtcNow;
                NormalizeScheduledTimes(now);
                if (string.Equals(GatewayTypes.Normalize(config.GatewayType), GatewayTypes.Local, StringComparison.OrdinalIgnoreCase))
                {
                    if (_process is not null || _bootstrapSocksListening || _runtime.GetStatusSnapshot().ProxyRunning)
                    {
                        await StopLocalRuntimeAsync(stoppingToken);
                    }

                    _consecutiveFailures = 0;
                    _currentRecoveryTier = 0;
                    _recoveryAction = null;
                    _tunnelConnected = false;
                    _bootstrapSocksListening = false;
                    _localProbeOk = true;
                    _endToEndProbeOk = true;
                    _tunnelState = "LocalMode";
                    _healthState = "Healthy";
                    _healthReasonCode = null;
                    _lastTunnelError = null;
                    _lastBootstrapError = null;
                    PublishStatus();
                    await Task.Delay(LoopDelay, stoppingToken);
                    continue;
                }

                if (!_runtime.IsProxyRequested())
                {
                    await StopLocalRuntimeAsync(stoppingToken);
                    SetDisconnected("proxy_not_requested", "Proxy is not requested.");
                    PublishStatus();
                    await Task.Delay(LoopDelay, stoppingToken);
                    continue;
                }

                if (!await EnsureValidLicenseAsync(config, stoppingToken))
                {
                    await StopLocalRuntimeAsync(stoppingToken);
                    SetDisconnected("license_invalid", "License is invalid.");
                    PublishStatus();
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                await _proxyEngine.EnsureRunningAsync(config.LocalProxyListenPort, stoppingToken);
                await _socksEngine.EnsureRunningAsync(config.BootstrapSocksLocalPort, stoppingToken);
                _runtime.SetProxyRunning(true, config.LocalProxyListenPort);
                _bootstrapSocksListening = await IsLoopbackTcpListeningAsync(config.BootstrapSocksLocalPort, stoppingToken);

                if (_process is null || _process.HasExited)
                {
                    if (DateTimeOffset.UtcNow >= _nextRecoveryAllowedAtUtc)
                    {
                        await StartTunnelProcessAsync(config, stoppingToken);
                    }
                }

                var probeCycleExecuted = false;
                if (now >= _nextLocalProbeAtUtc)
                {
                    _nextLocalProbeAtUtc = now.Add(LocalProbeInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(50, 450)));
                    probeCycleExecuted = true;
                    await RunLocalProbeAsync(config, stoppingToken);
                }

                if (now >= _nextEndToEndProbeAtUtc)
                {
                    _nextEndToEndProbeAtUtc = now.Add(EndToEndProbeInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(75, 700)));
                    probeCycleExecuted = true;
                    if (_localProbeOk)
                    {
                        await RunEndToEndProbeAsync(config, stoppingToken);
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
                            RecordEvent("info", "dual probe health restored");
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
                            await Task.Delay(LoopDelay, stoppingToken);
                            continue;
                        }

                        _consecutiveFailures++;
                        _healthState = "Degraded";
                        if (!string.Equals(_tunnelState, "RecoveringTier1", StringComparison.Ordinal) &&
                            !string.Equals(_tunnelState, "RecoveringTier2", StringComparison.Ordinal) &&
                            !string.Equals(_tunnelState, "RecoveringTier3", StringComparison.Ordinal))
                        {
                            _tunnelState = "Degraded";
                        }

                        await TryRecoverAsync(config, stoppingToken);
                    }
                }

                PublishStatus();
                await Task.Delay(LoopDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tunnel resilience orchestrator failure.");
                _fileLog.Error("Tunnel resilience orchestrator failure.", ex);
                _runtime.SetError(ex.Message);
                SetDegraded("orchestrator_exception", ex.Message);
                PublishStatus();
                await Task.Delay(ErrorDelay, stoppingToken);
            }
        }

        await StopTunnelProcessAsync();
        await StopLocalRuntimeAsync(CancellationToken.None);
        SetDisconnected("worker_stopped", "Tunnel resilience worker stopped.");
        PublishStatus();
    }

    private void PublishStatus()
    {
        var remoteForwardActive = _tunnelConnected && _localProbeOk;
        _runtime.SetResilienceStatus(
            _tunnelState,
            _healthState,
            _healthReasonCode,
            _consecutiveFailures,
            _currentRecoveryTier,
            _recoveryAction,
            _lastLocalProbeUtc,
            _lastEndToEndProbeUtc,
            _lastHealthyUtc,
            _tunnelConnected,
            _bootstrapSocksListening,
            remoteForwardActive,
            _lastTunnelError,
            _lastBootstrapError,
            _reconnectCount,
            _lastConnectedAtUtc,
            _events.ToArray());
    }

    private bool IsStartupGraceActive()
    {
        if (_process is null || _process.HasExited || _tunnelConnected || !_processStartedAtUtc.HasValue)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - _processStartedAtUtc.Value < UnestablishedGracePeriod;
    }

    private void SetDisconnected(string reasonCode, string reasonMessage)
    {
        _tunnelState = "Disconnected";
        _healthState = "Disconnected";
        _healthReasonCode = reasonCode;
        _localProbeOk = false;
        _endToEndProbeOk = false;
        _tunnelConnected = false;
        _recoveryAction = null;
        _lastTunnelError = reasonMessage;
        _lastBootstrapError = reasonMessage;
    }

    private void SetDegraded(string reasonCode, string reasonMessage)
    {
        _tunnelState = "Degraded";
        _healthState = "Degraded";
        _healthReasonCode = reasonCode;
        _lastTunnelError = reasonMessage;
        _lastBootstrapError = reasonMessage;
    }

    private async Task<bool> EnsureValidLicenseAsync(OmniRelay.Core.Configuration.ServiceConfig config, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextLicenseCheckAtUtc)
        {
            var status = _runtime.GetStatusSnapshot();
            if (status.LicenseCheckedAtUtc is null)
            {
                return true;
            }

            return status.LicenseValid;
        }

        var result = await _licenseValidator.ValidateAsync(
            config,
            forceOnline: false,
            transferRequested: false,
            cancellationToken: cancellationToken);
        _runtime.SetLicenseStatus(result);
        _nextLicenseCheckAtUtc = DateTimeOffset.UtcNow.Add(LicenseCheckInterval);

        if (!result.IsValid)
        {
            RecordEvent("warn", $"license check failed: {result.Reason ?? result.Error ?? "invalid"}");
            return false;
        }

        return true;
    }

    private async Task RunLocalProbeAsync(OmniRelay.Core.Configuration.ServiceConfig config, CancellationToken cancellationToken)
    {
        _lastLocalProbeUtc = DateTimeOffset.UtcNow;
        _tunnelConnected = false;

        if (_process is null || _process.HasExited)
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

        _tunnelConnected = await HasEstablishedSshSessionAsync(_process.Id, config, cancellationToken);
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
        _backendProtocol = backendProbe.Protocol;
        _localProbeOk = _tunnelConnected && backendProbe.Success;
        _bootstrapSocksListening = await IsLoopbackTcpListeningAsync(config.BootstrapSocksLocalPort, cancellationToken);

        if (!_localProbeOk)
        {
            _healthReasonCode = !_tunnelConnected ? "ssh_session_not_established" : backendProbe.ReasonCode;
            _lastTunnelError = !_tunnelConnected
                ? "SSH tunnel session is not established."
                : $"Local backend probe failed: {backendProbe.ReasonCode}";
            _lastBootstrapError = $"local_probe_failed:{_healthReasonCode}";
            LogProbeFailure(
                "local_probe",
                _healthReasonCode ?? "unknown",
                _lastTunnelError ?? "Local probe failed.");
            return;
        }

        _lastTunnelError = null;
        _lastBootstrapError = null;
    }

    private async Task RunEndToEndProbeAsync(OmniRelay.Core.Configuration.ServiceConfig config, CancellationToken cancellationToken)
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

    private async Task TryRecoverAsync(OmniRelay.Core.Configuration.ServiceConfig config, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextRecoveryAllowedAtUtc)
        {
            return;
        }

        if (string.Equals(_healthReasonCode, "ic1_adapter_missing_ipv4", StringComparison.OrdinalIgnoreCase))
        {
            _currentRecoveryTier = 0;
            _tunnelState = "Degraded";
            _recoveryAction = "awaiting_ic1_ipv4";
            _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(AdapterMissingRetryDelay);
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
            // If only end-to-end probe is failing while local tunnel path is healthy,
            // prefer preserving the local tunnel, but allow targeted restart for
            // persistent remote-backend failures (for example backend protocol unknown).
            if (!localPathFailure)
            {
                var requiresLocalRestart = ShouldRestartLocalTunnelForRemoteFailure(_healthReasonCode);
                switch (tier)
                {
                    case 1:
                        if (requiresLocalRestart && _remoteProbeModuleAvailable && _tunnelConnected)
                        {
                            RecordEvent("warn", "recovery tier1: remote backend unhealthy; running remote soft remediation");
                            _fileLog.Warn(
                                $"Recovery tier1: remote backend unhealthy ({_healthReasonCode ?? "unknown"}); running remote soft remediation.");
                            await RunRemoteWatchdogRemediationAsync(config, "soft", cancellationToken);
                            attemptedRecovery = true;
                        }

                        RecordEvent("warn", "recovery tier1: end-to-end probe failed; keeping local tunnel intact");
                        _fileLog.Warn("Recovery tier1: end-to-end probe failed; keeping local tunnel intact.");
                        _tunnelState = "Degraded";
                        _recoveryAction = null;
                        _currentRecoveryTier = 0;
                        break;
                    case 2:
                        if (requiresLocalRestart)
                        {
                            if (_remoteProbeModuleAvailable && _tunnelConnected)
                            {
                                RecordEvent("warn", "recovery tier2: remote backend unhealthy; running remote soft remediation");
                                _fileLog.Warn(
                                    $"Recovery tier2: remote backend unhealthy ({_healthReasonCode ?? "unknown"}); running remote soft remediation.");
                                await RunRemoteWatchdogRemediationAsync(config, "soft", cancellationToken);
                            }

                            RecordEvent("warn", "recovery tier2: remote backend unhealthy; restarting local tunnel");
                            _fileLog.Warn(
                                $"Recovery tier2: remote backend unhealthy ({_healthReasonCode ?? "unknown"}); restarting local tunnel.");
                            _runtime.RequestTunnelRestart("tier2_remote_backend_recovery");
                            await StopTunnelProcessAsync();
                            attemptedRecovery = true;
                            break;
                        }

                        RecordEvent("warn", "recovery tier2: end-to-end probe failed; keeping local tunnel intact");
                        _fileLog.Warn("Recovery tier2: end-to-end probe failed; keeping local tunnel intact.");
                        _tunnelState = "Degraded";
                        _recoveryAction = null;
                        _currentRecoveryTier = 0;
                        break;
                    default:
                        if (_remoteProbeModuleAvailable && _tunnelConnected)
                        {
                            RecordEvent("warn", "recovery tier3: end-to-end probe failed; running remote remediation only");
                            _fileLog.Warn("Recovery tier3: end-to-end probe failed; running remote remediation only.");
                            await RunRemoteWatchdogRemediationAsync(config, "hard", cancellationToken);
                            attemptedRecovery = true;
                        }
                        else
                        {
                            RecordEvent("warn", "recovery tier3: remote remediation skipped (module missing or ssh session not established)");
                            _fileLog.Warn("Recovery tier3: remote remediation skipped (module missing or ssh session not established).");
                        }

                        if (requiresLocalRestart)
                        {
                            RecordEvent("warn", "recovery tier3: remote backend remains unhealthy; restarting local tunnel");
                            _fileLog.Warn(
                                $"Recovery tier3: remote backend remains unhealthy ({_healthReasonCode ?? "unknown"}); restarting local tunnel.");
                            _runtime.RequestTunnelRestart("tier3_remote_backend_recovery");
                            await StopTunnelProcessAsync();
                            await CleanupOrphanTunnelProcessesAsync(config, cancellationToken);
                            attemptedRecovery = true;
                        }
                        break;
                }

                _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(GetRecoveryCooldown(tier));
                return;
            }

            switch (tier)
            {
                case 1:
                    RecordEvent("warn", "recovery tier1: restarting bootstrap SOCKS and tunnel");
                    _fileLog.Warn("Recovery tier1: restarting bootstrap SOCKS and tunnel.");
                    await _socksEngine.RestartAsync(config.BootstrapSocksLocalPort, cancellationToken);
                    _runtime.RequestTunnelRestart("tier1_recovery");
                    await StopTunnelProcessAsync();
                    attemptedRecovery = true;
                    break;
                case 2:
                    RecordEvent("warn", "recovery tier2: hard local cleanup");
                    _fileLog.Warn("Recovery tier2: hard local cleanup.");
                    await _proxyEngine.StopAsync(cancellationToken);
                    await _socksEngine.StopAsync(cancellationToken);
                    await StopTunnelProcessAsync();
                    await CleanupOrphanTunnelProcessesAsync(config, cancellationToken);
                    await _proxyEngine.EnsureRunningAsync(config.LocalProxyListenPort, cancellationToken);
                    await _socksEngine.EnsureRunningAsync(config.BootstrapSocksLocalPort, cancellationToken);
                    attemptedRecovery = true;
                    break;
                default:
                    RecordEvent("warn", "recovery tier3: hard local cleanup");
                    _fileLog.Warn("Recovery tier3: hard local cleanup.");
                    if (_remoteProbeModuleAvailable && _tunnelConnected)
                    {
                        RecordEvent("warn", "recovery tier3: running remote remediation before local cleanup");
                        _fileLog.Warn("Recovery tier3: running remote remediation before local cleanup.");
                        await RunRemoteWatchdogRemediationAsync(config, "hard", cancellationToken);
                    }
                    else
                    {
                        RecordEvent("warn", "recovery tier3: skipping remote remediation (module missing or ssh session not established)");
                        _fileLog.Warn("Recovery tier3: skipping remote remediation (module missing or ssh session not established).");
                    }
                    await _proxyEngine.StopAsync(cancellationToken);
                    await _socksEngine.StopAsync(cancellationToken);
                    await StopTunnelProcessAsync();
                    await CleanupOrphanTunnelProcessesAsync(config, cancellationToken);
                    await _proxyEngine.EnsureRunningAsync(config.LocalProxyListenPort, cancellationToken);
                    await _socksEngine.EnsureRunningAsync(config.BootstrapSocksLocalPort, cancellationToken);
                    attemptedRecovery = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            _runtime.SetError(ex.Message);
            _lastTunnelError = ex.Message;
            RecordEvent("error", $"recovery tier{tier} failed: {ex.Message}");
            _fileLog.Error($"Recovery tier{tier} failed.", ex);
        }

        if (attemptedRecovery && !string.Equals(_healthState, "Healthy", StringComparison.Ordinal))
        {
            _tunnelState = "Degraded";
            _recoveryAction = null;
            _currentRecoveryTier = 0;
        }

        _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(GetRecoveryCooldown(tier));
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

    private async Task<(bool Success, string Protocol, string ReasonCode)> ProbeBackendEndpointAsync(int port, CancellationToken cancellationToken)
    {
        if (!await IsLoopbackTcpListeningAsync(port, cancellationToken))
        {
            return (false, "unknown", "backend_listener_down");
        }

        if (await ProbeHttpConnectEndpointAsync(port, cancellationToken))
        {
            return (true, "http-connect", string.Empty);
        }

        if (await ProbeSocks5EndpointAsync(port, cancellationToken))
        {
            return (true, "socks5", string.Empty);
        }

        return (false, "unknown", "backend_protocol_unknown");
    }

    private static async Task<bool> ProbeSocks5EndpointAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token);
            using var stream = client.GetStream();
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

    private static async Task<bool> ProbeHttpConnectEndpointAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token);
            using var stream = client.GetStream();
            // Use method/protocol validation only, independent from egress reachability.
            const string request = "GET / HTTP/1.1\r\nHost: omnirelay-probe.local\r\n\r\n";
            var requestBytes = System.Text.Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, timeoutCts.Token);

            var response = new byte[64];
            var offset = 0;
            while (offset < response.Length)
            {
                var read = await stream.ReadAsync(response.AsMemory(offset, response.Length - offset), timeoutCts.Token);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
                if (offset >= 7)
                {
                    break;
                }
            }

            if (offset <= 0)
            {
                return false;
            }

            var text = System.Text.Encoding.ASCII.GetString(response, 0, offset);
            return text.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<(bool Success, string ReasonCode, string Message)> RunRemoteWatchdogProbeAsync(
        OmniRelay.Core.Configuration.ServiceConfig config,
        CancellationToken cancellationToken)
    {
        var (ok, stdout, stderr, error) = await ExecuteRemoteGatewayctlCommandAsync(
            config,
            $"/usr/bin/env bash /usr/local/sbin/omnirelay-tunnelctl probe --backend-host 127.0.0.1 --backend-port {config.TunnelRemotePort} --json",
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
        OmniRelay.Core.Configuration.ServiceConfig config,
        string level,
        CancellationToken cancellationToken)
    {
        var command = $"/usr/bin/env bash /usr/local/sbin/omnirelay-tunnelctl remediate --level {level} --backend-host 127.0.0.1 --backend-port {config.TunnelRemotePort} --json";
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

    private async Task<(bool Success, string Stdout, string Stderr, string? Error)> ExecuteRemoteGatewayctlCommandAsync(
        OmniRelay.Core.Configuration.ServiceConfig config,
        string remoteCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!SshTunnelProcessFactory.TryCreateRemoteCommandStartInfo(
                config,
                remoteCommand,
                out var startInfo,
                out var createError,
                includeSourceBind: _sshSourceBindEnabled) || startInfo is null)
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

                if (_sshSourceBindEnabled && IsSshSourceBindFailure(message))
                {
                    _sshSourceBindEnabled = false;
                    _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow;
                    _fileLog.Warn("Detected SSH source-bind failure during remote command; retrying without -b source binding.");
                    RecordEvent("warn", "ssh source-bind failed for remote command; switching to route-only mode");

                    return await ExecuteRemoteGatewayctlCommandWithoutBindAsync(
                        config,
                        remoteCommand,
                        timeout,
                        cancellationToken);
                }

                return (false, stdout, stderr, message);
            }

            return (true, stdout, stderr, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, string.Empty, string.Empty, $"remote command timed out after {timeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, string.Empty, ex.Message);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private static async Task<(bool Success, string Stdout, string Stderr, string? Error)> ExecuteRemoteGatewayctlCommandWithoutBindAsync(
        OmniRelay.Core.Configuration.ServiceConfig config,
        string remoteCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!SshTunnelProcessFactory.TryCreateRemoteCommandStartInfo(
                config,
                remoteCommand,
                out var startInfo,
                out var createError,
                includeSourceBind: false) || startInfo is null)
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
                return (false, stdout, stderr, string.IsNullOrWhiteSpace(detail)
                    ? $"ssh exited with code {process.ExitCode}"
                    : $"ssh exited with code {process.ExitCode}: {detail}");
            }

            return (true, stdout, stderr, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, string.Empty, string.Empty, $"remote command timed out after {timeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, string.Empty, ex.Message);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private async Task StartTunnelProcessAsync(OmniRelay.Core.Configuration.ServiceConfig config, CancellationToken cancellationToken)
    {
        await StopTunnelProcessAsync();
        await CleanupOrphanTunnelProcessesAsync(config, cancellationToken);

        if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(config.WhitelistAdapterIfIndex, out var bindIp) || bindIp is null)
        {
            _lastTunnelError = $"IC1 adapter IfIndex={config.WhitelistAdapterIfIndex} has no usable IPv4 address.";
            _healthReasonCode = "ic1_adapter_missing_ipv4";
            _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(AdapterMissingRetryDelay);
            RecordEvent("warn", _lastTunnelError);
            return;
        }

        var routeResult = await NetworkRouteManager.TryEnsureTunnelHostRouteAsync(config, cancellationToken);
        if (routeResult.Success)
        {
            _fileLog.Info(routeResult.Message);
        }
        else
        {
            _fileLog.Warn(routeResult.Message);
        }

        if (_sshSourceBindEnabled)
        {
            var (boundProbeOk, boundProbeError) = await ProbeSshReachabilityFromIc1Async(
                bindIp,
                config.TunnelHost,
                config.TunnelSshPort,
                cancellationToken);

            if (!boundProbeOk)
            {
                if (IsSshSourceBindFailure(boundProbeError))
                {
                    _sshSourceBindEnabled = false;
                    _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow;
                    var bindFailureMessage =
                        $"IC1 bound reachability probe failed from {bindIp} to {config.TunnelHost}:{config.TunnelSshPort} ({boundProbeError}); switching to route-only mode.";
                    _fileLog.Warn(bindFailureMessage);
                    RecordEvent("warn", "ssh reachability source-bind failed; falling back to route-only connect");
                }
                else
                {
                    _lastTunnelError = $"IC1 source {bindIp} cannot reach {config.TunnelHost}:{config.TunnelSshPort}. {boundProbeError}";
                    _healthReasonCode = "ssh_reachability_failed";
                    RecordEvent("warn", _lastTunnelError);
                    _fileLog.Warn(_lastTunnelError);
                    return;
                }
            }
        }

        if (!_sshSourceBindEnabled)
        {
            var (routeProbeOk, routeProbeError) = await ProbeSshReachabilityRouteOnlyAsync(
                config.TunnelHost,
                config.TunnelSshPort,
                cancellationToken);

            if (!routeProbeOk)
            {
                _lastTunnelError = $"Route-only probe cannot reach {config.TunnelHost}:{config.TunnelSshPort}. {routeProbeError}";
                _healthReasonCode = "ssh_reachability_failed";
                RecordEvent("warn", _lastTunnelError);
                _fileLog.Warn(_lastTunnelError);
                return;
            }
        }

        _fileLog.Info(
            $"Starting tunnel with IC1 IfIndex={config.WhitelistAdapterIfIndex}, sourceIp={bindIp}, target={config.TunnelHost}:{config.TunnelSshPort}.");

        if (!SshTunnelProcessFactory.TryCreateReverseTunnelStartInfo(
                config,
                out var processInfo,
                out var error,
                includeSourceBind: _sshSourceBindEnabled) || processInfo is null)
        {
            _lastTunnelError = error ?? "Tunnel configuration is invalid.";
            _healthReasonCode = "ssh_start_info_invalid";
            RecordEvent("error", _lastTunnelError);
            return;
        }

        _process = Process.Start(processInfo);
        if (_process is null)
        {
            _lastTunnelError = "Failed to start ssh process.";
            _healthReasonCode = "ssh_process_start_failed";
            RecordEvent("error", _lastTunnelError);
            return;
        }

        _process.StandardInput.Close();
        _processStartedAtUtc = DateTimeOffset.UtcNow;
        _reconnectCount++;
        _lastTunnelError = null;
        _healthReasonCode = null;
        _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow;
        RecordEvent("info", $"tunnel process started pid={_process.Id} reconnectCount={_reconnectCount}");
        _fileLog.Info($"Tunnel process started. pid={_process.Id} reconnectCount={_reconnectCount}");

        var processRef = _process;
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
                    _fileLog.Warn($"Tunnel process exited. exitCode={exitCode} error={cleaned}");
                    RecordEvent("warn", $"ssh process exited: {cleaned}");

                    if (TryGetRemoteForwardConflictPort(cleaned, out var conflictPort))
                    {
                        var portText = conflictPort > 0 ? conflictPort.ToString() : config.TunnelRemotePort.ToString();
                        var conflictMessage =
                            $"Gateway remote-forward port {portText} is already in use by another SSH session/relay. " +
                            "Stop the other relay/session or use a different Tunnel Remote Port.";
                        _healthReasonCode = "remote_forward_port_in_use";
                        _lastTunnelError = conflictMessage;
                        _fileLog.Warn(conflictMessage);
                        RecordEvent("warn", conflictMessage);
                        _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(45));
                    }

                    if (_sshSourceBindEnabled && IsSshSourceBindFailure(cleaned))
                    {
                        _sshSourceBindEnabled = false;
                        _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow;
                        _fileLog.Warn("Detected SSH source-bind failure; retrying without -b source binding.");
                        RecordEvent("warn", "ssh source-bind failed; falling back to route-only tunnel connect");
                    }

                    if (HasRemoteForwardFailure(cleaned) && !TryGetRemoteForwardConflictPort(cleaned, out _))
                    {
                        await TryScheduleRemoteForwardCleanupAsync(config, CancellationToken.None);
                    }

                    if (SshKnownHostsRepair.LooksLikeHostKeyMismatch(cleaned))
                    {
                        var repair = await SshKnownHostsRepair.TryRepairAsync(config, CancellationToken.None);
                        _fileLog.Warn($"Detected SSH host-key mismatch in tunnel worker. {repair.Message}");
                        RecordEvent("warn", $"ssh known_hosts repair: {repair.Message}");
                        if (repair.Success)
                        {
                            _lastTunnelError = "SSH host key changed; stale trust entry was repaired. Retrying tunnel connection.";
                            _healthReasonCode = "ssh_known_hosts_repaired";
                            _nextRecoveryAllowedAtUtc = DateTimeOffset.UtcNow;
                        }
                    }
                }
                else if (exitCode != 0)
                {
                    var detail = FirstNonEmpty(
                        string.IsNullOrWhiteSpace(stdout) ? null : stdout.Trim(),
                        string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim());
                    _lastTunnelError = string.IsNullOrWhiteSpace(detail)
                        ? $"ssh exited with code {exitCode}."
                        : $"ssh exited with code {exitCode}: {detail}";
                    _healthReasonCode = "ssh_process_exited";
                    _fileLog.Warn(_lastTunnelError);
                    RecordEvent("warn", _lastTunnelError);
                }
            }
            catch
            {
            }
        }, CancellationToken.None);
    }

    private async Task StopTunnelProcessAsync()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _processStartedAtUtc = null;
            _tunnelConnected = false;
        }
    }

    private async Task StopLocalRuntimeAsync(CancellationToken cancellationToken)
    {
        _runtime.SetProxyRunning(false, _runtime.GetConfigSnapshot().LocalProxyListenPort);
        await _proxyEngine.StopAsync(cancellationToken);
        await _socksEngine.StopAsync(cancellationToken);
        await StopTunnelProcessAsync();
        _bootstrapSocksListening = false;
    }

    private void UpdateAdapterStatus(OmniRelay.Core.Configuration.ServiceConfig config)
    {
        NetworkAdapterCatalog.TryGetPrimaryIpv4(config.WhitelistAdapterIfIndex, out var whitelistIp);
        NetworkAdapterCatalog.TryGetPrimaryIpv4(config.DefaultAdapterIfIndex, out var defaultIp);
        _runtime.SetAdapterIps(whitelistIp?.ToString(), defaultIp?.ToString());
    }

    private void NormalizeScheduledTimes(DateTimeOffset now)
    {
        if (_nextRecoveryAllowedAtUtc - now > SchedulerSkewClamp)
        {
            _nextRecoveryAllowedAtUtc = now;
            RecordEvent("warn", "clock_shift_detected_reset_recovery_window");
            _fileLog.Warn("Detected large clock shift; reset tunnel recovery cooldown window.");
        }

        if (_nextLocalProbeAtUtc - now > SchedulerSkewClamp)
        {
            _nextLocalProbeAtUtc = now;
        }

        if (_nextEndToEndProbeAtUtc - now > SchedulerSkewClamp)
        {
            _nextEndToEndProbeAtUtc = now;
        }

        if (_nextLicenseCheckAtUtc - now > SchedulerSkewClamp)
        {
            _nextLicenseCheckAtUtc = now;
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

    private async Task CleanupOrphanTunnelProcessesAsync(
        OmniRelay.Core.Configuration.ServiceConfig config,
        CancellationToken cancellationToken)
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
            "  (" +
            "    $_.CommandLine -like ('*' + $forwardA + '*') -or " +
            "    $_.CommandLine -like ('*' + $forwardB + '*') -or " +
            "    $_.CommandLine -like ('*' + $forwardALegacy + '*') -or " +
            "    $_.CommandLine -like ('*' + $forwardBLegacy + '*') -or " +
            "    (" +
            "      $_.CommandLine -like '* -R *' -and " +
            "      $_.CommandLine -like ('*' + $targetUser + '@' + $targetHost + '*')" +
            "    )" +
            "  )" +
            "} | " +
            "ForEach-Object { $matched += $_.ProcessId; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; if ($?) { $killed += $_.ProcessId } }; " +
            "Write-Output ('Stale ssh cleanup: matched=' + $matched.Count + ', killed=' + $killed.Count); " +
            "if ($killed.Count -gt 0) { Write-Output ('Killed stale tunnel ssh process IDs: ' + ($killed -join ', ')) }";

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
                _fileLog.Info(stdout);
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _fileLog.Warn($"Stale tunnel cleanup stderr: {stderr}");
            }
        }
        catch (Exception ex)
        {
            _fileLog.Warn($"Failed to cleanup stale tunnel ssh processes: {ex.Message}");
        }
    }

    private async Task TryScheduleRemoteForwardCleanupAsync(
        OmniRelay.Core.Configuration.ServiceConfig config,
        CancellationToken cancellationToken)
    {
        if (config.TunnelRemotePort <= 0 || config.BootstrapSocksRemotePort <= 0)
        {
            return;
        }

        // Clean only stale listener owners for the forwarded ports.
        var ports = $"{config.TunnelRemotePort} {config.BootstrapSocksRemotePort}";
        var remoteCommand =
            "ports=" + ShellSingleQuote(ports) + "; " +
            "killed=0; " +
            "for p in $ports; do " +
            "for pid in $(ss -lntp \"( sport = :$p )\" 2>/dev/null | sed -n \"s/.*pid=\\([0-9]\\+\\).*/\\1/p\" | sort -u); do " +
            "kill -KILL \"$pid\" >/dev/null 2>&1 && killed=$((killed+1)) || true; " +
            "done; " +
            "done; " +
            "echo \"Remote forward listener cleanup attempted for ports: $ports; killed=$killed\"";

        if (!SshTunnelProcessFactory.TryCreateRemoteCommandStartInfo(
                config,
                remoteCommand,
                out var psi,
                out var error,
                includeSourceBind: _sshSourceBindEnabled) || psi is null)
        {
            _fileLog.Warn($"Unable to schedule remote tunnel cleanup: {error ?? "cannot prepare ssh command."}");
            return;
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                _fileLog.Warn("Unable to start ssh process for remote tunnel cleanup.");
                return;
            }

            process.StandardInput.Close();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                _fileLog.Info(stdout);
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _fileLog.Warn($"Remote tunnel cleanup stderr: {stderr}");
            }
        }
        catch (Exception ex)
        {
            _fileLog.Warn($"Remote tunnel cleanup request failed: {ex.Message}");
        }
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

    private static bool IsSshSourceBindFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("Invalid argument", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot assign requested address", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("address not available", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
    }

    private static string ShellSingleQuote(string value)
    {
        return "'" + EscapeForSingleQuotedShell(value) + "'";
    }

    private static string EscapeForSingleQuotedShell(string value)
    {
        return (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private static bool ShouldRestartLocalTunnelForRemoteFailure(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return false;
        }

        return reasonCode.Equals("backend_protocol_unknown", StringComparison.OrdinalIgnoreCase) ||
               reasonCode.Equals("backend_unreachable", StringComparison.OrdinalIgnoreCase) ||
               reasonCode.Equals("backend_listener_down", StringComparison.OrdinalIgnoreCase) ||
               reasonCode.Equals("backend_endpoint_unresponsive", StringComparison.OrdinalIgnoreCase) ||
               reasonCode.Equals("remote_probe_timeout", StringComparison.OrdinalIgnoreCase);
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
        _fileLog.Warn($"{normalizedPhase} failed. reason={normalizedReason} detail={normalizedDetail}");
    }

    private static async Task<(bool Success, string Error)> ProbeSshReachabilityFromIc1Async(
        IPAddress sourceIp,
        string tunnelHost,
        int tunnelPort,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            client.Client.Bind(new IPEndPoint(sourceIp, 0));
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
        OmniRelay.Core.Configuration.ServiceConfig config,
        CancellationToken cancellationToken)
    {
        var hostCandidates = await ResolveTunnelHostCandidatesAsync(config.TunnelHost, cancellationToken);
        var lines = await ReadNetstatTcpLinesAsync(cancellationToken);

        foreach (var line in lines)
        {
            var parts = SplitColumns(line);
            if (parts.Length < 5)
            {
                continue;
            }

            if (!parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(parts[^1], out var pid) || pid != processId)
            {
                continue;
            }

            var state = parts[^2];
            if (!state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remoteToken = parts[^3];
            if (!TryParseEndpoint(remoteToken, out var remoteHost, out var remotePort))
            {
                continue;
            }

            if (remotePort != config.TunnelSshPort)
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
        return line
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
}
