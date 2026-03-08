using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OmniRelay.Core.Networking;
using OmniRelay.Service.Runtime;

namespace OmniRelay.Service.Workers;

public sealed class TunnelSupervisorWorker : BackgroundService
{
    private static readonly TimeSpan HealthyLoopDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorLoopDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StaleForwardCooldown = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan UnestablishedGracePeriod = TimeSpan.FromSeconds(15);

    private readonly GatewayRuntime _runtime;
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<TunnelSupervisorWorker> _logger;

    private Process? _process;
    private DateTimeOffset? _processStartedAtUtc;
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

                var licenseStatus = _runtime.GetStatusSnapshot();
                if (licenseStatus.LicenseCheckedAtUtc is not null && !licenseStatus.LicenseValid)
                {
                    var reason = string.IsNullOrWhiteSpace(licenseStatus.LicenseReason)
                        ? "License invalid."
                        : licenseStatus.LicenseReason;

                    await StopTunnelProcessAsync();
                    _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, reason);
                    _runtime.SetBootstrapSocksStatus(false, false, reason);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
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
                    // Avoid stale "connected/forwarded" UI state while waiting for restart backoff.
                    var snapshotBeforeRestart = _runtime.GetStatusSnapshot();
                    var exitReason = string.IsNullOrWhiteSpace(_lastError)
                        ? "Tunnel process is not running."
                        : _lastError;
                    _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, exitReason);
                    _runtime.SetBootstrapSocksStatus(snapshotBeforeRestart.BootstrapSocksListening, false, exitReason);

                    var retryDelay = GetRetryDelay(_reconnectCount, _lastError);
                    if (retryDelay > TimeSpan.Zero)
                    {
                        _fileLog.Info($"Tunnel restart backoff: waiting {retryDelay.TotalSeconds:F1}s. reason={_lastError ?? "none"}");
                        await Task.Delay(retryDelay, stoppingToken);
                    }

                    await StartTunnelProcessAsync(config, stoppingToken);
                }

                var connected = false;
                if (_process is { HasExited: false } runningProcess)
                {
                    connected = await HasEstablishedSshSessionAsync(runningProcess.Id, config, stoppingToken);
                    if (!connected)
                    {
                        var startedAtUtc = _processStartedAtUtc ?? DateTimeOffset.UtcNow;
                        if (DateTimeOffset.UtcNow - startedAtUtc >= UnestablishedGracePeriod)
                        {
                            _lastError = $"SSH process has no established session to {config.TunnelHost}:{config.TunnelSshPort}; restarting.";
                            _fileLog.Warn(_lastError);
                            await StopTunnelProcessAsync();
                        }
                    }
                    else
                    {
                        _lastConnectedAtUtc = DateTimeOffset.UtcNow;
                    }
                }

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

    private async Task StartTunnelProcessAsync(OmniRelay.Core.Configuration.ServiceConfig config, CancellationToken cancellationToken)
    {
        await StopTunnelProcessAsync();

        await CleanupOrphanTunnelProcessesAsync(config, cancellationToken);

        if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(config.WhitelistAdapterIfIndex, out var bindIp) || bindIp is null)
        {
            _lastError = $"IC1 adapter IfIndex={config.WhitelistAdapterIfIndex} has no usable IPv4 address.";
            _fileLog.Warn(_lastError);
            _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, _lastError);
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

        var (probeOk, probeError) = await ProbeSshReachabilityFromIc1Async(bindIp, config.TunnelHost, config.TunnelSshPort, cancellationToken);
        if (!probeOk)
        {
            _lastError = $"IC1 source {bindIp} cannot reach {config.TunnelHost}:{config.TunnelSshPort}. {probeError}";
            _fileLog.Warn(_lastError);
            _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, _lastError);
            _runtime.SetBootstrapSocksStatus(_runtime.GetStatusSnapshot().BootstrapSocksListening, false, _lastError);
            return;
        }

        _fileLog.Info($"Starting tunnel with IC1 IfIndex={config.WhitelistAdapterIfIndex}, sourceIp={bindIp}, target={config.TunnelHost}:{config.TunnelSshPort}.");

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
        _processStartedAtUtc = DateTimeOffset.UtcNow;
        _reconnectCount++;
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
                    if (HasRemoteForwardFailure(cleaned))
                    {
                        await TryScheduleRemoteForwardCleanupAsync(config, CancellationToken.None);
                    }
                    if (SshKnownHostsRepair.LooksLikeHostKeyMismatch(cleaned))
                    {
                        var repair = await SshKnownHostsRepair.TryRepairAsync(config, CancellationToken.None);
                        _fileLog.Warn($"Detected SSH host-key mismatch in tunnel worker. {repair.Message}");
                        if (repair.Success)
                        {
                            _reconnectCount = 0;
                            _lastError = "SSH host key changed; stale trust entry was repaired. Retrying tunnel connection.";
                        }
                    }
                }
                else if (exitCode != 0)
                {
                    _lastError = $"ssh exited with code {exitCode}.";
                    _fileLog.Warn($"Tunnel process exited. exitCode={exitCode}");
                }

                var snapshotAfterExit = _runtime.GetStatusSnapshot();
                var exitReason = string.IsNullOrWhiteSpace(_lastError)
                    ? "Tunnel process exited."
                    : _lastError;
                _runtime.SetTunnelStatus(false, _lastConnectedAtUtc, _reconnectCount, exitReason);
                _runtime.SetBootstrapSocksStatus(snapshotAfterExit.BootstrapSocksListening, false, exitReason);
            }
            catch
            {
            }
        }, cancellationToken);

    }

    private async Task TryScheduleRemoteForwardCleanupAsync(
        OmniRelay.Core.Configuration.ServiceConfig config,
        CancellationToken cancellationToken)
    {
        // Dedicated tunnel user only: clear stale user sessions that can keep remote -R listen ports occupied.
        var remoteCommand =
            "if command -v pkill >/dev/null 2>&1; then " +
            "u=" + ShellSingleQuote(config.TunnelUser.Trim()) + "; " +
            "nohup sh -lc 'sleep 1; pkill -KILL -u " + EscapeForSingleQuotedShell(config.TunnelUser.Trim()) + " >/dev/null 2>&1 || true' >/dev/null 2>&1 & " +
            "echo \"Remote tunnel session cleanup scheduled for user: $u\"; " +
            "else echo 'pkill is not available; skipping remote cleanup.'; fi";

        if (!SshTunnelProcessFactory.TryCreateRemoteCommandStartInfo(config, remoteCommand, out var psi, out var error) || psi is null)
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
            // Keep at least the original host token when DNS resolution fails.
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
