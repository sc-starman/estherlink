using EstherLink.Core.Networking;
using EstherLink.Service.Runtime;

namespace EstherLink.Service.Workers;

public sealed class ProxyCoordinatorWorker : BackgroundService
{
    private readonly GatewayRuntime _runtime;
    private readonly LicenseValidator _licenseValidator;
    private readonly HttpConnectProxyEngine _proxyEngine;
    private readonly Socks5BootstrapProxyEngine _socksEngine;
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<ProxyCoordinatorWorker> _logger;

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
                    var license = await _licenseValidator.ValidateAsync(config, forceOnline: false, stoppingToken);
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
}
