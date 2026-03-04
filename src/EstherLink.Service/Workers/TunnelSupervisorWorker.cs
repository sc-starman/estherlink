using System.Diagnostics;
using EstherLink.Service.Runtime;

namespace EstherLink.Service.Workers;

public sealed class TunnelSupervisorWorker : BackgroundService
{
    private readonly GatewayRuntime _runtime;
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<TunnelSupervisorWorker> _logger;

    private Process? _process;
    private int _reconnectCount;
    private DateTimeOffset? _lastConnectedAtUtc;
    private string? _lastError;

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
                if (!config.TunnelEnabled || string.IsNullOrWhiteSpace(config.TunnelHost))
                {
                    await StopTunnelProcessAsync();
                    _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, "Tunnel disabled.");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                if (_process is null || _process.HasExited)
                {
                    await StartTunnelProcessAsync(config, stoppingToken);
                }

                _runtime.SetTunnelStatus(_process is { HasExited: false }, _lastConnectedAtUtc, _reconnectCount, _lastError);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
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
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }

        await StopTunnelProcessAsync();
        _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, "Tunnel worker stopped.");
    }

    private async Task StartTunnelProcessAsync(EstherLink.Core.Configuration.ServiceConfig config, CancellationToken cancellationToken)
    {
        await StopTunnelProcessAsync();

        var args = BuildSshArguments(config);
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        _process = Process.Start(psi);
        if (_process is null)
        {
            throw new InvalidOperationException("Failed to start ssh process.");
        }

        _reconnectCount++;
        _lastConnectedAtUtc = DateTimeOffset.UtcNow;
        _lastError = null;
        _fileLog.Info($"Tunnel process started. pid={_process.Id} reconnectCount={_reconnectCount}");

        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = await _process.StandardError.ReadToEndAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _lastError = stderr.Trim();
                }
            }
            catch
            {
            }
        }, cancellationToken);

        await Task.Delay(GetBackoffDelay(_reconnectCount), cancellationToken);
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

    private static string BuildSshArguments(EstherLink.Core.Configuration.ServiceConfig config)
    {
        var args = new List<string>
        {
            "-NT",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=30",
            "-o", "ServerAliveCountMax=3",
            "-o", "TCPKeepAlive=yes"
        };

        if (!string.IsNullOrWhiteSpace(config.TunnelPrivateKeyPath))
        {
            args.Add("-i");
            args.Add($"\"{config.TunnelPrivateKeyPath}\"");
        }

        args.Add("-R");
        args.Add($"127.0.0.1:{config.TunnelRemotePort}:127.0.0.1:{config.LocalProxyListenPort}");
        args.Add($"{config.TunnelUser}@{config.TunnelHost}");
        args.Add("-p");
        args.Add(config.TunnelSshPort.ToString());

        return string.Join(' ', args);
    }

    private static TimeSpan GetBackoffDelay(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt, 5)));
        var jitterMs = Random.Shared.Next(250, 1250);
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(jitterMs);
    }
}
