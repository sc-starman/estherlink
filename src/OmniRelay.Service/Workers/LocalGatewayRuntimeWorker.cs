using System.Diagnostics;
using System.Net;
using System.Text.Json;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;
using OmniRelay.Service.Runtime;

namespace OmniRelay.Service.Workers;

public sealed class LocalGatewayRuntimeWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly GatewayRuntime _runtime;
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<LocalGatewayRuntimeWorker> _logger;
    private Process? _process;
    private bool _lastStartWasAutoRecovered;
    private long _lastObservedRequestVersion;

    public LocalGatewayRuntimeWorker(
        GatewayRuntime runtime,
        FileLogWriter fileLog,
        ILogger<LocalGatewayRuntimeWorker> logger)
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
                var gatewayType = GatewayTypes.Normalize(config.GatewayType);
                var currentVersion = _runtime.GetLocalGatewayRequestVersion();
                var restartRequested = _runtime.ConsumeLocalGatewayRestartRequested();

                if (!string.Equals(gatewayType, GatewayTypes.Local, StringComparison.OrdinalIgnoreCase))
                {
                    await EnsureStoppedAsync(removeFirewallRule: true, stoppingToken);
                    _runtime.SetLocalGatewayStatus("inactive", "gateway_type_remote", false);
                    _lastObservedRequestVersion = currentVersion;
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                var protocol = LocalGatewayProtocols.Normalize(config.LocalGateway.Protocol);
                if (!LocalGatewayProtocols.IsSupportedInV1(protocol))
                {
                    await EnsureStoppedAsync(removeFirewallRule: true, stoppingToken);
                    _runtime.SetLocalGatewayStatus("unsupported", "protocol_not_supported_v1", false);
                    _lastObservedRequestVersion = currentVersion;
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                if (!_runtime.IsLocalGatewayRequested() || !config.LocalGateway.RuntimeEnabled)
                {
                    await EnsureStoppedAsync(removeFirewallRule: true, stoppingToken);
                    _runtime.SetLocalGatewayStatus("inactive", "runtime_disabled", false);
                    _lastObservedRequestVersion = currentVersion;
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(config.DefaultAdapterIfIndex, out var ic2Ip) || ic2Ip is null)
                {
                    await EnsureStoppedAsync(removeFirewallRule: true, stoppingToken);
                    _runtime.SetLocalGatewayStatus("unhealthy", "ic2_adapter_unavailable", false);
                    _lastObservedRequestVersion = currentVersion;
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                var binaryPath = ServicePaths.ResolveXrayExecutablePath();
                if (!File.Exists(binaryPath))
                {
                    await EnsureStoppedAsync(removeFirewallRule: true, stoppingToken);
                    _runtime.SetLocalGatewayStatus("unhealthy", "xray_binary_missing", false);
                    _lastObservedRequestVersion = currentVersion;
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                var needRestart = restartRequested || currentVersion != _lastObservedRequestVersion;
                if (_process is null || _process.HasExited || needRestart)
                {
                    if (_process is not null && _process.HasExited)
                    {
                        _lastStartWasAutoRecovered = true;
                    }

                    await RestartProcessAsync(config, protocol, ic2Ip, binaryPath, stoppingToken);
                    _lastObservedRequestVersion = currentVersion;
                }
                else
                {
                    _runtime.SetLocalGatewayStatus("active", null, _lastStartWasAutoRecovered);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local gateway runtime worker failure.");
                _fileLog.Error("Local gateway runtime worker failure.", ex);
                _runtime.SetLocalGatewayStatus("unhealthy", ex.Message, false);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        await EnsureStoppedAsync(removeFirewallRule: true, CancellationToken.None);
        _runtime.SetLocalGatewayStatus("inactive", "worker_stopped", false);
    }

    private async Task RestartProcessAsync(
        ServiceConfig config,
        string protocol,
        IPAddress ic2Ip,
        string binaryPath,
        CancellationToken cancellationToken)
    {
        await EnsureStoppedAsync(removeFirewallRule: false, cancellationToken);

        var clients = _runtime.GetLocalGatewayClientsSnapshot()
            .Where(x => x.Enabled &&
                        string.Equals(LocalGatewayProtocols.Normalize(x.Protocol), protocol, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        WriteXrayConfig(config.LocalGateway, protocol, clients, ic2Ip);
        EnsureFirewallRule(config.LocalGateway.Port, protocol);

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(ServicePaths.LocalGatewayXrayConfigPath);

        _process = Process.Start(startInfo);
        if (_process is null)
        {
            _runtime.SetLocalGatewayStatus("unhealthy", "xray_start_failed", false);
            return;
        }

        _ = PumpStreamAsync(_process.StandardOutput, ServicePaths.LocalGatewayXrayStdoutLogPath, cancellationToken);
        _ = PumpStreamAsync(_process.StandardError, ServicePaths.LocalGatewayXrayStderrLogPath, cancellationToken);

        await Task.Delay(700, cancellationToken);
        if (_process.HasExited)
        {
            _runtime.SetLocalGatewayStatus("unhealthy", $"xray_exited_{_process.ExitCode}", false);
            return;
        }

        _runtime.SetLocalGatewayStatus("active", null, _lastStartWasAutoRecovered);
        _lastStartWasAutoRecovered = false;
    }

    private void WriteXrayConfig(
        LocalGatewayConfig config,
        string protocol,
        IReadOnlyList<LocalGatewayClient> clients,
        IPAddress ic2Ip)
    {
        object inbound;
        if (string.Equals(protocol, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
        {
            inbound = new
            {
                listen = config.BindAddress,
                port = config.Port,
                protocol = "shadowsocks",
                settings = new
                {
                    method = "aes-128-gcm",
                    network = "tcp,udp",
                    clients = clients.Select(x => new
                    {
                        password = x.Secret,
                        email = x.Email
                    }).ToArray()
                }
            };
        }
        else
        {
            inbound = new
            {
                listen = config.BindAddress,
                port = config.Port,
                protocol = "vless",
                settings = new
                {
                    decryption = "none",
                    clients = clients.Select(x => new
                    {
                        id = x.Id,
                        email = x.Email
                    }).ToArray()
                },
                streamSettings = new
                {
                    network = "tcp",
                    security = "none"
                }
            };
        }

        var xrayConfig = new
        {
            log = new
            {
                loglevel = "warning"
            },
            inbounds = new[] { inbound },
            outbounds = new object[]
            {
                new
                {
                    protocol = "freedom",
                    settings = new { },
                    sendThrough = ic2Ip.ToString()
                },
                new
                {
                    protocol = "blackhole",
                    tag = "blocked"
                }
            }
        };

        var raw = JsonSerializer.Serialize(xrayConfig, JsonOptions);
        File.WriteAllText(ServicePaths.LocalGatewayXrayConfigPath, raw);
    }

    private static async Task PumpStreamAsync(StreamReader reader, string filePath, CancellationToken cancellationToken)
    {
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

    private async Task EnsureStoppedAsync(bool removeFirewallRule, CancellationToken cancellationToken)
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken);
            }
        }
        catch
        {
            // Ignore stop errors.
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            if (removeFirewallRule)
            {
                RemoveFirewallRule();
            }
        }
    }

    private void EnsureFirewallRule(int port, string protocol)
    {
        RemoveFirewallRule();

        if (string.Equals(protocol, LocalGatewayProtocols.Shadowsocks, StringComparison.OrdinalIgnoreCase))
        {
            RunFirewallCommand($@"advfirewall firewall add rule name=""{ServicePaths.LocalGatewayFirewallRuleName} TCP"" dir=in action=allow protocol=TCP localport={port} profile=any");
            RunFirewallCommand($@"advfirewall firewall add rule name=""{ServicePaths.LocalGatewayFirewallRuleName} UDP"" dir=in action=allow protocol=UDP localport={port} profile=any");
            return;
        }

        RunFirewallCommand($@"advfirewall firewall add rule name=""{ServicePaths.LocalGatewayFirewallRuleName} TCP"" dir=in action=allow protocol=TCP localport={port} profile=any");
    }

    private void RemoveFirewallRule()
    {
        RunFirewallCommand($@"advfirewall firewall delete rule name=""{ServicePaths.LocalGatewayFirewallRuleName} TCP""");
        RunFirewallCommand($@"advfirewall firewall delete rule name=""{ServicePaths.LocalGatewayFirewallRuleName} UDP""");
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
            _fileLog.Error($"Firewall command failed: netsh {args}", ex);
        }
    }
}
