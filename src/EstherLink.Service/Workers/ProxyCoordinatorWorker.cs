using EstherLink.Core.Networking;
using EstherLink.Service.Runtime;
using System.Net;
using System.Net.Sockets;

namespace EstherLink.Service.Workers;

public sealed class ProxyCoordinatorWorker : BackgroundService
{
    private readonly GatewayRuntime _runtime;
    private readonly LicenseValidator _licenseValidator;
    private readonly HttpConnectProxyEngine _proxyEngine;
    private readonly Socks5BootstrapProxyEngine _socksEngine;
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<ProxyCoordinatorWorker> _logger;
    private DateTimeOffset _nextBootstrapProbeAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRecoveryAllowedAtUtc = DateTimeOffset.MinValue;
    private int _bootstrapProbeFailureCount;

    private static readonly TimeSpan BootstrapProbeInterval = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan BootstrapProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromSeconds(90);
    private const int BootstrapProbeFailuresBeforeRecovery = 5;

    public ProxyCoordinatorWorker(
        GatewayRuntime runtime,
        LicenseValidator licenseValidator,
        HttpConnectProxyEngine proxyEngine,
        Socks5BootstrapProxyEngine socksEngine,
        FileLogWriter fileLog,
        ILogger<ProxyCoordinatorWorker> logger)
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
        var nextLicenseCheck = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = _runtime.GetConfigSnapshot();
                UpdateAdapterStatus(config);

