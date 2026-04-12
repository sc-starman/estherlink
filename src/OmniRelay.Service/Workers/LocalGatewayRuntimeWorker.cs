using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
    private const string OpenVpnNatName = "OmniRelayOpenVpnNat";
    private const string OpenVpnNatPrefix = "10.66.0.0/24";
    private Process? _process;
    private bool _lastStartWasAutoRecovered;
    private long _lastObservedRequestVersion;
    private DateTimeOffset _lastOpenVpnDriverInstallAttemptUtc = DateTimeOffset.MinValue;
    private bool _openVpnMetricPinApplied;
    private int _openVpnMetricPinnedIfIndex = -1;
    private int? _openVpnMetricPinnedPrevious;
    private int _openVpnMetricDeprioritizedIfIndex = -1;
    private int? _openVpnMetricDeprioritizedPrevious;
    private bool _openVpnStrictRouteApplied;
    private int _openVpnStrictRouteIfIndex = -1;
    private string? _openVpnStrictRouteGateway;

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

                var binaryPath = ResolveLocalGatewayExecutablePath(protocol);
                if (string.Equals(protocol, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
                {
                    var installResult = await EnsureOpenVpnInstalledAndResolveBinaryAsync(binaryPath, stoppingToken);
                    binaryPath = installResult.BinaryPath;
                    if (!string.IsNullOrWhiteSpace(installResult.ErrorReason))
                    {
                        await EnsureStoppedAsync(removeFirewallRule: true, stoppingToken);
                        _runtime.SetLocalGatewayStatus("unhealthy", installResult.ErrorReason, false);
                        _lastObservedRequestVersion = currentVersion;
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                        continue;
                    }
                }

                if (!File.Exists(binaryPath))
                {
                    await EnsureStoppedAsync(removeFirewallRule: true, stoppingToken);
                    _runtime.SetLocalGatewayStatus(
                        "unhealthy",
                        string.Equals(protocol, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase)
                            ? "openvpn_not_installed_or_binary_missing"
                            : "xray_binary_missing",
                        false);
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

    private static string ResolveLocalGatewayExecutablePath(string protocol)
    {
        return string.Equals(protocol, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase)
            ? ServicePaths.ResolveOpenVpnExecutablePath()
            : ServicePaths.ResolveXrayExecutablePath();
    }

    private async Task RestartProcessAsync(
        ServiceConfig config,
        string protocol,
        IPAddress ic2Ip,
        string binaryPath,
        CancellationToken cancellationToken)
    {
        if (string.Equals(protocol, LocalGatewayProtocols.OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
        {
            await RestartOpenVpnProcessAsync(config, protocol, binaryPath, cancellationToken);
            return;
        }

        await RestartXrayProcessAsync(config, protocol, ic2Ip, binaryPath, cancellationToken);
    }

    private async Task RestartXrayProcessAsync(
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

    private async Task RestartOpenVpnProcessAsync(
        ServiceConfig config,
        string protocol,
        string openVpnBinaryPath,
        CancellationToken cancellationToken)
    {
        await EnsureStoppedAsync(removeFirewallRule: false, cancellationToken);

        var driver = await EnsureOpenVpnDriverReadyAsync(cancellationToken);
        if (!driver.Ready)
        {
            _runtime.SetLocalGatewayStatus("unhealthy", driver.ErrorReason ?? "openvpn_driver_missing_or_install_failed", false);
            return;
        }

        var clients = _runtime.GetLocalGatewayClientsSnapshot()
            .Where(x => x.Enabled &&
                        string.Equals(LocalGatewayProtocols.Normalize(x.Protocol), protocol, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var prepare = await EnsureOpenVpnAssetsAsync(config.LocalGateway, clients, openVpnBinaryPath, cancellationToken);
        if (!prepare.Success)
        {
            _runtime.SetLocalGatewayStatus("unhealthy", prepare.Error ?? "openvpn_prepare_failed", false);
            return;
        }

        if (!EnsureOpenVpnNatReady(out var natError))
        {
            _runtime.SetLocalGatewayStatus("unhealthy", natError ?? "openvpn_nat_setup_failed", false);
            return;
        }

        if (!TryApplyOpenVpnStrictRouteOverride(config, out var routeError))
        {
            _runtime.SetLocalGatewayStatus("unhealthy", routeError ?? "openvpn_ic2_route_pin_failed", false);
            return;
        }

        EnsureFirewallRule(config.LocalGateway.Port, protocol);
        _runtime.SetLocalGatewayStatus("activating", "openvpn_starting", false);

        var startInfo = new ProcessStartInfo
        {
            FileName = openVpnBinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(ServicePaths.LocalGatewayOpenVpnServerConfigPath);

        _process = Process.Start(startInfo);
        if (_process is null)
        {
            _runtime.SetLocalGatewayStatus("unhealthy", "openvpn_start_failed", false);
            return;
        }

        _ = PumpStreamAsync(_process.StandardOutput, ServicePaths.LocalGatewayOpenVpnStdoutLogPath, cancellationToken);
        _ = PumpStreamAsync(_process.StandardError, ServicePaths.LocalGatewayOpenVpnStderrLogPath, cancellationToken);

        await Task.Delay(1200, cancellationToken);
        if (_process.HasExited)
        {
            _runtime.SetLocalGatewayStatus("unhealthy", $"openvpn_exited_{_process.ExitCode}", false);
            return;
        }

        _runtime.SetLocalGatewayStatus("active", null, _lastStartWasAutoRecovered);
        _lastStartWasAutoRecovered = false;
    }

    private async Task<(bool Ready, string? ErrorReason)> EnsureOpenVpnDriverReadyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ServicePaths.ResolveOpenVpnExecutablePath()))
        {
            return (false, "openvpn_binary_missing");
        }

        if (IsOpenVpnDriverPresent())
        {
            return (true, null);
        }

        if ((DateTimeOffset.UtcNow - _lastOpenVpnDriverInstallAttemptUtc) < TimeSpan.FromSeconds(30))
        {
            return (false, "openvpn_driver_missing");
        }

        _lastOpenVpnDriverInstallAttemptUtc = DateTimeOffset.UtcNow;
        var installerPath = ServicePaths.ResolveOpenVpnDriverInstallerPath();
        if (!File.Exists(installerPath))
        {
            _fileLog.Warn($"OpenVPN driver installer is missing: {installerPath}");
            return (false, "openvpn_installer_missing");
        }

        _fileLog.Info($"Attempting OpenVPN MSI installation for driver/runtime from {installerPath}");
        _runtime.SetLocalGatewayStatus("activating", "openvpn_installing", false);
        var install = await InstallOpenVpnDriverAsync(installerPath, cancellationToken);
        if (!install.Success)
        {
            _fileLog.Warn($"OpenVPN driver installer command failed with exit code {install.ExitCode}.");
            return (false, $"openvpn_installer_exit_code_{install.ExitCode}");
        }

        await Task.Delay(1500, cancellationToken);
        if (!IsOpenVpnDriverPresent())
        {
            _fileLog.Warn("OpenVPN driver still unavailable after installer execution.");
            return (false, "openvpn_driver_still_missing_after_install");
        }

        _fileLog.Info("OpenVPN driver is ready.");
        return (true, null);
    }

    private static bool IsOpenVpnDriverPresent()
    {
        return IsWindowsServiceKnown("ovpn-dco") ||
               IsWindowsServiceKnown("wintun") ||
               IsWindowsServiceKnown("tap0901");
    }

    private static bool IsWindowsServiceKnown(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("query");
            psi.ArgumentList.Add(serviceName);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool Success, int ExitCode)> InstallOpenVpnDriverAsync(string installerPath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(installerPath).ToLowerInvariant();
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (extension == ".msi")
        {
            psi.FileName = "msiexec.exe";
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(installerPath);
            psi.ArgumentList.Add("/qn");
            psi.ArgumentList.Add("/norestart");
        }
        else
        {
            psi.FileName = installerPath;
            psi.ArgumentList.Add("/S");
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (false, -1);
        }

        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode == 0, process.ExitCode);
    }

    private async Task<(string BinaryPath, string? ErrorReason)> EnsureOpenVpnInstalledAndResolveBinaryAsync(string currentBinaryPath, CancellationToken cancellationToken)
    {
        if (File.Exists(currentBinaryPath))
        {
            return (currentBinaryPath, null);
        }

        if ((DateTimeOffset.UtcNow - _lastOpenVpnDriverInstallAttemptUtc) < TimeSpan.FromSeconds(30))
        {
            return (currentBinaryPath, "openvpn_binary_missing");
        }

        _lastOpenVpnDriverInstallAttemptUtc = DateTimeOffset.UtcNow;
        var installerPath = ServicePaths.ResolveOpenVpnDriverInstallerPath();
        if (!File.Exists(installerPath))
        {
            _fileLog.Warn($"OpenVPN MSI installer is missing: {installerPath}");
            return (currentBinaryPath, "openvpn_installer_missing");
        }

        _runtime.SetLocalGatewayStatus("activating", "openvpn_installing", false);
        var install = await InstallOpenVpnDriverAsync(installerPath, cancellationToken);
        if (!install.Success)
        {
            _fileLog.Warn($"OpenVPN MSI install command failed with exit code {install.ExitCode}.");
            return (currentBinaryPath, $"openvpn_installer_exit_code_{install.ExitCode}");
        }

        await Task.Delay(1500, cancellationToken);
        var resolved = ServicePaths.ResolveOpenVpnExecutablePath();
        if (!File.Exists(resolved))
        {
            return (resolved, "openvpn_binary_missing_after_install");
        }

        return (resolved, null);
    }

    private async Task<(bool Success, string? Error)> EnsureOpenVpnAssetsAsync(
        LocalGatewayConfig config,
        IReadOnlyList<LocalGatewayClient> clients,
        string openVpnBinaryPath,
        CancellationToken cancellationToken)
    {
        try
        {
            ServicePaths.EnsureDirectories();
            Directory.CreateDirectory(ServicePaths.LocalGatewayOpenVpnDirectory);
            EnsureOpenVpnCertificates();
            WriteOpenVpnAuthScript();
            WriteOpenVpnAuthFile(clients);
            if (!await EnsureOpenVpnTlsCryptKeyAsync(openVpnBinaryPath, cancellationToken))
            {
                return (false, "openvpn_tls_crypt_key_generation_failed");
            }

            WriteOpenVpnServerConfig(config);
            return (true, null);
        }
        catch (Exception ex)
        {
            _fileLog.Error("Failed preparing OpenVPN runtime assets.", ex);
            return (false, "openvpn_prepare_failed");
        }
    }

    private void EnsureOpenVpnCertificates()
    {
        if (File.Exists(ServicePaths.LocalGatewayOpenVpnCaPath) &&
            File.Exists(ServicePaths.LocalGatewayOpenVpnServerCertPath) &&
            File.Exists(ServicePaths.LocalGatewayOpenVpnServerKeyPath))
        {
            return;
        }

        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest(
            "CN=OmniRelay Local OpenVPN CA",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));

        using var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        using var serverKey = RSA.Create(2048);
        var serverReq = new CertificateRequest(
            "CN=OmniRelay Local OpenVPN Server",
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        serverReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverReq.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                false));
        serverReq.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));
        serverReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(serverReq.PublicKey, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        using var serverCert = serverReq.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), serial);

        File.WriteAllText(ServicePaths.LocalGatewayOpenVpnCaPath, ToPem("CERTIFICATE", caCert.Export(X509ContentType.Cert)));
        File.WriteAllText(ServicePaths.LocalGatewayOpenVpnServerCertPath, ToPem("CERTIFICATE", serverCert.Export(X509ContentType.Cert)));
        File.WriteAllText(ServicePaths.LocalGatewayOpenVpnServerKeyPath, ToPem("PRIVATE KEY", serverKey.ExportPkcs8PrivateKey()));
    }

    private static string ToPem(string label, byte[] raw)
    {
        var b64 = Convert.ToBase64String(raw);
        var sb = new StringBuilder(b64.Length + 80);
        sb.AppendLine($"-----BEGIN {label}-----");
        for (var i = 0; i < b64.Length; i += 64)
        {
            sb.AppendLine(b64.Substring(i, Math.Min(64, b64.Length - i)));
        }

        sb.AppendLine($"-----END {label}-----");
        return sb.ToString();
    }

    private async Task<bool> EnsureOpenVpnTlsCryptKeyAsync(string openVpnBinaryPath, CancellationToken cancellationToken)
    {
        if (File.Exists(ServicePaths.LocalGatewayOpenVpnTlsCryptKeyPath))
        {
            return true;
        }

        var psi = new ProcessStartInfo
        {
            FileName = openVpnBinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--genkey");
        psi.ArgumentList.Add("secret");
        psi.ArgumentList.Add(ServicePaths.LocalGatewayOpenVpnTlsCryptKeyPath);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 && File.Exists(ServicePaths.LocalGatewayOpenVpnTlsCryptKeyPath);
    }

    private static void WriteOpenVpnAuthScript()
    {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var script = """
param([string]$CredentialFile)
$ErrorActionPreference = "Stop"
$logFile = Join-Path $PSScriptRoot "openvpn.auth.log"
function Write-AuthLog([string]$message) {
  try {
    Add-Content -LiteralPath $logFile -Value ("{0} {1}" -f (Get-Date -Format o), $message)
  } catch {}
}
if ([string]::IsNullOrWhiteSpace($CredentialFile) -or -not (Test-Path -LiteralPath $CredentialFile)) {
  Write-AuthLog "AUTH_FAIL reason=credential_file_missing"
  exit 1
}
$lines = Get-Content -LiteralPath $CredentialFile -ErrorAction Stop
if ($lines.Count -lt 2) {
  Write-AuthLog "AUTH_FAIL reason=credential_file_invalid"
  exit 1
}
$username = ""
if ($null -ne $lines[0]) {
  $username = [string]$lines[0]
}
$username = $username.Trim()
$password = ""
if ($null -ne $lines[1]) {
  $password = [string]$lines[1]
}
$password = $password.Trim()
if ([string]::IsNullOrWhiteSpace($username)) {
  Write-AuthLog "AUTH_FAIL reason=username_missing"
  exit 1
}
$authFile = Join-Path $PSScriptRoot "openvpn.auth.txt"
if (-not (Test-Path -LiteralPath $authFile)) {
  Write-AuthLog ("AUTH_FAIL reason=auth_file_missing username={0}" -f $username)
  exit 1
}
foreach ($line in Get-Content -LiteralPath $authFile) {
  if ([string]::IsNullOrWhiteSpace($line)) {
    continue
  }
  $idx = $line.IndexOf(":")
  if ($idx -lt 1) {
    continue
  }
  $u = $line.Substring(0, $idx).Trim()
  $p = $line.Substring($idx + 1)
  if ($u.Equals($username, [System.StringComparison]::OrdinalIgnoreCase) -and $p -ceq $password) {
    Write-AuthLog ("AUTH_OK username={0}" -f $username)
    exit 0
  }
}
Write-AuthLog ("AUTH_FAIL reason=invalid_credentials username={0}" -f $username)
exit 1
""";
        File.WriteAllText(ServicePaths.LocalGatewayOpenVpnAuthScriptPath, script, utf8NoBom);

        var wrapper = """
@echo off
setlocal
set "PSH=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
"%PSH%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0auth-verify.ps1" "%~1"
exit /b %ERRORLEVEL%
""";
        File.WriteAllText(ServicePaths.LocalGatewayOpenVpnAuthCmdPath, wrapper, utf8NoBom);
    }

    private static void WriteOpenVpnAuthFile(IReadOnlyList<LocalGatewayClient> clients)
    {
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
        File.WriteAllLines(ServicePaths.LocalGatewayOpenVpnAuthFilePath, lines, utf8NoBom);
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

    private static void WriteOpenVpnServerConfig(LocalGatewayConfig config)
    {
        static string Quote(string path) => $"\"{path.Replace("\\", "/")}\"";

        var authVerifyCommand = ServicePaths.LocalGatewayOpenVpnAuthCmdPath.Replace("\\", "/");

        var sb = new StringBuilder(4096);
        sb.AppendLine($"port {config.Port}");
        sb.AppendLine("proto tcp-server");
        sb.AppendLine("dev tun");
        sb.AppendLine("topology subnet");
        sb.AppendLine("server 10.66.0.0 255.255.255.0");
        sb.AppendLine("keepalive 10 60");
        sb.AppendLine($"ca {Quote(ServicePaths.LocalGatewayOpenVpnCaPath)}");
        sb.AppendLine($"cert {Quote(ServicePaths.LocalGatewayOpenVpnServerCertPath)}");
        sb.AppendLine($"key {Quote(ServicePaths.LocalGatewayOpenVpnServerKeyPath)}");
        sb.AppendLine($"tls-crypt {Quote(ServicePaths.LocalGatewayOpenVpnTlsCryptKeyPath)}");
        sb.AppendLine("verify-client-cert none");
        sb.AppendLine("username-as-common-name");
        sb.AppendLine($"auth-user-pass-verify {Quote(authVerifyCommand)} via-file");
        sb.AppendLine("script-security 2");
        sb.AppendLine("persist-key");
        sb.AppendLine("persist-tun");
        sb.AppendLine("push \"redirect-gateway def1 bypass-dhcp\"");
        sb.AppendLine("push \"dhcp-option DNS 1.1.1.1\"");
        sb.AppendLine("push \"dhcp-option DNS 8.8.8.8\"");
        sb.AppendLine($"status {Quote(Path.Combine(ServicePaths.LocalGatewayOpenVpnDirectory, "status.log"))}");
        sb.AppendLine("cipher AES-256-GCM");
        sb.AppendLine("data-ciphers AES-256-GCM:AES-128-GCM");
        sb.AppendLine("auth SHA256");
        sb.AppendLine("verb 3");
        File.WriteAllText(ServicePaths.LocalGatewayOpenVpnServerConfigPath, sb.ToString(), Encoding.UTF8);
    }

    private bool TryApplyOpenVpnInterfaceMetricPin(ServiceConfig config, out string? error)
    {
        error = null;
        RemoveOpenVpnInterfaceMetricPin();

        var ic2IfIndex = config.DefaultAdapterIfIndex;
        if (ic2IfIndex <= 0)
        {
            error = "openvpn_ic2_adapter_invalid";
            return false;
        }

        if (!TryGetInterfaceMetric(ic2IfIndex, out var ic2PrevMetric))
        {
            error = "openvpn_ic2_metric_read_failed";
            return false;
        }

        if (!TrySetInterfaceMetric(ic2IfIndex, 5))
        {
            error = "openvpn_ic2_metric_set_failed";
            return false;
        }

        _openVpnMetricPinApplied = true;
        _openVpnMetricPinnedIfIndex = ic2IfIndex;
        _openVpnMetricPinnedPrevious = ic2PrevMetric;

        var ic1IfIndex = config.WhitelistAdapterIfIndex;
        if (ic1IfIndex > 0 && ic1IfIndex != ic2IfIndex && TryGetInterfaceMetric(ic1IfIndex, out var ic1PrevMetric))
        {
            if (TrySetInterfaceMetric(ic1IfIndex, 250))
            {
                _openVpnMetricDeprioritizedIfIndex = ic1IfIndex;
                _openVpnMetricDeprioritizedPrevious = ic1PrevMetric;
            }
        }

        _fileLog.Info($"OpenVPN IC2 metric pin enabled. ic2IfIndex={ic2IfIndex} metric=5");
        return true;
    }

    private void RemoveOpenVpnInterfaceMetricPin()
    {
        if (!_openVpnMetricPinApplied)
        {
            return;
        }

        if (_openVpnMetricPinnedIfIndex > 0 && _openVpnMetricPinnedPrevious.HasValue)
        {
            TrySetInterfaceMetric(_openVpnMetricPinnedIfIndex, _openVpnMetricPinnedPrevious.Value);
        }

        if (_openVpnMetricDeprioritizedIfIndex > 0 && _openVpnMetricDeprioritizedPrevious.HasValue)
        {
            TrySetInterfaceMetric(_openVpnMetricDeprioritizedIfIndex, _openVpnMetricDeprioritizedPrevious.Value);
        }

        _openVpnMetricPinApplied = false;
        _openVpnMetricPinnedIfIndex = -1;
        _openVpnMetricPinnedPrevious = null;
        _openVpnMetricDeprioritizedIfIndex = -1;
        _openVpnMetricDeprioritizedPrevious = null;
        _fileLog.Info("OpenVPN IC2 metric pin removed.");
    }

    private bool TryGetInterfaceMetric(int ifIndex, out int metric)
    {
        metric = 0;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"$m=(Get-NetIPInterface -AddressFamily IPv4 -InterfaceIndex {ifIndex} -ErrorAction Stop).InterfaceMetric; Write-Output $m");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0)
            {
                return false;
            }

            return int.TryParse((output ?? string.Empty).Trim(), out metric) && metric > 0;
        }
        catch (Exception ex)
        {
            _fileLog.Error($"Failed reading IPv4 metric for interface {ifIndex}.", ex);
            return false;
        }
    }

    private bool TrySetInterfaceMetric(int ifIndex, int metric)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ipv4 set interface interface={ifIndex} metric={metric}",
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

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _fileLog.Error($"Failed setting IPv4 metric for interface {ifIndex} => {metric}.", ex);
            return false;
        }
    }

    private bool EnsureOpenVpnNatReady(out string? error)
    {
        error = null;
        try
        {
            var check = RunPowerShell(
                "$n = Get-NetNat -Name '" + OpenVpnNatName + @"' -ErrorAction SilentlyContinue; " +
                "if ($null -eq $n) { Write-Output 'missing'; exit 0 }; " +
                "Write-Output $n.InternalIPInterfaceAddressPrefix");
            if (!check.Success)
            {
                error = "openvpn_nat_query_failed";
                return false;
            }

            var currentPrefix = (check.Output ?? string.Empty).Trim();
            if (string.Equals(currentPrefix, OpenVpnNatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(currentPrefix) &&
                !string.Equals(currentPrefix, "missing", StringComparison.OrdinalIgnoreCase))
            {
                var remove = RunPowerShell("Remove-NetNat -Name '" + OpenVpnNatName + "' -Confirm:$false");
                if (!remove.Success)
                {
                    error = "openvpn_nat_remove_failed";
                    return false;
                }
            }

            var create = RunPowerShell(
                "New-NetNat -Name '" + OpenVpnNatName + "' -InternalIPInterfaceAddressPrefix '" + OpenVpnNatPrefix + "' | Out-Null");
            if (!create.Success)
            {
                error = "openvpn_nat_create_failed";
                return false;
            }

            _fileLog.Info($"OpenVPN NAT ensured. name={OpenVpnNatName} prefix={OpenVpnNatPrefix}");
            return true;
        }
        catch (Exception ex)
        {
            _fileLog.Error("Failed ensuring OpenVPN NAT.", ex);
            error = "openvpn_nat_setup_failed";
            return false;
        }
    }

    private void RemoveOpenVpnNat()
    {
        var remove = RunPowerShell("Remove-NetNat -Name '" + OpenVpnNatName + "' -Confirm:$false -ErrorAction SilentlyContinue");
        if (remove.Success)
        {
            _fileLog.Info("OpenVPN NAT removed.");
        }
    }

    private (bool Success, string Output) RunPowerShell(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, string.Empty);
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(8000);
            if (process.ExitCode != 0)
            {
                _fileLog.Warn($"PowerShell command failed. exit={process.ExitCode} stderr={error}");
                return (false, output);
            }

            return (true, output);
        }
        catch (Exception ex)
        {
            _fileLog.Error("Failed executing PowerShell command.", ex);
            return (false, string.Empty);
        }
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
            RemoveOpenVpnInterfaceMetricPin();
            RemoveOpenVpnStrictRouteOverride();
            RemoveOpenVpnNat();
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

    private bool TryApplyOpenVpnStrictRouteOverride(ServiceConfig config, out string? error)
    {
        error = null;
        RemoveOpenVpnStrictRouteOverride();
        RemoveOpenVpnInterfaceMetricPin();

        if (!NetworkAdapterCatalog.TryGetPrimaryIpv4Gateway(config.DefaultAdapterIfIndex, out var gateway) || gateway is null)
        {
            error = "openvpn_ic2_gateway_unavailable";
            return false;
        }

        var gatewayIp = gateway.ToString();
        var ifIndex = config.DefaultAdapterIfIndex;

        var firstAdded = RunRouteCommand($"add 0.0.0.0 mask 128.0.0.0 {gatewayIp} if {ifIndex} metric 3");
        var secondAdded = RunRouteCommand($"add 128.0.0.0 mask 128.0.0.0 {gatewayIp} if {ifIndex} metric 3");
        if (!firstAdded || !secondAdded)
        {
            RunRouteCommand($"delete 0.0.0.0 mask 128.0.0.0 {gatewayIp} if {ifIndex}");
            RunRouteCommand($"delete 128.0.0.0 mask 128.0.0.0 {gatewayIp} if {ifIndex}");
            error = "openvpn_ic2_route_pin_failed";
            return false;
        }

        _openVpnStrictRouteApplied = true;
        _openVpnStrictRouteIfIndex = ifIndex;
        _openVpnStrictRouteGateway = gatewayIp;
        _fileLog.Info($"OpenVPN IC2 strict route pin enabled via gateway={gatewayIp} ifIndex={ifIndex}.");
        return true;
    }

    private void RemoveOpenVpnStrictRouteOverride()
    {
        if (!_openVpnStrictRouteApplied || string.IsNullOrWhiteSpace(_openVpnStrictRouteGateway) || _openVpnStrictRouteIfIndex <= 0)
        {
            return;
        }

        var gatewayIp = _openVpnStrictRouteGateway!;
        var ifIndex = _openVpnStrictRouteIfIndex;
        RunRouteCommand($"delete 0.0.0.0 mask 128.0.0.0 {gatewayIp} if {ifIndex}");
        RunRouteCommand($"delete 128.0.0.0 mask 128.0.0.0 {gatewayIp} if {ifIndex}");

        _openVpnStrictRouteApplied = false;
        _openVpnStrictRouteIfIndex = -1;
        _openVpnStrictRouteGateway = null;
        _fileLog.Info("OpenVPN IC2 strict route pin removed.");
    }

    private bool RunRouteCommand(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "route.exe",
                Arguments = args,
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

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _fileLog.Error($"Route command failed: route {args}", ex);
            return false;
        }
    }
}
