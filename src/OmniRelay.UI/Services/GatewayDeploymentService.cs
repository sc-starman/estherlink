using OmniRelay.Core.Configuration;
using OmniRelay.UI.Models;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmniRelay.UI.Services;

public sealed class GatewayDeploymentService : IGatewayDeploymentService, IGatewayHealthService
{
    private const string GatewayCtlPath = "/usr/local/sbin/omnirelay-gatewayctl";
    private const string RemoteInstallScriptPath = "/tmp/omnirelay-gatewayctl.sh";
    private const string RemoteOmniPanelCommonScriptPath = "/tmp/omnirelay-omnipanel-common.sh";
    private const string RemoteUploadedPanelCertPath = "/tmp/omnirelay-omnipanel-upload.crt";
    private const string RemoteUploadedPanelKeyPath = "/tmp/omnirelay-omnipanel-upload.key";

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

            await EnsureCleanProtocolSwitchAsync(request, sudoPassword, progress, cancellationToken);
            await EnsureRuntimeSocksBackendReadyForInstallAsync(request, sudoPassword, progress, cancellationToken);

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 5,
                Message = "Uploading gateway installer script"
            });

            await UploadInstallerScriptAsync(request, progress, cancellationToken);
            await UploadOmniPanelCommonScriptAsync(request, progress, cancellationToken);

            var uploadedPanelCertRemotePath = string.Empty;
            var uploadedPanelKeyRemotePath = string.Empty;
            if (request.GatewayPanelSslEnabled &&
                string.Equals(request.GatewayPanelSslMode, "uploaded", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayInstall,
                    Percent = 9,
                    Message = "Uploading OmniPanel TLS certificate and private key"
                });

                await UploadFileAsync(request, request.GatewayPanelCertLocalPath, RemoteUploadedPanelCertPath, progress, cancellationToken);
                await UploadFileAsync(request, request.GatewayPanelKeyLocalPath, RemoteUploadedPanelKeyPath, progress, cancellationToken);
                uploadedPanelCertRemotePath = RemoteUploadedPanelCertPath;
                uploadedPanelKeyRemotePath = RemoteUploadedPanelKeyPath;
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
                uploadedPanelKeyRemotePath);
            var command =
                "set -euo pipefail; " +
                $"chmod +x {ShellQuote(RemoteInstallScriptPath)}; " +
                $"sed -i 's/\\r$//' {ShellQuote(RemoteInstallScriptPath)} || true; " +
                $"bash -n {ShellQuote(RemoteInstallScriptPath)} >/tmp/omnirelay-gatewayctl.syntax.log 2>&1 || {{ cat /tmp/omnirelay-gatewayctl.syntax.log; exit 43; }}; " +
                $"bash {ShellQuote(RemoteInstallScriptPath)} {installArgs}";

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

            var args = BuildCommonArgs(request) + " --json";
            var command =
                "set -euo pipefail; " +
                $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo 'Gateway control script is not installed on VPS. Run Install Gateway first.'; exit 31; }}; " +
                $"{ShellQuote(GatewayCtlPath)} dns-status {args}";

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
        EnsureSudoPassword(sudoPassword);

        var args = BuildCommonArgs(request) + " --json";
        var command =
            "set -euo pipefail; " +
            $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo '{{\"activeProtocol\":\"vless_reality_3xui\",\"sshState\":\"missing\",\"xuiState\":\"missing\",\"singBoxState\":\"missing\",\"openVpnState\":\"missing\",\"ipsecState\":\"missing\",\"xl2tpdState\":\"missing\",\"omniPanelState\":\"missing\",\"nginxState\":\"missing\",\"fail2banState\":\"disabled\",\"backendPort\":0,\"publicPort\":0,\"panelPort\":0,\"omniPanelInternalPort\":0,\"xuiPanelPort\":0,\"backendListener\":false,\"publicListener\":false,\"panelListener\":false,\"omniPanelInternalListener\":false,\"inboundId\":\"\",\"dnsConfigPresent\":false,\"dnsRuleActive\":false,\"dohReachableViaTunnel\":false,\"udp53PathReady\":false,\"dnsPathHealthy\":false}}'; exit 0; }}; " +
            $"{ShellQuote(GatewayCtlPath)} status {args}";

        var result = await ExecuteCommandAsync(request.Config, command, sudoPassword, null, cancellationToken);
        var json = ExtractLastJsonLine(result.Output);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GatewayServiceStatus
            {
                SshState = result.Success ? "unknown" : "error",
                XuiState = result.Success ? "unknown" : "error",
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
        EnsureSudoPassword(sudoPassword);

        var args = BuildCommonArgs(request) + " --json";
        var command =
            "set -euo pipefail; " +
            $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo '{{\"healthy\":false,\"activeProtocol\":\"vless_reality_3xui\",\"sshState\":\"missing\",\"xuiState\":\"missing\",\"singBoxState\":\"missing\",\"openVpnState\":\"missing\",\"ipsecState\":\"missing\",\"xl2tpdState\":\"missing\",\"omniPanelState\":\"missing\",\"nginxState\":\"missing\",\"fail2banState\":\"disabled\",\"backendPort\":0,\"publicPort\":0,\"panelPort\":0,\"omniPanelInternalPort\":0,\"xuiPanelPort\":0,\"backendListener\":false,\"publicListener\":false,\"panelListener\":false,\"omniPanelInternalListener\":false,\"inboundId\":\"\",\"dnsConfigPresent\":false,\"dnsRuleActive\":false,\"dohReachableViaTunnel\":false,\"udp53PathReady\":false,\"dnsPathHealthy\":false}}'; exit 0; }}; " +
            $"{ShellQuote(GatewayCtlPath)} health {args}";

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

        var args = BuildCommonArgs(request);
        var uninstallCommand =
            "set -euo pipefail; " +
            $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo 'Gateway control script not found during pre-install switch cleanup.'; exit 31; }}; " +
            $"{ShellQuote(GatewayCtlPath)} uninstall {args}";

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
        var selectedProtocol = GatewayProtocols.Normalize(request.SelectedGatewayProtocol);
        if (!string.Equals(selectedProtocol, GatewayProtocols.OpenVpnTcpRelay, StringComparison.OrdinalIgnoreCase))
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
            "def probe_http_connect() -> bool:\n" +
            "    req = b\"CONNECT 1.1.1.1:443 HTTP/1.1\\r\\nHost: 1.1.1.1:443\\r\\nProxy-Connection: keep-alive\\r\\n\\r\\n\"\n" +
            "    try:\n" +
            "        with socket.create_connection((\"127.0.0.1\", port), timeout=4) as s:\n" +
            "            s.settimeout(4)\n" +
            "            s.sendall(req)\n" +
            "            data = s.recv(64)\n" +
            "    except Exception:\n" +
            "        return False\n" +
            "    return data.startswith(b\"HTTP/1.0 \") or data.startswith(b\"HTTP/1.1 \")\n" +
            "\n" +
            "if probe_socks():\n" +
            "    print(f\"Runtime backend probe passed on 127.0.0.1:{port} (mode=socks5).\")\n" +
            "    raise SystemExit(0)\n" +
            "if probe_http_connect():\n" +
            "    print(f\"Runtime backend probe passed on 127.0.0.1:{port} (mode=http-connect).\")\n" +
            "    raise SystemExit(0)\n" +
            "try:\n" +
            "    with socket.create_connection((\"127.0.0.1\", port), timeout=4) as s:\n" +
            "        pass\n" +
            "except Exception as ex:\n" +
            "    print(f\"Runtime backend probe failed on 127.0.0.1:{port}: {ex}\")\n" +
            "    raise SystemExit(44)\n" +
            "print(f\"Runtime backend probe failed on 127.0.0.1:{port}: unsupported proxy protocol (expected socks5 or http-connect)\")\n" +
            "raise SystemExit(44)\n" +
            "PY";

        var probeResult = await ExecuteCommandAsync(
            request.Config,
            command,
            sudoPassword,
            line =>
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
            },
            cancellationToken);

        if (!probeResult.Success)
        {
            throw new InvalidOperationException(
                $"Runtime tunnel backend preflight failed on 127.0.0.1:{backendPort}. {probeResult.ErrorMessage}");
        }

        progress?.Report(new DeploymentProgressSnapshot
        {
            Phase = DeploymentPhases.GatewayInstall,
            Percent = 4,
            Message = $"Runtime tunnel backend check passed (127.0.0.1:{backendPort})"
        });
    }

    private async Task<(bool GatewayCtlPresent, bool ProtocolDetermined, string? CurrentProtocol)> DetectCurrentGatewayProtocolAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var command =
            "set -euo pipefail; " +
            $"if [ ! -x {ShellQuote(GatewayCtlPath)} ]; then echo '__OMNIRELAY_NO_GATEWAYCTL__'; exit 0; fi; " +
            $"if proto=$({ShellQuote(GatewayCtlPath)} get-protocol 2>/dev/null | tr -d '\\r\\n' | xargs); then " +
            "  if [ -n \"$proto\" ]; then echo \"__OMNIRELAY_PROTO__:${proto}\"; exit 0; fi; " +
            "fi; " +
            $"status_json=$({ShellQuote(GatewayCtlPath)} status --json 2>/dev/null || true); " +
            "if [ -n \"$status_json\" ]; then echo \"__OMNIRELAY_STATUS__:${status_json}\"; exit 0; fi; " +
            "echo '__OMNIRELAY_UNKNOWN__';";

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
        await UploadFileAsync(request, localScript, RemoteInstallScriptPath, progress, cancellationToken);
    }

    private async Task UploadOmniPanelCommonScriptAsync(
        GatewayDeploymentRequest request,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var localScript = ResolveOmniPanelCommonScriptPath();
        await UploadFileAsync(request, localScript, RemoteOmniPanelCommonScriptPath, progress, cancellationToken);
    }

    private async Task UploadFileAsync(
        GatewayDeploymentRequest request,
        string localPath,
        string remotePath,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo? BuildStartInfo(out string bindIp, out string? error)
        {
            if (!SshCliStartInfoFactory.TryCreateBoundScpUploadStartInfo(
                    request.Config,
                    localPath,
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

    private static string ResolveInstallerScriptPath(string selectedGatewayProtocol)
    {
        var baseDir = AppContext.BaseDirectory;
        var normalizedProtocol = GatewayProtocols.Normalize(selectedGatewayProtocol);
        var scriptFileName = normalizedProtocol switch
        {
            var protocol when string.Equals(protocol, GatewayProtocols.VlessPlain3xui, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_3xui_vless_plain.sh",
            var protocol when string.Equals(protocol, GatewayProtocols.Shadowsocks3xui, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_3xui_shadowsocks.sh",
            var protocol when string.Equals(protocol, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_singbox_shadowtls.sh",
            var protocol when string.Equals(protocol, GatewayProtocols.OpenVpnTcpRelay, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_openvpn.sh",
            var protocol when string.Equals(protocol, GatewayProtocols.IpsecL2tpHwdsl2, StringComparison.OrdinalIgnoreCase) => "setup_omnirelay_vps_ipsec_l2tp.sh",
            _ => "setup_omnirelay_vps_3xui_vless_reality.sh"
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

            var args = BuildCommonArgs(request);
            var command =
                "set -euo pipefail; " +
                $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo 'Gateway control script is not installed on VPS. Run Install Gateway first.'; exit 31; }}; " +
                $"{ShellQuote(GatewayCtlPath)} {operation} {args}";

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
        var wrapped = WrapCommand(command, sudoPassword);
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
        string panelKeyRemotePath)
    {
        return
            $"install {BuildCommonArgs(request)} {BuildProtocolArgs(request)} " +
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
    }

    private static string BuildCommonArgs(GatewayDeploymentRequest request)
    {
        return string.Join(" ", new[]
        {
            "--public-port", request.GatewayPublicPort.ToString(),
            "--panel-port", request.GatewayPanelPort.ToString(),
            "--backend-port", request.Config.TunnelRemotePort.ToString(),
            "--ssh-port", request.Config.TunnelSshPort.ToString(),
            "--bootstrap-socks-port", request.Config.BootstrapSocksRemotePort.ToString(),
            "--vps-ip", ShellQuote(request.Config.TunnelHost.Trim()),
            "--tunnel-user", ShellQuote(request.Config.TunnelUser.Trim()),
            "--dns-mode", ShellQuote(request.GatewayDnsMode.Trim().ToLowerInvariant()),
            "--doh-endpoints", ShellQuote(request.GatewayDohEndpoints.Trim()),
            "--dns-udp-only", request.GatewayDnsUdpOnly ? "true" : "false"
        });
    }

    private static string BuildProtocolArgs(GatewayDeploymentRequest request)
    {
        var selectedProtocol = GatewayProtocols.Normalize(request.SelectedGatewayProtocol);
        if (string.Equals(selectedProtocol, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return $"--camouflage-server {ShellQuote(request.ShadowTlsCamouflageServer.Trim())}";
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.Shadowsocks3xui, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedProtocol, GatewayProtocols.VlessPlain3xui, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.IpsecL2tpHwdsl2, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (string.Equals(selectedProtocol, GatewayProtocols.OpenVpnTcpRelay, StringComparison.OrdinalIgnoreCase))
        {
            var openVpnClientDns = request.OpenVpnClientDns?.Trim() ?? string.Empty;
            return
                $"--openvpn-network {ShellQuote(request.OpenVpnNetwork.Trim())} " +
                $"--openvpn-client-dns {ShellQuote(openVpnClientDns)}";
        }

        return string.Join(" ", new[]
        {
            "--gateway-sni", ShellQuote(request.GatewaySni.Trim()),
            "--gateway-target", ShellQuote(request.GatewayTarget.Trim())
        });
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

        if (string.IsNullOrWhiteSpace(request.Config.TunnelHost))
        {
            throw new InvalidOperationException("Tunnel host is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Config.TunnelUser))
        {
            throw new InvalidOperationException("Tunnel user is required.");
        }

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
        if (string.Equals(selectedProtocol, GatewayProtocols.VlessReality3xui, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.GatewaySni))
            {
                throw new InvalidOperationException("Gateway SNI is required.");
            }

            if (string.IsNullOrWhiteSpace(request.GatewayTarget))
            {
                throw new InvalidOperationException("Gateway target is required.");
            }
        }
        else if (string.Equals(selectedProtocol, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.ShadowTlsCamouflageServer))
            {
                throw new InvalidOperationException("ShadowTLS camouflage server is required.");
            }
        }
        else if (string.Equals(selectedProtocol, GatewayProtocols.OpenVpnTcpRelay, StringComparison.OrdinalIgnoreCase)
                 && string.IsNullOrWhiteSpace(request.OpenVpnNetwork))
        {
            throw new InvalidOperationException("OpenVPN tunnel network is required.");
        }

        if (string.IsNullOrWhiteSpace(request.GatewayDohEndpoints))
        {
            throw new InvalidOperationException("Gateway DoH endpoints are required.");
        }

        var dnsMode = request.GatewayDnsMode?.Trim().ToLowerInvariant();
        if (dnsMode is not ("hybrid" or "doh" or "udp"))
        {
            throw new InvalidOperationException("Gateway DNS mode must be hybrid, doh, or udp.");
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
        public string ActiveProtocol { get; set; } = GatewayProtocols.VlessReality3xui;
        public string SshState { get; set; } = "unknown";
        public string XuiState { get; set; } = "unknown";
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
        public int XuiPanelPort { get; set; }
        public bool BackendListener { get; set; }
        public bool PublicListener { get; set; }
        public bool PanelListener { get; set; }
        public bool OmniPanelInternalListener { get; set; }
        public string InboundId { get; set; } = string.Empty;
        public bool DnsConfigPresent { get; set; }
        public bool DnsRuleActive { get; set; }
        public bool DohReachableViaTunnel { get; set; }
        public bool Udp53PathReady { get; set; }
        public bool DnsPathHealthy { get; set; }
        public string DnsMode { get; set; } = "unknown";
        public bool DnsUdpOnly { get; set; }
        public string DohEndpoints { get; set; } = string.Empty;

        public GatewayServiceStatus ToModel() => new()
        {
            ActiveProtocol = ActiveProtocol,
            SshState = SshState,
            XuiState = XuiState,
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
            XuiPanelPort = XuiPanelPort,
            BackendListener = BackendListener,
            PublicListener = PublicListener,
            PanelListener = PanelListener,
            OmniPanelInternalListener = OmniPanelInternalListener,
            InboundId = InboundId,
            DnsConfigPresent = DnsConfigPresent,
            DnsRuleActive = DnsRuleActive,
            DohReachableViaTunnel = DohReachableViaTunnel,
            Udp53PathReady = Udp53PathReady,
            DnsPathHealthy = DnsPathHealthy,
            DnsMode = DnsMode,
            DnsUdpOnly = DnsUdpOnly,
            DohEndpoints = DohEndpoints
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
                XuiState = baseModel.XuiState,
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
                XuiPanelPort = baseModel.XuiPanelPort,
                BackendListener = baseModel.BackendListener,
                PublicListener = baseModel.PublicListener,
                PanelListener = baseModel.PanelListener,
                OmniPanelInternalListener = baseModel.OmniPanelInternalListener,
                InboundId = baseModel.InboundId,
                DnsConfigPresent = baseModel.DnsConfigPresent,
                DnsRuleActive = baseModel.DnsRuleActive,
                DohReachableViaTunnel = baseModel.DohReachableViaTunnel,
                Udp53PathReady = baseModel.Udp53PathReady,
                DnsPathHealthy = baseModel.DnsPathHealthy,
                DnsMode = baseModel.DnsMode,
                DnsUdpOnly = baseModel.DnsUdpOnly,
                DohEndpoints = baseModel.DohEndpoints
            };
        }
    }
}