                if (!_runtime.IsProxyRequested())
                {
                    _runtime.SetProxyRunning(false, config.LocalProxyListenPort);
                    _runtime.SetBootstrapSocksStatus(false, _runtime.GetStatusSnapshot().TunnelConnected, "Proxy not requested.");
                    await _proxyEngine.StopAsync(stoppingToken);
                    await _socksEngine.StopAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                if (DateTimeOffset.UtcNow >= nextLicenseCheck)
                {
                    var license = await _licenseValidator.ValidateAsync(
                        config,
                        forceOnline: false,
                        transferRequested: false,
                        cancellationToken: stoppingToken);
                    _runtime.SetLicenseStatus(license);
                    nextLicenseCheck = DateTimeOffset.UtcNow.AddMinutes(5);

                    if (!license.IsValid)
                    {
                        _runtime.SetProxyRunning(false, config.LocalProxyListenPort);
                        _runtime.SetBootstrapSocksStatus(false, _runtime.GetStatusSnapshot().TunnelConnected, license.Error ?? "License invalid.");
                        await _proxyEngine.StopAsync(stoppingToken);
                        await _socksEngine.StopAsync(stoppingToken);
                        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                        continue;
                    }
                }

                await _proxyEngine.EnsureRunningAsync(config.LocalProxyListenPort, stoppingToken);
                await _socksEngine.EnsureRunningAsync(config.BootstrapSocksLocalPort, stoppingToken);
                _runtime.SetProxyRunning(true, config.LocalProxyListenPort);
                _runtime.SetBootstrapSocksStatus(true, _runtime.GetStatusSnapshot().TunnelConnected, null);

                await RunBootstrapSocksWatchdogAsync(config, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _runtime.SetError(ex.Message);
                _runtime.SetProxyRunning(false, _runtime.GetConfigSnapshot().LocalProxyListenPort);
                _runtime.SetBootstrapSocksStatus(false, _runtime.GetStatusSnapshot().TunnelConnected, ex.Message);
                _fileLog.Error("Proxy coordinator failure.", ex);
                _logger.LogError(ex, "Proxy coordinator failure.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        await _proxyEngine.StopAsync(CancellationToken.None);
        await _socksEngine.StopAsync(CancellationToken.None);
    }

    private void UpdateAdapterStatus(EstherLink.Core.Configuration.ServiceConfig config)
    {
        NetworkAdapterCatalog.TryGetPrimaryIpv4(config.WhitelistAdapterIfIndex, out var whitelistIp);
        NetworkAdapterCatalog.TryGetPrimaryIpv4(config.DefaultAdapterIfIndex, out var defaultIp);
        _runtime.SetAdapterIps(whitelistIp?.ToString(), defaultIp?.ToString());
    }

    private async Task RunBootstrapSocksWatchdogAsync(EstherLink.Core.Configuration.ServiceConfig config, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var status = _runtime.GetStatusSnapshot();
        if (!status.TunnelConnected || !status.BootstrapSocksRemoteForwardActive)
        {
            _bootstrapProbeFailureCount = 0;
            return;
        }

        if (now < _nextBootstrapProbeAtUtc)
        {
            return;
        }

        _nextBootstrapProbeAtUtc = now.Add(BootstrapProbeInterval);
        var success = await ProbeBootstrapSocksEgressAsync(config.BootstrapSocksLocalPort, cancellationToken);
        if (success)
        {
            _bootstrapProbeFailureCount = 0;
            return;
        }

        _bootstrapProbeFailureCount++;
        _runtime.SetBootstrapSocksStatus(status.BootstrapSocksListening, status.BootstrapSocksRemoteForwardActive, $"Bootstrap SOCKS watchdog probe failed ({_bootstrapProbeFailureCount}/{BootstrapProbeFailuresBeforeRecovery}).");

        if (_bootstrapProbeFailureCount < BootstrapProbeFailuresBeforeRecovery || now < _nextRecoveryAllowedAtUtc)
        {
            return;
        }

        _nextRecoveryAllowedAtUtc = now.Add(RecoveryCooldown);
        _bootstrapProbeFailureCount = 0;
        _fileLog.Warn("Bootstrap SOCKS watchdog triggered automatic recovery: restarting local SOCKS listener and requesting tunnel restart.");
        await _socksEngine.RestartAsync(config.BootstrapSocksLocalPort, cancellationToken);
        _runtime.RequestTunnelRestart("Automatic tunnel recovery requested after repeated bootstrap SOCKS probe failures.");
    }

    private static async Task<bool> ProbeBootstrapSocksEgressAsync(int socksPort, CancellationToken cancellationToken)
    {
        // Try multiple public targets to reduce false positives when a single IP is blocked.
        if (await ProbeBootstrapSocksTargetAsync(socksPort, "1.1.1.1", 443, true, cancellationToken))
        {
            return true;
        }

        if (await ProbeBootstrapSocksTargetAsync(socksPort, "8.8.8.8", 443, true, cancellationToken))
        {
            return true;
        }

        return await ProbeBootstrapSocksTargetAsync(socksPort, "deb.debian.org", 443, false, cancellationToken);
    }

    private static async Task<bool> ProbeBootstrapSocksTargetAsync(int socksPort, string targetHost, int targetPort, bool isIpv4Literal, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(BootstrapProbeTimeout);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, socksPort, cts.Token);
        using var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token);
        var methodReply = new byte[2];
        await ReadExactAsync(stream, methodReply, cts.Token);
        if (methodReply[0] != 0x05 || methodReply[1] != 0x00)
        {
            return false;
        }

        byte[] request;
        if (isIpv4Literal)
        {
            if (!IPAddress.TryParse(targetHost, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var ipBytes = ip.GetAddressBytes();
            request = new byte[10];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = 0x01;
            request[4] = ipBytes[0];
            request[5] = ipBytes[1];
            request[6] = ipBytes[2];
            request[7] = ipBytes[3];
            request[8] = (byte)((targetPort >> 8) & 0xFF);
            request[9] = (byte)(targetPort & 0xFF);
        }
        else
        {
            var hostBytes = System.Text.Encoding.ASCII.GetBytes(targetHost);
            if (hostBytes.Length == 0 || hostBytes.Length > 255)
            {
                return false;
            }

            request = new byte[7 + hostBytes.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = 0x03;
            request[4] = (byte)hostBytes.Length;
            hostBytes.CopyTo(request, 5);
            request[^2] = (byte)((targetPort >> 8) & 0xFF);
            request[^1] = (byte)(targetPort & 0xFF);
        }

        await stream.WriteAsync(request, cts.Token);

        var replyHeader = new byte[4];
        await ReadExactAsync(stream, replyHeader, cts.Token);
        if (replyHeader[0] != 0x05 || replyHeader[1] != 0x00)
        {
            return false;
        }

        switch (replyHeader[3])
        {
            case 0x01:
                await ReadExactAsync(stream, new byte[6], cts.Token); // ipv4 + port
                break;
            case 0x03:
                var lenBuf = new byte[1];
                await ReadExactAsync(stream, lenBuf, cts.Token);
                await ReadExactAsync(stream, new byte[lenBuf[0] + 2], cts.Token); // domain + port
                break;
            case 0x04:
                await ReadExactAsync(stream, new byte[18], cts.Token); // ipv6 + port
                break;
            default:
                return false;
        }

        return true;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read <= 0)
            {
                throw new IOException("SOCKS watchdog probe read EOF.");
            }

            offset += read;
        }
    }
}
