using System.Diagnostics;
using EstherLink.Service.Runtime;

namespace EstherLink.Service.Workers;

public sealed class TunnelSupervisorWorker : BackgroundService
{
    private static readonly TimeSpan HealthyLoopDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorLoopDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StaleForwardCooldown = TimeSpan.FromSeconds(95);

    private readonly GatewayRuntime _runtime;
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<TunnelSupervisorWorker> _logger;

    private Process? _process;
    private int _reconnectCount;
    private DateTimeOffset? _lastConnectedAtUtc;
    private string? _lastError;
    private long _handledRestartVersion;

    public TunnelSupervisorWorker(
        GatewayRuntime runtime,
        FileLogWriter fileLog,
        ILogger<TunnelSupervisorWorker> logger)
    {
        _runtime = runtime;
        _fileLog = fileLog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = _runtime.GetConfigSnapshot();
                var requestedRestartVersion = _runtime.GetTunnelRestartRequestVersion();
                if (requestedRestartVersion > _handledRestartVersion)
                {
                    _handledRestartVersion = requestedRestartVersion;
                    _lastError = "Automatic tunnel recovery requested by SOCKS watchdog.";
                    _fileLog.Warn(_lastError);
                    await StopTunnelProcessAsync();
                }

                if (string.IsNullOrWhiteSpace(config.TunnelHost))
                {
                    await StopTunnelProcessAsync();
                    _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, "Tunnel host is not configured.");
                    _runtime.SetBootstrapSocksStatus(false, false, "Tunnel host is not configured.");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                if (_process is null || _process.HasExited)
                {
                    var retryDelay = GetRetryDelay(_reconnectCount, _lastError);
                    if (retryDelay > TimeSpan.Zero)
                    {
                        _fileLog.Info($"Tunnel restart backoff: waiting {retryDelay.TotalSeconds:F1}s. reason={_lastError ?? "none"}");
                        await Task.Delay(retryDelay, stoppingToken);
                    }

                    await StartTunnelProcessAsync(config, stoppingToken);
                }

                var connected = _process is { HasExited: false };
                var forwardActive = connected && !HasRemoteForwardFailure(_lastError);
                _runtime.SetTunnelStatus(connected, _lastConnectedAtUtc, _reconnectCount, _lastError);
                _runtime.SetBootstrapSocksStatus(
                    _runtime.GetStatusSnapshot().BootstrapSocksListening,
                    forwardActive,
                    forwardActive ? null : _lastError);
                await Task.Delay(HealthyLoopDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogError(ex, "Tunnel supervisor failure.");
                _fileLog.Error("Tunnel supervisor failure.", ex);
                _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, _lastError);
                _runtime.SetBootstrapSocksStatus(_runtime.GetStatusSnapshot().BootstrapSocksListening, false, _lastError);
                await Task.Delay(ErrorLoopDelay, stoppingToken);
            }
        }

        await StopTunnelProcessAsync();
        _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, "Tunnel worker stopped.");
        _runtime.SetBootstrapSocksStatus(_runtime.GetStatusSnapshot().BootstrapSocksListening, false, "Tunnel worker stopped.");
    }

    private async Task StartTunnelProcessAsync(EstherLink.Core.Configuration.ServiceConfig config, CancellationToken cancellationToken)
    {
        await StopTunnelProcessAsync();

        if (!SshTunnelProcessFactory.TryCreateReverseTunnelStartInfo(config, out var psi, out var error) || psi is null)
        {
            _lastError = error ?? "Tunnel configuration is invalid.";
            _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, _lastError);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return;
        }

        _process = Process.Start(psi);
        if (_process is null)
        {
            throw new InvalidOperationException("Failed to start ssh process.");
        }

        _process.StandardInput.Close();
        _reconnectCount++;
        _lastConnectedAtUtc = DateTimeOffset.UtcNow;
        _lastError = null;
        _fileLog.Info($"Tunnel process started. pid={_process.Id} reconnectCount={_reconnectCount}");

        var processRef = _process;
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = await processRef.StandardError.ReadToEndAsync(cancellationToken);
                await processRef.WaitForExitAsync(cancellationToken);

                var exitCode = processRef.ExitCode;
                var cleaned = string.IsNullOrWhiteSpace(stderr) ? string.Empty : stderr.Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    _lastError = cleaned;
                    _fileLog.Warn($"Tunnel process exited. exitCode={exitCode} error={cleaned}");
                }
                else if (exitCode != 0)
                {
                    _lastError = $"ssh exited with code {exitCode}.";
                    _fileLog.Warn($"Tunnel process exited. exitCode={exitCode}");
                }
            }
            catch
            {
            }
        }, cancellationToken);

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
        }
    }

    private static TimeSpan GetBackoffDelay(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt, 5)));
        var jitterMs = Random.Shared.Next(250, 1250);
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(jitterMs);
    }

    private static TimeSpan GetRetryDelay(int attempt, string? error)
    {
        if (HasRemoteForwardFailure(error))
        {
            // Automatic stale-session recovery: give sshd time to expire dead sessions that still hold remote listen ports.
            var jitterSeconds = Random.Shared.Next(2, 9);
            return StaleForwardCooldown + TimeSpan.FromSeconds(jitterSeconds);
        }

        return GetBackoffDelay(attempt);
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
}
