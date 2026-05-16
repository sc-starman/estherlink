using OmniRelay.Core.Configuration;
using OmniRelay.UI.Models;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;

namespace OmniRelay.UI.Services;

public sealed class GatewayDeploymentService : IGatewayDeploymentService, IGatewayHealthService
{
    private const string GatewayCtlPath = "/usr/local/sbin/omnirelay-gatewayctl";
    private const string TunnelCtlPath = "/usr/local/sbin/omnirelay-tunnelctl";
    private const string RemoteInstallScriptPath = "/tmp/omnirelay-gatewayctl.sh";
    private const string RemoteOmniPanelCommonScriptPath = "/tmp/omnirelay-omnipanel-common.sh";
    private const string RemoteBootstrapCommonScriptPath = "/tmp/omnirelay-bootstrap-common.sh";
    private const string RemoteSingBoxConnectorCommonScriptPath = "/tmp/omnirelay-singbox-connector-common.sh";
    private const string RemoteTunnelModuleScriptPath = "/tmp/omnirelay-tunnel-module.sh";
    private const string RemoteUploadedPanelCertPath = "/tmp/omnirelay-omnipanel-upload.crt";
    private const string RemoteUploadedPanelKeyPath = "/tmp/omnirelay-omnipanel-upload.key";
    private const string RemoteOpenVpnSharedCaCertPath = "/tmp/omnirelay-openvpn-shared-ca.crt";
    private const string RemoteOpenVpnSharedClientCertPath = "/tmp/omnirelay-openvpn-shared-client.crt";
    private const string RemoteOpenVpnSharedClientKeyPath = "/tmp/omnirelay-openvpn-shared-client.key";
    private const string RemoteOpenVpnSharedTlsCryptKeyPath = "/tmp/omnirelay-openvpn-shared-ta.key";

    public GatewayDeploymentService() { }

    public async Task<GatewayOperationResult> CheckGatewayBootstrapAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateRequest(request);
            EnsureSudoPassword(sudoPassword);

            if (!IsTunnelBootstrapMode(request))
            {
                progress?.Report(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayBootstrap,
                    Percent = 100,
                    Message = "Direct bootstrap mode selected; SOCKS bootstrap check skipped"
                });
                return new GatewayOperationResult(true, "Direct bootstrap mode selected; SOCKS bootstrap preflight skipped.");
            }

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayBootstrap,
                Percent = 10,
                Message = "Checking SOCKS bootstrap endpoint"
            });

            var socksPort = request.Config.BootstrapSocksRemotePort.ToString();
            var command = """
                set -euo pipefail;
                ss -lnt '( sport = :__PORT__ )' 2>/dev/null | awk 'NR>1 {print $0}' | grep -q . || { echo 'SOCKS listener is not present on 127.0.0.1:__PORT__'; exit 41; };
                sync_clock_done=0;
                sync_clock_via_socks() {
                  local hdr epoch was_ntp;
                  was_ntp='';
                  hdr="$(curl --silent --show-error --insecure --max-time 25 --connect-timeout 10 --retry 0 --socks5-hostname 127.0.0.1:__PORT__ -I https://deb.debian.org/ 2>/dev/null | tr -d '\r' | awk 'tolower($1)=="date:"{$1="";sub(/^ /,"");print;exit}')";
                  [ -n "$hdr" ] || return 1;
                  epoch="$(date -u -d "$hdr" +%s 2>/dev/null || true)";
                  [ -n "$epoch" ] || return 1;
                  if command -v timedatectl >/dev/null 2>&1; then
                    was_ntp="$(timedatectl show -p NTP --value 2>/dev/null || true)";
                    [ "$was_ntp" = "yes" ] && timedatectl set-ntp false >/dev/null 2>&1 || true;
                  fi;
                  date -u -s "@$epoch" >/dev/null 2>&1 || return 1;
                  if command -v hwclock >/dev/null 2>&1; then hwclock --systohc >/dev/null 2>&1 || true; fi;
                  if command -v timedatectl >/dev/null 2>&1 && [ "${was_ntp:-}" = "yes" ]; then timedatectl set-ntp true >/dev/null 2>&1 || true; fi;
                  echo 'Adjusted VPS clock from HTTPS Date header via SOCKS tunnel.';
                  return 0;
                };
                ok=0;
                for i in 1 2 3; do
                  if curl --fail --silent --show-error --max-time 45 --connect-timeout 20 --retry 0 --socks5-hostname 127.0.0.1:__PORT__ https://deb.debian.org/ >/dev/null 2>/tmp/omnirelay-bootstrap-curl.err; then ok=1; break; fi;
                  err="$(tr -d '\r' </tmp/omnirelay-bootstrap-curl.err 2>/dev/null || true)";
                  [ -n "$err" ] && printf '%s\n' "$err";
                  if [ "$sync_clock_done" != "1" ] && printf '%s' "$err" | grep -qi 'certificate is not yet valid'; then
                    echo 'Detected TLS clock skew; attempting clock sync over SOCKS tunnel.';
                    if sync_clock_via_socks; then sync_clock_done=1; continue; fi;
                    sync_clock_done=1;
                  fi;
                  echo "SOCKS egress probe attempt ${i}/3 failed, retrying...";
                  sleep 3;
                done;
                rm -f /tmp/omnirelay-bootstrap-curl.err;
                if [ "$ok" != "1" ]; then echo 'SOCKS egress probe failed after retries.'; exit 42; fi;
                echo 'Gateway bootstrap SOCKS check passed.';
                """.Replace("__PORT__", socksPort);

            var result = await ExecuteCommandAsync(
                request.Config,
                command,
                sudoPassword,
                line =>
                {
                    var clean = SanitizeTerminalLine(line);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = DeploymentPhases.GatewayBootstrap,
                            Percent = 0,
                            Message = $"[vps] {clean}"
                        });
                    }
                },
                cancellationToken);
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayBootstrap,
                Percent = result.Success ? 100 : 0,
                Message = result.Success ? "SOCKS bootstrap check passed" : "SOCKS bootstrap check failed"
            });

            return result.Success
                ? new GatewayOperationResult(true, "Gateway bootstrap check passed.")
                : new GatewayOperationResult(false, result.ErrorMessage);
        }
        catch (Exception ex)
        {
            return new GatewayOperationResult(false, $"Gateway bootstrap check failed: {ex.Message}");
        }
    }

    public async Task<GatewayOperationResult> InstallGatewayAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateRequest(request);
            EnsureSudoPassword(sudoPassword);

            var bootstrap = await CheckGatewayBootstrapAsync(request, sudoPassword, progress, cancellationToken);
            if (!bootstrap.Success)
            {
                return new GatewayOperationResult(false, $"Gateway bootstrap preflight failed: {bootstrap.Message}");
            }

            await EnsureTunnelModuleInstalledAsync(request, sudoPassword, DeploymentPhases.GatewayInstall, progress, cancellationToken);
            await EnsureCleanProtocolSwitchAsync(request, sudoPassword, progress, cancellationToken);
            await EnsureRuntimeSocksBackendReadyForInstallAsync(request, sudoPassword, progress, cancellationToken);

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 5,
                Message = "Uploading gateway installer script"
            });

            await UploadInstallerScriptAsync(request, progress, cancellationToken);
            await UploadBootstrapCommonScriptAsync(request, progress, cancellationToken);
            await UploadOmniPanelCommonScriptAsync(request, progress, cancellationToken);
            await UploadSingBoxConnectorCommonScriptAsync(request, progress, cancellationToken);

            var uploadedPanelCertRemotePath = string.Empty;
            var uploadedPanelKeyRemotePath = string.Empty;
            var uploadedOpenVpnSharedCaCertRemotePath = string.Empty;
            var uploadedOpenVpnSharedClientCertRemotePath = string.Empty;
            var uploadedOpenVpnSharedClientKeyRemotePath = string.Empty;
            var uploadedOpenVpnSharedTlsCryptKeyRemotePath = string.Empty;
            if (request.GatewayPanelSslEnabled &&
                string.Equals(request.GatewayPanelSslMode, "uploaded", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayInstall,
                    Percent = 9,
                    Message = "Uploading OmniPanel TLS certificate and private key"
                });

                uploadedPanelCertRemotePath = GetRemoteUploadedPanelCertPath(request);
                uploadedPanelKeyRemotePath = GetRemoteUploadedPanelKeyPath(request);
                await UploadFileAsync(request, request.GatewayPanelCertLocalPath, uploadedPanelCertRemotePath, progress, cancellationToken);
                await UploadFileAsync(request, request.GatewayPanelKeyLocalPath, uploadedPanelKeyRemotePath, progress, cancellationToken);
            }

            if (string.Equals(GatewayProtocols.Normalize(request.SelectedGatewayProtocol), GatewayProtocols.OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayInstall,
                    Percent = 9,
                    Message = "Uploading OpenVPN shared bundle files"
                });
                uploadedOpenVpnSharedCaCertRemotePath = RemoteOpenVpnSharedCaCertPath;
                uploadedOpenVpnSharedClientCertRemotePath = RemoteOpenVpnSharedClientCertPath;
                uploadedOpenVpnSharedClientKeyRemotePath = RemoteOpenVpnSharedClientKeyPath;
                uploadedOpenVpnSharedTlsCryptKeyRemotePath = RemoteOpenVpnSharedTlsCryptKeyPath;
                await UploadFileAsync(request, request.OpenVpnSharedCaCertLocalPath, uploadedOpenVpnSharedCaCertRemotePath, progress, cancellationToken);
                await UploadFileAsync(request, request.OpenVpnSharedClientCertLocalPath, uploadedOpenVpnSharedClientCertRemotePath, progress, cancellationToken);
                await UploadFileAsync(request, request.OpenVpnSharedClientKeyLocalPath, uploadedOpenVpnSharedClientKeyRemotePath, progress, cancellationToken);
                await UploadFileAsync(request, request.OpenVpnSharedTlsCryptKeyLocalPath, uploadedOpenVpnSharedTlsCryptKeyRemotePath, progress, cancellationToken);
            }

            var panelUser = string.IsNullOrWhiteSpace(request.GatewayPanelUser)
                ? $"omniadmin_{RandomAlphaNum(6)}"
                : request.GatewayPanelUser.Trim();
            var panelPassword = string.IsNullOrWhiteSpace(request.GatewayPanelPassword)
                ? RandomAlphaNum(24)
                : request.GatewayPanelPassword.Trim();
            var panelBasePath = $"omni{RandomAlphaNum(14).ToLowerInvariant()}";
            var installArgs = BuildInstallArgs(
                request,
                panelUser,
                panelPassword,
                panelBasePath,
                uploadedPanelCertRemotePath,
                uploadedPanelKeyRemotePath,
                uploadedOpenVpnSharedCaCertRemotePath,
                uploadedOpenVpnSharedClientCertRemotePath,
                uploadedOpenVpnSharedClientKeyRemotePath,
                uploadedOpenVpnSharedTlsCryptKeyRemotePath);
            var remoteInstallScriptPath = GetRemoteInstallScriptPath(request);
            var command =
                "set -euo pipefail; " +
                $"chmod +x {ShellQuote(remoteInstallScriptPath)}; " +
                $"sed -i 's/\\r$//' {ShellQuote(remoteInstallScriptPath)} || true; " +
                $"bash -n {ShellQuote(remoteInstallScriptPath)} >/tmp/omnirelay-gatewayctl.syntax.log 2>&1 || {{ cat /tmp/omnirelay-gatewayctl.syntax.log; exit 43; }}; " +
                $"bash {ShellQuote(remoteInstallScriptPath)} {installArgs}";

            using var installTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            installTimeoutCts.CancelAfter(TimeSpan.FromMinutes(20));

            var result = await ExecuteCommandAsync(
                request.Config,
                command,
                sudoPassword,
                line =>
                {
                    var clean = SanitizeTerminalLine(line);
                    if (TryParseProgressLine(clean, out var pct, out var message))
                    {
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = DeploymentPhases.GatewayInstall,
                            Percent = pct,
                            Message = message
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(clean))
                    {
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = DeploymentPhases.GatewayInstall,
                            Percent = 0,
                            Message = $"[vps] {clean}"
                        });
                    }
                },
                installTimeoutCts.Token);

            if (!result.Success)
            {
                return new GatewayOperationResult(false, result.ErrorMessage);
            }

            var panelHost = string.IsNullOrWhiteSpace(request.GatewayPanelDomain)
                ? request.Config.TunnelHost
                : request.GatewayPanelDomain.Trim();
            var panelScheme = request.GatewayPanelSslEnabled ? "https" : "http";
            var panelUrl = $"{panelScheme}://{panelHost}:{request.GatewayPanelPort}/";
            return new GatewayOperationResult(
                true,
                $"Gateway install completed. Panel URL: {panelUrl} | Username: {panelUser} | Password: {panelPassword}",
                panelUrl,
                panelUser,
                panelPassword);
        }
        catch (Exception ex)
        {
            return new GatewayOperationResult(false, $"Gateway install failed: {ex.Message}");
        }
    }

    public Task<GatewayOperationResult> StartGatewayAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "start", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public Task<GatewayOperationResult> StopGatewayAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "stop", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public Task<GatewayOperationResult> UninstallGatewayAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "uninstall", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public Task<GatewayOperationResult> ApplyGatewayDnsAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "dns-apply", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public async Task<GatewayOperationResult> CheckGatewayDnsAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateRequest(request);
            EnsureSudoPassword(sudoPassword);

            var gatewayCtlPath = GetGatewayCtlPath(request);
            var args = BuildCommonArgs(request) + " --json";
            var command =
                "set -euo pipefail; " +
                $"[ -x {ShellQuote(gatewayCtlPath)} ] || {{ echo '{MissingGatewayCtlMessage(request)}'; exit 31; }}; " +
                $"{ShellQuote(gatewayCtlPath)} dns-status {args} {BuildProtocolArgs(request)}";

            var result = await ExecuteCommandAsync(
                request.Config,
                command,
                sudoPassword,
                line =>
                {
                    var clean = SanitizeTerminalLine(line);
                    if (TryParseProgressLine(clean, out var pct, out var message))
                    {
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = DeploymentPhases.GatewayHealth,
                            Percent = pct,
                            Message = message
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(clean) && !clean.StartsWith("{", StringComparison.Ordinal))
                    {
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = DeploymentPhases.GatewayHealth,
                            Percent = 0,
                            Message = $"[vps] {clean}"
                        });
                    }
                },
                cancellationToken);

            if (!result.Success)
            {
                return new GatewayOperationResult(false, result.ErrorMessage);
            }

            var json = ExtractLastJsonLine(result.Output) ?? "{}";
            var dto = JsonSerializer.Deserialize<GatewayHealthDto>(json, JsonOptions) ?? new GatewayHealthDto();
            var ok = dto.DnsConfigPresent && dto.DnsRuleActive && dto.DohReachableViaTunnel;
            return new GatewayOperationResult(ok, ok ? "Gateway DNS path check passed." : "Gateway DNS path check failed.");
        }
        catch (Exception ex)
        {
            return new GatewayOperationResult(false, $"Gateway DNS check failed: {ex.Message}");
        }
    }

    public Task<GatewayOperationResult> RepairGatewayDnsAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "dns-repair", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public async Task<GatewayServiceStatus> GetStatusAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var gatewayCtlPath = GetGatewayCtlPath(request);
        var args = BuildCommonArgs(request) + " --json";
        var command =
            "set -euo pipefail; " +
            $"[ -x {ShellQuote(gatewayCtlPath)} ] || {{ echo '{{\"activeProtocol\":\"vless_tls_singbox\",\"sshState\":\"missing\",\"singBoxState\":\"missing\",\"openVpnState\":\"missing\",\"ipsecState\":\"missing\",\"xl2tpdState\":\"missing\",\"omniPanelState\":\"missing\",\"nginxState\":\"missing\",\"fail2banState\":\"disabled\",\"backendPort\":0,\"publicPort\":0,\"panelPort\":0,\"omniPanelInternalPort\":0,\"backendListener\":false,\"publicListener\":false,\"panelListener\":false,\"omniPanelInternalListener\":false,\"inboundId\":\"\",\"dnsConfigPresent\":false,\"dnsRuleActive\":false,\"dohReachableViaTunnel\":false,\"dnsPathHealthy\":false,\"tunnelHealthy\":false,\"tunnelReason\":\"missing\",\"tunnelBackendProtocol\":\"unknown\",\"tunnelEgressReachable\":false}}'; exit 0; }}; " +
            $"{ShellQuote(gatewayCtlPath)} status {args} {BuildProtocolArgs(request)}";

        var result = await ExecuteCommandAsync(request.Config, command, sudoPassword, null, cancellationToken);
        var json = ExtractLastJsonLine(result.Output);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GatewayServiceStatus
            {
                SshState = result.Success ? "unknown" : "error",
                Fail2BanState = result.Success ? "unknown" : "error"
            };
        }

        var dto = JsonSerializer.Deserialize<GatewayStatusDto>(json, JsonOptions) ?? new GatewayStatusDto();
        return dto.ToModel();
    }

    public async Task<GatewayHealthReport> GetHealthAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var gatewayCtlPath = GetGatewayCtlPath(request);
        var args = BuildCommonArgs(request) + " --json";
        var command =
            "set -euo pipefail; " +
            $"[ -x {ShellQuote(gatewayCtlPath)} ] || {{ echo '{{\"healthy\":false,\"activeProtocol\":\"vless_tls_singbox\",\"sshState\":\"missing\",\"singBoxState\":\"missing\",\"openVpnState\":\"missing\",\"ipsecState\":\"missing\",\"xl2tpdState\":\"missing\",\"omniPanelState\":\"missing\",\"nginxState\":\"missing\",\"fail2banState\":\"disabled\",\"backendPort\":0,\"publicPort\":0,\"panelPort\":0,\"omniPanelInternalPort\":0,\"backendListener\":false,\"publicListener\":false,\"panelListener\":false,\"omniPanelInternalListener\":false,\"inboundId\":\"\",\"dnsConfigPresent\":false,\"dnsRuleActive\":false,\"dohReachableViaTunnel\":false,\"dnsPathHealthy\":false,\"tunnelHealthy\":false,\"tunnelReason\":\"missing\",\"tunnelBackendProtocol\":\"unknown\",\"tunnelEgressReachable\":false}}'; exit 0; }}; " +
            $"{ShellQuote(gatewayCtlPath)} health {args} {BuildProtocolArgs(request)}";

        var result = await ExecuteCommandAsync(
            request.Config,
            command,
            sudoPassword,
            line =>
            {
                if (TryParseProgressLine(line, out var pct, out var message))
                {
                    progress?.Report(new DeploymentProgressSnapshot
                    {
                        Phase = DeploymentPhases.GatewayHealth,
                        Percent = pct,
                        Message = message
                    });
                }
            },
            cancellationToken);

        var json = ExtractLastJsonLine(result.Output) ?? "{}";
        var dto = JsonSerializer.Deserialize<GatewayHealthDto>(json, JsonOptions) ?? new GatewayHealthDto();
        return dto.ToModel(json);
    }

    private async Task EnsureCleanProtocolSwitchAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DeploymentProgressSnapshot
        {
            Phase = DeploymentPhases.GatewayInstall,
            Percent = 2,
            Message = "Checking existing gateway protocol"
        });

        var selectedProtocol = GatewayProtocols.Normalize(request.SelectedGatewayProtocol);
        var probe = await DetectCurrentGatewayProtocolAsync(request, sudoPassword, progress, cancellationToken);
        if (!probe.GatewayCtlPresent)
        {
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 3,
                Message = "No existing gateway controller found; proceeding with install"
            });
            return;
        }

        if (probe.ProtocolDetermined &&
            string.Equals(probe.CurrentProtocol, selectedProtocol, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 3,
                Message = $"Current gateway protocol already '{selectedProtocol}'; uninstall pre-step skipped"
            });
            return;
        }

        var reason = probe.ProtocolDetermined
            ? $"Current protocol '{probe.CurrentProtocol}' differs from selected '{selectedProtocol}'."
            : "Current protocol could not be determined from installed gateway controller.";

        progress?.Report(new DeploymentProgressSnapshot
        {
            Phase = DeploymentPhases.GatewayInstall,
            Percent = 3,
            Message = $"{reason} Running strict uninstall before install"
        });

        var gatewayCtlPath = GetGatewayCtlPath(request);
        // Keep pre-install uninstall backward-compatible with older gatewayctl versions
        // that don't understand newer protocol-specific flags.
        var args = BuildCommonArgs(request, includeBootstrapMode: true).Trim();
        var uninstallCommand =
            "set -euo pipefail; " +
            $"[ -x {ShellQuote(gatewayCtlPath)} ] || {{ echo 'Gateway control script not found during pre-install switch cleanup.'; exit 31; }}; " +
            $"{ShellQuote(gatewayCtlPath)} uninstall {args}";

        var uninstallResult = await ExecuteCommandAsync(
            request.Config,
            uninstallCommand,
            sudoPassword,
            line =>
            {
                var clean = SanitizeTerminalLine(line);
                if (TryParseProgressLine(clean, out var pct, out var message))
                {
                    progress?.Report(new DeploymentProgressSnapshot
                    {
                        Phase = DeploymentPhases.GatewayInstall,
                        Percent = Math.Clamp(pct, 0, 100),
                        Message = $"[switch-uninstall] {message}"
                    });
                }
                else if (!string.IsNullOrWhiteSpace(clean))
                {
                    progress?.Report(new DeploymentProgressSnapshot
                    {
                        Phase = DeploymentPhases.GatewayInstall,
                        Percent = 0,
                        Message = $"[vps] {clean}"
                    });
                }
            },
            cancellationToken);

        if (!uninstallResult.Success)
        {
            throw new InvalidOperationException($"Gateway protocol switch uninstall failed: {uninstallResult.ErrorMessage}");
        }

        progress?.Report(new DeploymentProgressSnapshot
        {
            Phase = DeploymentPhases.GatewayInstall,
            Percent = 4,
            Message = "Strict uninstall completed; continuing with install"
        });
    }

    private async Task EnsureRuntimeSocksBackendReadyForInstallAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsTunnelBootstrapMode(request))
        {
            return;
        }

        var backendPort = request.Config.TunnelRemotePort;
        progress?.Report(new DeploymentProgressSnapshot
        {
            Phase = DeploymentPhases.GatewayInstall,
            Percent = 4,
            Message = $"Checking runtime tunnel backend endpoint (127.0.0.1:{backendPort})"
        });

        var command =
            "set -euo pipefail; " +
            $"port={backendPort}; " +
            "python3 - \"$port\" <<'PY'\n" +
            "import socket\n" +
            "import sys\n" +
            "port = int(sys.argv[1])\n" +
            "def probe_socks() -> bool:\n" +
            "    try:\n" +
            "        with socket.create_connection((\"127.0.0.1\", port), timeout=4) as s:\n" +
            "            s.settimeout(4)\n" +
            "            s.sendall(b\"\\x05\\x01\\x00\")\n" +
            "            data = s.recv(2)\n" +
            "    except Exception:\n" +
            "        return False\n" +
            "    return len(data) == 2 and data[0] == 0x05 and data[1] in (0x00, 0x02)\n" +
            "\n" +
            "if probe_socks():\n" +
            "    print(f\"Runtime backend probe passed on 127.0.0.1:{port} (mode=socks5).\")\n" +
            "    raise SystemExit(0)\n" +
            "try:\n" +
            "    with socket.create_connection((\"127.0.0.1\", port), timeout=4) as s:\n" +
            "        pass\n" +
            "except Exception as ex:\n" +
            "    print(f\"Runtime backend probe failed on 127.0.0.1:{port}: {ex}\")\n" +
            "    raise SystemExit(44)\n" +
            "print(f\"Runtime backend probe failed on 127.0.0.1:{port}: unsupported proxy protocol (expected socks5)\")\n" +
            "raise SystemExit(44)\n" +
            "PY";

        void ReportLine(string line)
        {
            var clean = SanitizeTerminalLine(line);
            if (string.IsNullOrWhiteSpace(clean))
            {
                return;
            }

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 0,
                Message = $"[vps] {clean}"
            });
        }

        var probeResult = await ExecuteCommandAsync(
            request.Config,
            command,
            sudoPassword,
            ReportLine,
            cancellationToken);

        if (!probeResult.Success)
        {
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 4,
                Message = $"Runtime backend probe failed; trying tunnel remediation (127.0.0.1:{backendPort})"
            });

            var recoveryResult = await TryRecoverRuntimeSocksBackendForInstallAsync(
                request,
                sudoPassword,
                backendPort,
                command,
                ReportLine,
                progress,
                cancellationToken);

            if (!recoveryResult.Success)
            {
                throw new InvalidOperationException(
                    $"Runtime tunnel backend preflight failed on 127.0.0.1:{backendPort}. Initial error: {probeResult.ErrorMessage}. Recovery error: {recoveryResult.ErrorMessage}");
            }
        }

        progress?.Report(new DeploymentProgressSnapshot
        {
            Phase = DeploymentPhases.GatewayInstall,
            Percent = 4,
            Message = $"Runtime tunnel backend check passed (127.0.0.1:{backendPort})"
        });
    }

    private async Task<CommandExecutionResult> TryRecoverRuntimeSocksBackendForInstallAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        int backendPort,
        string probeCommand,
        Action<string>? onLine,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var tunnelCtlQuoted = ShellQuote(TunnelCtlPath);
        var levels = new[] { "soft", "hard" };
        CommandExecutionResult? lastProbeResult = null;
        CommandExecutionResult? lastRemediateResult = null;

        foreach (var level in levels)
        {
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 4,
                Message = $"Running tunnel remediation ({level}) for backend 127.0.0.1:{backendPort}"
            });

            var remediateCommand =
                "set -euo pipefail; " +
                $"[ -x {tunnelCtlQuoted} ] || {{ echo 'tunnelctl is missing on VPS.'; exit 45; }}; " +
                $"{tunnelCtlQuoted} remediate --level {ShellQuote(level)} --backend-host 127.0.0.1 --backend-port {backendPort} --json || true; " +
                $"{tunnelCtlQuoted} probe --backend-host 127.0.0.1 --backend-port {backendPort} --json || true";

            lastRemediateResult = await ExecuteCommandAsync(
                request.Config,
                remediateCommand,
                sudoPassword,
                onLine,
                cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            lastProbeResult = await ExecuteCommandAsync(
                request.Config,
                probeCommand,
                sudoPassword,
                onLine,
                cancellationToken);

            if (lastProbeResult.Success)
            {
                return lastProbeResult;
            }
        }

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 4,
                Message = $"Waiting for runtime backend recovery (attempt {attempt}/8)"
            });

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            lastProbeResult = await ExecuteCommandAsync(
                request.Config,
                probeCommand,
                sudoPassword,
                onLine,
                cancellationToken);
            if (lastProbeResult.Success)
            {
                return lastProbeResult;
            }
        }

        var error = lastProbeResult?.ErrorMessage;
        if (string.IsNullOrWhiteSpace(error))
        {
            error = lastRemediateResult?.ErrorMessage;
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            error = "Runtime backend recovery attempts did not restore 127.0.0.1 listener.";
        }

        return new CommandExecutionResult(false, lastProbeResult?.Output ?? string.Empty, error);
    }

    private async Task<(bool GatewayCtlPresent, bool ProtocolDetermined, string? CurrentProtocol)> DetectCurrentGatewayProtocolAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var gatewayCtlPath = GetGatewayCtlPath(request);
        var relayId = NormalizeRelayId(request.RelayId);
        var metadataFile = string.IsNullOrWhiteSpace(relayId)
            ? "/etc/omnirelay/gateway/metadata.json"
            : $"/etc/omnirelay/relays/{relayId}/gateway/metadata.json";
        var command =
            "set -euo pipefail; " +
            $"if [ ! -x {ShellQuote(gatewayCtlPath)} ]; then echo '__OMNIRELAY_NO_GATEWAYCTL__'; exit 0; fi; " +
            $"if [ -s {ShellQuote(metadataFile)} ]; then " +
            $"  proto=$(jq -r '.protocol // empty' {ShellQuote(metadataFile)} 2>/dev/null | tr -d '\\r\\n' | xargs || true); " +
            "  if [ -n \"$proto\" ]; then echo \"__OMNIRELAY_PROTO__:${proto}\"; exit 0; fi; " +
            "fi; " +
            "echo '__OMNIRELAY_PROTOCOL_UNKNOWN__';";

        var result = await ExecuteCommandAsync(
            request.Config,
            command,
            sudoPassword,
            line =>
            {
                var clean = SanitizeTerminalLine(line);
                if (string.IsNullOrWhiteSpace(clean) || clean.StartsWith("__OMNIRELAY_", StringComparison.Ordinal))
                {
                    return;
                }

                progress?.Report(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayInstall,
                    Percent = 0,
                    Message = $"[vps] {clean}"
                });
            },
            cancellationToken);

        if (!result.Success)
        {
            return (false, false, null);
        }

        var lines = result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Any(static line => string.Equals(line, "__OMNIRELAY_NO_GATEWAYCTL__", StringComparison.Ordinal)))
        {
            return (false, false, null);
        }

        var protoLine = lines.FirstOrDefault(static line => line.StartsWith("__OMNIRELAY_PROTO__:", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(protoLine))
        {
            var rawProto = protoLine["__OMNIRELAY_PROTO__:".Length..].Trim();
            if (TryNormalizeKnownProtocol(rawProto, out var normalized))
            {
                return (true, true, normalized);
            }
        }

        var statusLine = lines.FirstOrDefault(static line => line.StartsWith("__OMNIRELAY_STATUS__:", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(statusLine))
        {
            var rawStatusJson = statusLine["__OMNIRELAY_STATUS__:".Length..].Trim();
            if (TryExtractKnownProtocolFromStatusJson(rawStatusJson, out var normalized))
            {
                return (true, true, normalized);
            }
        }

        var trailingJson = ExtractLastJsonLine(result.Output);
        if (!string.IsNullOrWhiteSpace(trailingJson) &&
            TryExtractKnownProtocolFromStatusJson(trailingJson, out var trailingNormalized))
        {
            return (true, true, trailingNormalized);
        }

        return (true, false, null);
    }

    private static bool TryExtractKnownProtocolFromStatusJson(string? json, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("activeProtocol", out var activeProtocolElement))
            {
                return false;
            }

            return TryNormalizeKnownProtocol(activeProtocolElement.GetString(), out normalized);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizeKnownProtocol(string? protocol, out string normalized)
    {
        normalized = string.Empty;
        var raw = (protocol ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        foreach (var known in GatewayProtocols.All)
        {
            if (string.Equals(raw, known.Value, StringComparison.OrdinalIgnoreCase))
            {
                normalized = known.Value;
                return true;
            }
        }

        return false;
    }

    private async Task UploadInstallerScriptAsync(
        GatewayDeploymentRequest request,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var localScript = ResolveInstallerScriptPath(request.SelectedGatewayProtocol);
        await UploadFileAsync(request, localScript, GetRemoteInstallScriptPath(request), progress, cancellationToken);
    }

    private async Task UploadOmniPanelCommonScriptAsync(
        GatewayDeploymentRequest request,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var localScript = ResolveOmniPanelCommonScriptPath();
        await UploadFileAsync(request, localScript, RemoteOmniPanelCommonScriptPath, progress, cancellationToken);
    }

    private async Task UploadBootstrapCommonScriptAsync(
        GatewayDeploymentRequest request,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var localScript = ResolveBootstrapCommonScriptPath();
        await UploadFileAsync(request, localScript, RemoteBootstrapCommonScriptPath, progress, cancellationToken);
    }

    private async Task UploadTunnelModuleScriptAsync(
        GatewayDeploymentRequest request,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var localScript = ResolveTunnelModuleScriptPath();
        await UploadFileAsync(request, localScript, RemoteTunnelModuleScriptPath, progress, cancellationToken);
    }

    private async Task UploadSingBoxConnectorCommonScriptAsync(
        GatewayDeploymentRequest request,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var localScript = ResolveSingBoxConnectorCommonScriptPath();
        await UploadFileAsync(request, localScript, RemoteSingBoxConnectorCommonScriptPath, progress, cancellationToken);
    }

    private async Task EnsureTunnelModuleInstalledAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        string phase,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DeploymentProgressSnapshot
        {
            Phase = phase,
            Percent = 4,
            Message = "Ensuring tunnel watchdog module"
        });

        await UploadTunnelModuleScriptAsync(request, progress, cancellationToken);

        const string probeUrl = "https://1.1.1.1/cdn-cgi/trace";
        var command =
            "set -euo pipefail; " +
            $"chmod +x {ShellQuote(RemoteTunnelModuleScriptPath)}; " +
            $"sed -i 's/\\r$//' {ShellQuote(RemoteTunnelModuleScriptPath)} || true; " +
            $"bash -n {ShellQuote(RemoteTunnelModuleScriptPath)} >/tmp/omnirelay-tunnelctl.syntax.log 2>&1 || {{ cat /tmp/omnirelay-tunnelctl.syntax.log; exit 43; }}; " +
            $"bash {ShellQuote(RemoteTunnelModuleScriptPath)} install " +
            $"--backend-host '127.0.0.1' " +
            $"--backend-port {request.Config.TunnelRemotePort} " +
            $"--probe-url {ShellQuote(probeUrl)} " +
            $"--timeout 12 --json; " +
            $"[ -x {ShellQuote(TunnelCtlPath)} ] || {{ echo 'Tunnel module did not install correctly.'; exit 44; }}; " +
            $"sed -i 's/\\r$//' {ShellQuote(TunnelCtlPath)} || true; " +
            $"/usr/bin/env bash {ShellQuote(TunnelCtlPath)} status --json >/tmp/omnirelay-tunnelctl.status.log 2>&1 || {{ cat /tmp/omnirelay-tunnelctl.status.log; exit 45; }}";

        var result = await ExecuteCommandAsync(
            request.Config,
            command,
            sudoPassword,
            line =>
            {
                var clean = SanitizeTerminalLine(line);
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    progress?.Report(new DeploymentProgressSnapshot
                    {
                        Phase = phase,
                        Percent = 0,
                        Message = $"[vps] {clean}"
                    });
                }
            },
            cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to install tunnel module: {result.ErrorMessage}");
        }
    }

    private async Task UploadFileAsync(
        GatewayDeploymentRequest request,
        string localPath,
        string remotePath,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        string? normalizedTempPath = null;
        var uploadPath = localPath;
        if (IsShellScriptPath(localPath))
        {
            uploadPath = NormalizeScriptToLfTempCopy(localPath, out normalizedTempPath);
        }

        ProcessStartInfo? BuildStartInfo(out string bindIp, out string? error)
        {
            if (!SshCliStartInfoFactory.TryCreateBoundScpUploadStartInfo(
                    request.Config,
                    uploadPath,
                    remotePath,
                    out var startInfo,
                    out bindIp,
                    out error))
            {
                return null;
            }

            return startInfo;
        }

        var startInfo = BuildStartInfo(out var bindIp, out var buildError);
        if (startInfo is null)
        {
            throw new InvalidOperationException(buildError ?? "Cannot prepare SSH/SCP command for IC1 gateway operation.");
        }

        progress?.Report(new DeploymentProgressSnapshot
        {
            Phase = DeploymentPhases.GatewayInstall,
            Percent = 8,
            Message = $"IC1 adapter IPv4 resolved as {bindIp} for installer upload"
        });

        try
        {
            var result = await RunCliWithHostKeyRepairAsync(
                request.Config,
                () =>
                {
                    var psi = BuildStartInfo(out _, out var retryError);
                    return (psi, retryError);
                },
                line =>
                {
                    var clean = SanitizeTerminalLine(line);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = DeploymentPhases.GatewayInstall,
                            Percent = 0,
                            Message = $"[vps] {clean}"
                        });
                    }
                },
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(FirstMeaningfulLine(result.Output, string.Empty));
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(normalizedTempPath))
            {
                try
                {
                    File.Delete(normalizedTempPath);
                }
                catch
                {
                    // no-op
                }
            }
        }
    }

    private static bool IsShellScriptPath(string path)
    {
        return string.Equals(Path.GetExtension(path), ".sh", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeScriptToLfTempCopy(string sourcePath, out string tempPath)
    {
        var content = File.ReadAllText(sourcePath);
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        tempPath = Path.Combine(Path.GetTempPath(), $"omnirelay-script-{Guid.NewGuid():N}.sh");
        File.WriteAllText(tempPath, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return tempPath;
    }

    private static string ResolveInstallerScriptPath(string selectedGatewayProtocol)
    {
        var baseDir = AppContext.BaseDirectory;
        var normalizedProtocol = GatewayProtocols.Normalize(selectedGatewayProtocol);
        var scriptFileName = normalizedProtocol switch
        {
            var protocol when string.Equals(protocol, GatewayProtocols.VlessPlainSingbox, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_singbox.sh",
            var protocol when string.Equals(protocol, GatewayProtocols.ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_singbox.sh",
            var protocol when string.Equals(protocol, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_singbox.sh",
            var protocol when string.Equals(protocol, GatewayProtocols.OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_openvpn_singbox.sh",
            var protocol when string.Equals(protocol, GatewayProtocols.IpsecL2tpSingbox, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_ipsec_l2tp_singbox.sh",
            _ => "setup_omnirelay_vps_singbox.sh"
        };
        var candidates = new[]
        {
            Path.Combine(baseDir, "GatewayScripts", scriptFileName),
            Path.Combine(baseDir, scriptFileName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "scripts", scriptFileName))
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Gateway installer script not found. Expected {scriptFileName} in app GatewayScripts content.");
        }

        return path;
    }

    private static string ResolveOmniPanelCommonScriptPath()
    {
        var baseDir = AppContext.BaseDirectory;
        const string scriptFileName = "setup_omnirelay_omnipanel_common.sh";
        var candidates = new[]
        {
            Path.Combine(baseDir, "GatewayScripts", scriptFileName),
            Path.Combine(baseDir, scriptFileName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "scripts", scriptFileName))
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Shared OmniPanel script not found. Expected {scriptFileName} in app GatewayScripts content.");
        }

        return path;
    }

    private static string ResolveTunnelModuleScriptPath()
    {
        var baseDir = AppContext.BaseDirectory;
        const string scriptFileName = "setup_omnirelay_gateway_tunnel_module.sh";
        var candidates = new[]
        {
            Path.Combine(baseDir, "GatewayScripts", scriptFileName),
            Path.Combine(baseDir, scriptFileName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "scripts", scriptFileName))
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Tunnel module script not found. Expected {scriptFileName} in app GatewayScripts content.");
        }

        return path;
    }

    private static string ResolveBootstrapCommonScriptPath()
    {
        var baseDir = AppContext.BaseDirectory;
        const string scriptFileName = "setup_omnirelay_gateway_bootstrap_common.sh";
        var candidates = new[]
        {
            Path.Combine(baseDir, "GatewayScripts", scriptFileName),
            Path.Combine(baseDir, scriptFileName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "scripts", scriptFileName))
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Bootstrap helper script not found. Expected {scriptFileName} in app GatewayScripts content.");
        }

        return path;
    }

    private static string ResolveSingBoxConnectorCommonScriptPath()
    {
        var baseDir = AppContext.BaseDirectory;
        const string scriptFileName = "setup_omnirelay_gateway_singbox_connector_common.sh";
        var candidates = new[]
        {
            Path.Combine(baseDir, "GatewayScripts", scriptFileName),
            Path.Combine(baseDir, scriptFileName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "scripts", scriptFileName))
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Sing-box connector helper script not found. Expected {scriptFileName} in app GatewayScripts content.");
        }

        return path;
    }

    private static string GetGatewayCtlPath(GatewayDeploymentRequest request)
    {
        var relayId = NormalizeRelayId(request.RelayId);
        return string.IsNullOrWhiteSpace(relayId)
            ? GatewayCtlPath
            : $"/usr/local/sbin/omnirelay-gatewayctl-{relayId}";
    }

    private static string GetRemoteInstallScriptPath(GatewayDeploymentRequest request)
    {
        var relayId = NormalizeRelayId(request.RelayId);
        return string.IsNullOrWhiteSpace(relayId)
            ? RemoteInstallScriptPath
            : $"/tmp/omnirelay-gatewayctl-{relayId}.sh";
    }

    private static string GetRemoteUploadedPanelCertPath(GatewayDeploymentRequest request)
    {
        var relayId = NormalizeRelayId(request.RelayId);
        return string.IsNullOrWhiteSpace(relayId)
            ? RemoteUploadedPanelCertPath
            : $"/tmp/omnirelay-omnipanel-upload-{relayId}.crt";
    }

    private static string GetRemoteUploadedPanelKeyPath(GatewayDeploymentRequest request)
    {
        var relayId = NormalizeRelayId(request.RelayId);
        return string.IsNullOrWhiteSpace(relayId)
            ? RemoteUploadedPanelKeyPath
            : $"/tmp/omnirelay-omnipanel-upload-{relayId}.key";
    }

    private static string MissingGatewayCtlMessage(GatewayDeploymentRequest request)
    {
        var relayId = NormalizeRelayId(request.RelayId);
        return string.IsNullOrWhiteSpace(relayId)
            ? "Gateway control script is not installed on VPS. Run Install Gateway first."
            : $"Gateway control script for relay '{relayId}' is not installed on VPS. Run Install Gateway for this relay first.";
    }

    private static string NormalizeRelayId(string? relayId)
    {
        return (relayId ?? string.Empty).Trim();
    }

    private async Task<GatewayOperationResult> RunSimpleGatewayCommandAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        string operation,
        string phase,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateRequest(request);
            EnsureSudoPassword(sudoPassword);
            await EnsureTunnelModuleInstalledAsync(request, sudoPassword, phase, progress, cancellationToken);

            var gatewayCtlPath = GetGatewayCtlPath(request);
            var args = $"{BuildCommonArgs(request)} {BuildProtocolArgs(request)}".Trim();
            var command =
                "set -euo pipefail; " +
                $"[ -x {ShellQuote(gatewayCtlPath)} ] || {{ echo '{MissingGatewayCtlMessage(request)}'; exit 31; }}; " +
                $"{ShellQuote(gatewayCtlPath)} {operation} {args}";

            var result = await ExecuteCommandAsync(
                request.Config,
                command,
                sudoPassword,
                line =>
                {
                    var clean = SanitizeTerminalLine(line);
                    if (TryParseProgressLine(clean, out var pct, out var message))
                    {
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = phase,
                            Percent = pct,
                            Message = message
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(clean))
                    {
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = phase,
                            Percent = 0,
                            Message = $"[vps] {clean}"
                        });
                    }
                },
                cancellationToken);

            return result.Success
                ? new GatewayOperationResult(true, $"Gateway {operation} completed.")
                : new GatewayOperationResult(false, result.ErrorMessage);
        }
        catch (Exception ex)
        {
            return new GatewayOperationResult(false, $"Gateway {operation} failed: {ex.Message}");
        }
    }

    private async Task<CommandExecutionResult> ExecuteCommandAsync(
        ServiceConfig config,
        string command,
        string sudoPassword,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var normalizedCommand = NormalizeShellCommand(command);
        var wrapped = WrapCommand(normalizedCommand, sudoPassword);
        ProcessStartInfo? BuildStartInfo(out string bindIp, out string? error)
        {
            if (!SshCliStartInfoFactory.TryCreateBoundSshCommandStartInfo(
                    config,
                    wrapped,
                    out var startInfo,
                    out bindIp,
                    out error))
            {
                return null;
            }

            return startInfo;
        }

        var startInfo = BuildStartInfo(out var bindIp, out var error);
        if (startInfo is null)
        {
            return new CommandExecutionResult(false, string.Empty, error ?? "Cannot prepare SSH command for IC1 gateway operation.");
        }

        onLine?.Invoke($"IC1 adapter IPv4 resolved as {bindIp} for gateway SSH operation");
        var result = await RunCliWithHostKeyRepairAsync(
            config,
            () =>
            {
                var psi = BuildStartInfo(out _, out var retryError);
                return (psi, retryError);
            },
            onLine,
            cancellationToken);
        var success = result.ExitCode == 0;
        var errorMessage = success ? string.Empty : FirstMeaningfulLine(result.Output, string.Empty);
        return new CommandExecutionResult(success, result.Output, errorMessage);
    }

    private static string NormalizeShellCommand(string command)
    {
        return (command ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private async Task<CliExecutionResult> RunCliWithHostKeyRepairAsync(
        ServiceConfig config,
        Func<(ProcessStartInfo? StartInfo, string? Error)> startInfoFactory,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var (startInfo, error) = startInfoFactory();
        if (startInfo is null)
        {
            return new CliExecutionResult(255, error ?? "Cannot prepare SSH command for IC1 gateway operation.");
        }

        var first = await RunCliProcessAsync(startInfo, onLine, cancellationToken);
        if (first.ExitCode == 0 || !SshCliStartInfoFactory.LooksLikeHostKeyMismatch(first.Output))
        {
            return first;
        }

        onLine?.Invoke("Detected SSH host-key change; removing stale known_hosts entry and retrying once.");
        var repair = await SshCliStartInfoFactory.TryRepairKnownHostEntryAsync(config, cancellationToken);
        onLine?.Invoke(repair.Message);
        if (!repair.Success)
        {
            return first;
        }

        var (retryStartInfo, retryError) = startInfoFactory();
        if (retryStartInfo is null)
        {
            var merged = string.IsNullOrWhiteSpace(retryError)
                ? first.Output
                : $"{first.Output}{Environment.NewLine}{retryError}";
            return new CliExecutionResult(255, merged);
        }

        var second = await RunCliProcessAsync(retryStartInfo, onLine, cancellationToken);
        var mergedOutput = $"{first.Output}{Environment.NewLine}{second.Output}";
        return new CliExecutionResult(second.ExitCode, mergedOutput);
    }

    private static async Task<CliExecutionResult> RunCliProcessAsync(
        ProcessStartInfo startInfo,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Cannot start process '{startInfo.FileName}' for gateway SSH operation.");
        }

        process.StandardInput.Close();

        var output = new StringBuilder();
        var stdoutTask = PumpReaderAsync(process.StandardOutput, output, onLine, cancellationToken);
        var stderrTask = PumpReaderAsync(process.StandardError, output, onLine, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
            return new CliExecutionResult(process.ExitCode, output.ToString());
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process);
            throw;
        }
    }

    private static async Task PumpReaderAsync(
        StreamReader reader,
        StringBuilder output,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            output.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    private static string BuildInstallArgs(
        GatewayDeploymentRequest request,
        string panelUser,
        string panelPassword,
        string panelBasePath,
        string panelCertRemotePath,
        string panelKeyRemotePath,
        string openVpnSharedCaCertRemotePath,
        string openVpnSharedClientCertRemotePath,
        string openVpnSharedClientKeyRemotePath,
        string openVpnSharedTlsCryptKeyRemotePath)
    {
        var args =
            $"install {BuildCommonArgs(request, includeBootstrapMode: true)} {BuildProtocolArgs(request)} " +
            $"--tunnel-auth {ShellQuote(MapTunnelAuth(request.Config))} " +
            $"--panel-user {ShellQuote(panelUser)} " +
            $"--panel-password {ShellQuote(panelPassword)} " +
            $"--panel-base-path {ShellQuote(panelBasePath)} " +
            $"--panel-domain {ShellQuote(request.GatewayPanelDomain.Trim())} " +
            $"--panel-domain-only {(request.GatewayPanelDomainOnly ? "true" : "false")} " +
            $"--panel-ssl {(request.GatewayPanelSslEnabled ? "true" : "false")} " +
            $"--panel-ssl-mode {ShellQuote(NormalizePanelSslMode(request.GatewayPanelSslMode))} " +
            $"--panel-cert-file {ShellQuote(panelCertRemotePath)} " +
            $"--panel-key-file {ShellQuote(panelKeyRemotePath)}";

        var selectedProtocol = GatewayProtocols.Normalize(request.SelectedGatewayProtocol);
        if (string.Equals(selectedProtocol, GatewayProtocols.OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            args +=
                $" --openvpn-shared-ca-cert-file {ShellQuote(openVpnSharedCaCertRemotePath)}" +
                $" --openvpn-shared-client-cert-file {ShellQuote(openVpnSharedClientCertRemotePath)}" +
                $" --openvpn-shared-client-key-file {ShellQuote(openVpnSharedClientKeyRemotePath)}" +
                $" --openvpn-shared-tls-crypt-key-file {ShellQuote(openVpnSharedTlsCryptKeyRemotePath)}";
        }

        return args;
    }

    private static string BuildCommonArgs(GatewayDeploymentRequest request, bool includeBootstrapMode = false)
    {
        var args = new List<string>
        {
            "--public-port", request.GatewayPublicPort.ToString(),
            "--panel-port", request.GatewayPanelPort.ToString(),
            "--backend-port", request.Config.TunnelRemotePort.ToString(),
            "--ssh-port", request.Config.TunnelSshPort.ToString(),
            "--bootstrap-socks-port", request.Config.BootstrapSocksRemotePort.ToString(),
            "--vps-ip", ShellQuote(request.Config.TunnelHost.Trim()),
            "--tunnel-user", ShellQuote(request.Config.TunnelUser.Trim()),
            "--doh-endpoints", ShellQuote(request.GatewayDohEndpoints.Trim())
        };

        var relayId = NormalizeRelayId(request.RelayId);
        if (!string.IsNullOrWhiteSpace(relayId))
        {
            args.Add("--relay-id");
            args.Add(ShellQuote(relayId));
        }

        if (includeBootstrapMode)
        {
            args.Add("--bootstrap-mode");
            args.Add(ShellQuote(NormalizeBootstrapMode(request.BootstrapMode)));
        }

        return string.Join(" ", args);
    }

    private static string BuildProtocolArgs(GatewayDeploymentRequest request)
    {
        var selectedProtocol = GatewayProtocols.Normalize(request.SelectedGatewayProtocol);
        var args = new List<string>();

        if (string.Equals(selectedProtocol, GatewayProtocols.VlessTlsSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.MixedSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.SocksSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.HttpSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.Hysteria2Singbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.TrojanSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.NaiveSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--protocol");
            args.Add(ShellQuote(selectedProtocol));
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.VlessTlsSingbox, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--tls-enabled");
            args.Add(request.GatewayProtocolTlsEnabled ? "true" : "false");
            args.Add("--tls-server-name");
            args.Add(ShellQuote(request.GatewayProtocolTlsServerName.Trim()));
            args.Add("--tls-cert-file");
            args.Add(ShellQuote(request.GatewayProtocolCertPath.Trim()));
            args.Add("--tls-key-file");
            args.Add(ShellQuote(request.GatewayProtocolKeyPath.Trim()));
            args.Add("--vless-flow");
            args.Add(ShellQuote(request.GatewayProtocolTlsEnabled ? request.VlessTlsFlow.Trim() : string.Empty));
            return string.Join(" ", args);
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--camouflage-server");
            args.Add(ShellQuote(request.ShadowTlsCamouflageServer.Trim()));
            args.Add("--shadowtls-strict-mode");
            args.Add(request.ShadowTlsStrictMode ? "true" : "false");
            args.Add("--shadowtls-wildcard-sni");
            args.Add(ShellQuote(request.ShadowTlsWildcardSni.Trim()));
            return string.Join(" ", args);
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.MixedSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.SocksSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.HttpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--proxy-username");
            args.Add(ShellQuote(request.GatewayProxyUsername.Trim()));
            args.Add("--proxy-password");
            args.Add(ShellQuote(request.GatewayProxyPassword));
            return string.Join(" ", args);
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.Hysteria2Singbox, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--tls-enabled");
            args.Add(request.GatewayProtocolTlsEnabled ? "true" : "false");
            args.Add("--tls-server-name");
            args.Add(ShellQuote(request.GatewayProtocolTlsServerName.Trim()));
            args.Add("--tls-cert-file");
            args.Add(ShellQuote(request.GatewayProtocolCertPath.Trim()));
            args.Add("--tls-key-file");
            args.Add(ShellQuote(request.GatewayProtocolKeyPath.Trim()));
            args.Add("--proxy-password");
            args.Add(ShellQuote(request.GatewayProxyPassword));
            args.Add("--hysteria2-up-mbps");
            args.Add(request.Hysteria2UpMbps.ToString());
            args.Add("--hysteria2-down-mbps");
            args.Add(request.Hysteria2DownMbps.ToString());
            args.Add("--hysteria2-obfs-password");
            args.Add(ShellQuote(request.Hysteria2ObfsPassword.Trim()));
            args.Add("--hysteria2-ignore-client-bandwidth");
            args.Add(request.Hysteria2IgnoreClientBandwidth ? "true" : "false");
            args.Add("--hysteria2-masquerade-url");
            args.Add(ShellQuote(request.Hysteria2MasqueradeUrl.Trim()));
            return string.Join(" ", args);
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.TrojanSingbox, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--tls-enabled");
            args.Add(request.GatewayProtocolTlsEnabled ? "true" : "false");
            args.Add("--tls-server-name");
            args.Add(ShellQuote(request.GatewayProtocolTlsServerName.Trim()));
            args.Add("--tls-cert-file");
            args.Add(ShellQuote(request.GatewayProtocolCertPath.Trim()));
            args.Add("--tls-key-file");
            args.Add(ShellQuote(request.GatewayProtocolKeyPath.Trim()));
            args.Add("--proxy-password");
            args.Add(ShellQuote(request.GatewayProxyPassword));
            return string.Join(" ", args);
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.NaiveSingbox, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--tls-enabled");
            args.Add(request.GatewayProtocolTlsEnabled ? "true" : "false");
            args.Add("--tls-server-name");
            args.Add(ShellQuote(request.GatewayProtocolTlsServerName.Trim()));
            args.Add("--tls-cert-file");
            args.Add(ShellQuote(request.GatewayProtocolCertPath.Trim()));
            args.Add("--tls-key-file");
            args.Add(ShellQuote(request.GatewayProtocolKeyPath.Trim()));
            args.Add("--proxy-username");
            args.Add(ShellQuote(request.GatewayProxyUsername.Trim()));
            args.Add("--proxy-password");
            args.Add(ShellQuote(request.GatewayProxyPassword));
            args.Add("--naive-network");
            args.Add(ShellQuote(request.NaiveNetwork.Trim()));
            args.Add("--naive-quic-cc");
            args.Add(ShellQuote(request.NaiveQuicCongestionControl.Trim()));
            return string.Join(" ", args);
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return string.Join(" ", args);
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.IpsecL2tpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--ipsec-network");
            args.Add(ShellQuote(request.IpsecL2tpNetwork.Trim()));
            return string.Join(" ", args);
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--openvpn-network");
            args.Add(ShellQuote(request.OpenVpnNetwork.Trim()));
            return string.Join(" ", args);
        }

        return string.Join(" ", args);
    }

    private static string NormalizePanelSslMode(string? value)
    {
        return string.Equals(value?.Trim(), "uploaded", StringComparison.OrdinalIgnoreCase)
            ? "uploaded"
            : "letsencrypt";
    }

    private static string MapTunnelAuth(ServiceConfig config)
    {
        var normalized = TunnelAuthMethods.Normalize(config.TunnelAuthMethod);
        return string.Equals(normalized, TunnelAuthMethods.Password, StringComparison.Ordinal)
            ? "password"
            : "host_key";
    }

    private static void ValidateRequest(GatewayDeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Config);

        var relayId = NormalizeRelayId(request.RelayId);
        if (!string.IsNullOrWhiteSpace(relayId) && !Regex.IsMatch(relayId, "^[A-Za-z0-9_-]{1,64}$"))
        {
            throw new InvalidOperationException("Relay id may contain only letters, numbers, underscore, or dash and must be at most 64 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Config.TunnelHost))
        {
            throw new InvalidOperationException("Tunnel host is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Config.TunnelUser))
        {
            throw new InvalidOperationException("Tunnel user is required.");
        }

        _ = NormalizeBootstrapMode(request.BootstrapMode);

        if (request.GatewayPublicPort <= 0 || request.GatewayPublicPort > 65535)
        {
            throw new InvalidOperationException("Gateway public port is invalid.");
        }

        if (request.GatewayPanelPort <= 0 || request.GatewayPanelPort > 65535)
        {
            throw new InvalidOperationException("Gateway panel port is invalid.");
        }

        if (request.Config.TunnelRemotePort <= 0 || request.Config.TunnelRemotePort > 65535)
        {
            throw new InvalidOperationException("Tunnel remote port is invalid.");
        }

        if (request.Config.BootstrapSocksRemotePort <= 0 || request.Config.BootstrapSocksRemotePort > 65535)
        {
            throw new InvalidOperationException("Bootstrap SOCKS remote port is invalid.");
        }

        if (request.GatewayPublicPort == request.GatewayPanelPort)
        {
            throw new InvalidOperationException("Gateway public and panel ports must be different.");
        }

        if ((request.GatewayPanelDomainOnly || request.GatewayPanelSslEnabled) &&
            string.IsNullOrWhiteSpace(request.GatewayPanelDomain))
        {
            throw new InvalidOperationException("OmniPanel domain is required when Domain Only or SSL is enabled.");
        }

        var panelSslMode = NormalizePanelSslMode(request.GatewayPanelSslMode);
        if (request.GatewayPanelSslEnabled && panelSslMode == "uploaded")
        {
            if (string.IsNullOrWhiteSpace(request.GatewayPanelCertLocalPath) ||
                string.IsNullOrWhiteSpace(request.GatewayPanelKeyLocalPath))
            {
                throw new InvalidOperationException("Uploaded OmniPanel SSL mode requires both certificate and private key files.");
            }

            if (!File.Exists(request.GatewayPanelCertLocalPath))
            {
                throw new InvalidOperationException("Uploaded OmniPanel certificate file does not exist.");
            }

            if (!File.Exists(request.GatewayPanelKeyLocalPath))
            {
                throw new InvalidOperationException("Uploaded OmniPanel private key file does not exist.");
            }
        }

        var selectedProtocol = GatewayProtocols.Normalize(request.SelectedGatewayProtocol);
        if (string.Equals(selectedProtocol, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.ShadowTlsCamouflageServer))
            {
                throw new InvalidOperationException("ShadowTLS camouflage server is required.");
            }
        }
        else if (string.Equals(selectedProtocol, GatewayProtocols.OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase)
                 && string.IsNullOrWhiteSpace(request.OpenVpnNetwork))
        {
            throw new InvalidOperationException("OpenVPN tunnel network is required.");
        }
        else if (string.Equals(selectedProtocol, GatewayProtocols.OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase)
                 && !IsValidIpv4Cidr(request.OpenVpnNetwork))
        {
            throw new InvalidOperationException("OpenVPN tunnel network must be a valid IPv4 CIDR.");
        }
        else if (string.Equals(selectedProtocol, GatewayProtocols.IpsecL2tpSingbox, StringComparison.OrdinalIgnoreCase)
                 && string.IsNullOrWhiteSpace(request.IpsecL2tpNetwork))
        {
            throw new InvalidOperationException("IPSec/L2TP tunnel network is required.");
        }
        else if (string.Equals(selectedProtocol, GatewayProtocols.IpsecL2tpSingbox, StringComparison.OrdinalIgnoreCase)
                 && !IsValidIpv4Cidr(request.IpsecL2tpNetwork))
        {
            throw new InvalidOperationException("IPSec/L2TP tunnel network must be a valid IPv4 CIDR.");
        }
        else if (string.Equals(selectedProtocol, GatewayProtocols.OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.OpenVpnSharedCaCertLocalPath) ||
                string.IsNullOrWhiteSpace(request.OpenVpnSharedClientCertLocalPath) ||
                string.IsNullOrWhiteSpace(request.OpenVpnSharedClientKeyLocalPath) ||
                string.IsNullOrWhiteSpace(request.OpenVpnSharedTlsCryptKeyLocalPath))
            {
                throw new InvalidOperationException("OpenVPN shared bundle files are required (CA cert, client cert, client key, tls-crypt key).");
            }
            if (!File.Exists(request.OpenVpnSharedCaCertLocalPath) ||
                !File.Exists(request.OpenVpnSharedClientCertLocalPath) ||
                !File.Exists(request.OpenVpnSharedClientKeyLocalPath) ||
                !File.Exists(request.OpenVpnSharedTlsCryptKeyLocalPath))
            {
                throw new InvalidOperationException("One or more OpenVPN shared bundle files do not exist.");
            }
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.TrojanSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.Hysteria2Singbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.NaiveSingbox, StringComparison.OrdinalIgnoreCase))
        {
            if (!request.GatewayProtocolTlsEnabled)
            {
                throw new InvalidOperationException("TLS must be enabled for selected protocol.");
            }

            if (string.IsNullOrWhiteSpace(request.GatewayProtocolCertPath) || string.IsNullOrWhiteSpace(request.GatewayProtocolKeyPath))
            {
                throw new InvalidOperationException("Protocol TLS certificate and key are required.");
            }
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.VlessTlsSingbox, StringComparison.OrdinalIgnoreCase)
            && request.GatewayProtocolTlsEnabled
            && (string.IsNullOrWhiteSpace(request.GatewayProtocolCertPath) || string.IsNullOrWhiteSpace(request.GatewayProtocolKeyPath)))
        {
            throw new InvalidOperationException("Protocol TLS certificate and key are required when TLS is enabled.");
        }

        if (string.IsNullOrWhiteSpace(request.GatewayDohEndpoints))
        {
            throw new InvalidOperationException("Gateway DoH endpoints are required.");
        }
    }

    private static void EnsureSudoPassword(string sudoPassword)
    {
        if (string.IsNullOrWhiteSpace(sudoPassword))
        {
            throw new InvalidOperationException("Sudo password is required for gateway operations.");
        }
    }

    private static string WrapCommand(string command, string sudoPassword)
    {
        if (string.IsNullOrWhiteSpace(sudoPassword))
        {
            return $"bash -lc {ShellQuote(command)}";
        }

        var rootShell = $"bash -lc {ShellQuote(command)}";
        var inner = $"printf '%s\\n' {ShellQuote(sudoPassword)} | sudo -S -p '' {rootShell} 2>&1";
        return $"bash -lc {ShellQuote(inner)}";
    }

    private static string ShellQuote(string value)
    {
        return "'" + (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string RandomAlphaNum(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = chars[bytes[i] % chars.Length];
        }

        return new string(buffer);
    }

    private static bool IsValidIpv4Cidr(string? cidr)
    {
        var value = (cidr ?? string.Empty).Trim();
        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 32)
        {
            return false;
        }

        return true;
    }

    private static bool TryParseProgressLine(string line, out int percent, out string message)
    {
        percent = 0;
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        const string marker = "OMNIRELAY_PROGRESS:";
        if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = line[marker.Length..];
        var firstSep = payload.IndexOf(':');
        if (firstSep < 0)
        {
            return false;
        }

        var pctText = payload[..firstSep].Trim();
        if (!int.TryParse(pctText, out percent))
        {
            return false;
        }

        percent = Math.Clamp(percent, 0, 100);
        message = payload[(firstSep + 1)..].Trim();
        return true;
    }

    private static string? ExtractLastJsonLine(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var line = SplitLines(output)
            .Select(x => x.Trim())
            .LastOrDefault(x => x.StartsWith("{", StringComparison.Ordinal) && x.EndsWith("}", StringComparison.Ordinal));

        return line;
    }

    private static IEnumerable<string> SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string FirstMeaningfulLine(string output, string error)
    {
        var outputLines = SplitLines(output)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Where(x => !x.StartsWith("OMNIRELAY_PROGRESS:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var errorLine = outputLines
            .LastOrDefault(x => x.Contains("ERROR:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(errorLine))
        {
            return errorLine;
        }

        var firstOutput = outputLines.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(firstOutput))
        {
            return firstOutput;
        }

        var firstError = SplitLines(error)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return string.IsNullOrWhiteSpace(firstError) ? "Remote command failed." : firstError.Trim();
    }

    private static string SanitizeTerminalLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var noAnsi = Regex.Replace(line, @"\x1B\[[0-9;?]*[ -/]*[@-~]", string.Empty);
        return noAnsi.Trim();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CommandExecutionResult(bool Success, string Output, string ErrorMessage);
    private sealed record CliExecutionResult(int ExitCode, string Output);

    private class GatewayStatusDto
    {
        public string ActiveProtocol { get; set; } = GatewayProtocols.VlessTlsSingbox;
        public string SshState { get; set; } = "unknown";
        public string SingBoxState { get; set; } = "unknown";
        public string OpenVpnState { get; set; } = "unknown";
        public string IpsecState { get; set; } = "unknown";
        public string Xl2tpdState { get; set; } = "unknown";
        public string OmniPanelState { get; set; } = "unknown";
        public string NginxState { get; set; } = "unknown";
        public string Fail2banState { get; set; } = "unknown";
        public int BackendPort { get; set; }
        public int PublicPort { get; set; }
        public int PanelPort { get; set; }
        public int OmniPanelInternalPort { get; set; }
        public bool BackendListener { get; set; }
        public bool PublicListener { get; set; }
        public bool PanelListener { get; set; }
        public bool OmniPanelInternalListener { get; set; }
        public string InboundId { get; set; } = string.Empty;
        public bool DnsConfigPresent { get; set; }
        public bool DnsRuleActive { get; set; }
        public bool DohReachableViaTunnel { get; set; }
        public bool DnsPathHealthy { get; set; }
        public string DohEndpoints { get; set; } = string.Empty;
        public bool TunnelHealthy { get; set; }
        public string TunnelReason { get; set; } = string.Empty;
        public string TunnelBackendProtocol { get; set; } = string.Empty;
        public bool TunnelEgressReachable { get; set; }

        public GatewayServiceStatus ToModel() => new()
        {
            ActiveProtocol = ActiveProtocol,
            SshState = SshState,
            SingBoxState = SingBoxState,
            OpenVpnState = OpenVpnState,
            IpsecState = IpsecState,
            Xl2tpdState = Xl2tpdState,
            OmniPanelState = OmniPanelState,
            NginxState = NginxState,
            Fail2BanState = Fail2banState,
            BackendPort = BackendPort,
            PublicPort = PublicPort,
            PanelPort = PanelPort,
            OmniPanelInternalPort = OmniPanelInternalPort,
            BackendListener = BackendListener,
            PublicListener = PublicListener,
            PanelListener = PanelListener,
            OmniPanelInternalListener = OmniPanelInternalListener,
            InboundId = InboundId,
            DnsConfigPresent = DnsConfigPresent,
            DnsRuleActive = DnsRuleActive,
            DohReachableViaTunnel = DohReachableViaTunnel,
            DnsPathHealthy = DnsPathHealthy,
            DohEndpoints = DohEndpoints,
            TunnelHealthy = TunnelHealthy,
            TunnelReason = TunnelReason,
            TunnelBackendProtocol = TunnelBackendProtocol,
            TunnelEgressReachable = TunnelEgressReachable
        };
    }

    private sealed class GatewayHealthDto : GatewayStatusDto
    {
        public bool Healthy { get; set; }
        public string DnsLastError { get; set; } = string.Empty;

        public GatewayHealthReport ToModel(string rawJson)
        {
            var baseModel = ToModel();
            return new GatewayHealthReport
            {
                Healthy = Healthy,
                DnsLastError = DnsLastError,
                RawJson = rawJson,
                CheckedAtUtc = DateTimeOffset.UtcNow,
                ActiveProtocol = baseModel.ActiveProtocol,
                SshState = baseModel.SshState,
                SingBoxState = baseModel.SingBoxState,
                OpenVpnState = baseModel.OpenVpnState,
                IpsecState = baseModel.IpsecState,
                Xl2tpdState = baseModel.Xl2tpdState,
                OmniPanelState = baseModel.OmniPanelState,
                NginxState = baseModel.NginxState,
                Fail2BanState = baseModel.Fail2BanState,
                BackendPort = baseModel.BackendPort,
                PublicPort = baseModel.PublicPort,
                PanelPort = baseModel.PanelPort,
                OmniPanelInternalPort = baseModel.OmniPanelInternalPort,
                BackendListener = baseModel.BackendListener,
                PublicListener = baseModel.PublicListener,
                PanelListener = baseModel.PanelListener,
                OmniPanelInternalListener = baseModel.OmniPanelInternalListener,
                InboundId = baseModel.InboundId,
                DnsConfigPresent = baseModel.DnsConfigPresent,
                DnsRuleActive = baseModel.DnsRuleActive,
                DohReachableViaTunnel = baseModel.DohReachableViaTunnel,
                DnsPathHealthy = baseModel.DnsPathHealthy,
                DohEndpoints = baseModel.DohEndpoints,
                TunnelHealthy = baseModel.TunnelHealthy,
                TunnelReason = baseModel.TunnelReason,
                TunnelBackendProtocol = baseModel.TunnelBackendProtocol,
                TunnelEgressReachable = baseModel.TunnelEgressReachable
            };
        }
    }

    private static bool IsTunnelBootstrapMode(GatewayDeploymentRequest request)
    {
        return string.Equals(NormalizeBootstrapMode(request.BootstrapMode), GatewayBootstrapModes.Tunnel, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBootstrapMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            GatewayBootstrapModes.Tunnel => GatewayBootstrapModes.Tunnel,
            GatewayBootstrapModes.Direct => GatewayBootstrapModes.Direct,
            _ => throw new InvalidOperationException("Bootstrap mode must be tunnel or direct.")
        };
    }
}

