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
using System.Net.Http;
using System.Net.Sockets;

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
    private const string ConnectorCorePath = "/usr/local/bin/connector-core";
    private static readonly HttpClient ReleaseHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    public GatewayDeploymentService() { }

    public async Task<GatewayOperationResult> CheckGatewayBootstrapAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await CheckGatewayBootstrapInternalAsync(
            request,
            sudoPassword,
            progress,
            cancellationToken,
            bootstrapTunnelAlreadyEstablished: false);
    }

    private async Task<GatewayOperationResult> CheckGatewayBootstrapInternalAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken,
        bool bootstrapTunnelAlreadyEstablished,
        int? bootstrapSocksRemotePortOverride = null)
    {
        Process? bootstrapTunnelProcess = null;
        int? temporaryBootstrapPort = null;
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

            if (!bootstrapTunnelAlreadyEstablished)
            {
                var bootstrapSession = await StartTemporaryBootstrapTunnelAsync(request, progress, cancellationToken);
                if (!bootstrapSession.Success || bootstrapSession.Process is null)
                {
                    return new GatewayOperationResult(false, bootstrapSession.Message);
                }

                bootstrapTunnelProcess = bootstrapSession.Process;
                temporaryBootstrapPort = bootstrapSession.RemotePort;
            }

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayBootstrap,
                Percent = 10,
                Message = "Checking SOCKS bootstrap endpoint"
            });

            var socksPort = (bootstrapSocksRemotePortOverride ?? temporaryBootstrapPort ?? request.Config.TunnelRemotePort).ToString();
            var command = """
                set -euo pipefail;
                ss -lnt '( sport = :__PORT__ )' 2>/dev/null | awk 'NR>1 {print $0}' | grep -q . || { echo 'SOCKS listener is not present on 127.0.0.1:__PORT__'; exit 41; };
                sync_clock_done=0;
                sync_clock_attempted=0;
                first_success_target='';
                first_date_target='';
                first_date_header='';
                sync_clock_via_socks() {
                  local hdr epoch was_ntp target;
                  target="${1:-}";
                  [ -n "$target" ] || return 1;
                  was_ntp='';
                  hdr="$(curl --silent --show-error --insecure --max-time 25 --connect-timeout 10 --retry 0 --socks5-hostname 127.0.0.1:__PORT__ -I "$target" 2>/dev/null | tr -d '\r' | awk 'tolower($1)=="date:"{$1="";sub(/^ /,"");print;exit}')";
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
                  echo "Adjusted VPS clock from HTTPS Date header via SOCKS tunnel (target: ${target}).";
                  return 0;
                };
                probe_targets='https://8.8.8.8/ https://dns.google/ https://9.9.9.9/';
                probe_success_count=0;
                probe_fail_count=0;
                probe_fail_details='';
                ok=0;
                for i in 1 2 3; do
                  attempt_ok=0;
                  for target in $probe_targets; do
                    if curl --fail --silent --show-error --max-time 45 --connect-timeout 20 --retry 0 --socks5-hostname 127.0.0.1:__PORT__ "$target" >/dev/null 2>/tmp/omnirelay-bootstrap-curl.err; then
                      echo "SOCKS egress probe target succeeded: ${target}";
                      probe_success_count=$((probe_success_count+1));
                      [ -n "$first_success_target" ] || first_success_target="$target";
                      if [ -z "$first_date_target" ]; then
                        hdr="$(curl --silent --show-error --insecure --max-time 25 --connect-timeout 10 --retry 0 --socks5-hostname 127.0.0.1:__PORT__ -I "$target" 2>/tmp/omnirelay-bootstrap-date.err | tr -d '\r' | awk 'tolower($1)=="date:"{$1="";sub(/^ /,"");print;exit}' || true)";
                        if [ -n "$hdr" ]; then
                          first_date_target="$target";
                          first_date_header="$hdr";
                        fi;
                      fi;
                      attempt_ok=1;
                      ok=1;
                      break;
                    fi;
                    err="$(tr -d '\r' </tmp/omnirelay-bootstrap-curl.err 2>/dev/null || true)";
                    [ -n "$err" ] && printf '[target=%s] %s\n' "$target" "$err";
                    probe_fail_count=$((probe_fail_count+1));
                    probe_fail_details="${probe_fail_details}[target=${target}] ${err}; ";
                    if [ "$sync_clock_done" != "1" ] && [ "$sync_clock_attempted" != "1" ] && printf '%s' "$err" | grep -qi 'certificate is not yet valid'; then
                      sync_clock_attempted=1;
                      echo "Detected TLS clock skew from target ${target}; attempting clock sync over SOCKS tunnel.";
                      if sync_clock_via_socks "$target"; then
                        sync_clock_done=1;
                      else
                        echo "WARNING: Clock sync attempt failed for target ${target}; continuing bootstrap probe.";
                      fi;
                    fi;
                  done;
                  if [ "$attempt_ok" = "1" ]; then
                    break;
                  fi;
                  echo "SOCKS egress probe attempt ${i}/3 failed for all targets, retrying...";
                  sleep 3;
                done;
                rm -f /tmp/omnirelay-bootstrap-curl.err;
                rm -f /tmp/omnirelay-bootstrap-date.err;
                if [ "$ok" != "1" ]; then
                  echo "SOCKS egress probe failed after retries. Targets=${probe_targets}. Failures=${probe_fail_count}. Details=${probe_fail_details}";
                  exit 42;
                fi;
                if [ "$sync_clock_done" != "1" ]; then
                  if [ -n "$first_date_target" ] && [ -n "$first_date_header" ]; then
                    if sync_clock_via_socks "$first_date_target"; then
                      sync_clock_done=1;
                    else
                      echo "WARNING: Best-effort clock sync failed using successful target ${first_date_target}; continuing.";
                    fi;
                  else
                    echo "WARNING: No HTTPS Date header was available from successful probe targets; skipping clock sync.";
                  fi;
                fi;
                echo "SOCKS egress probe summary: success_target=${first_success_target:-none}, successes=${probe_success_count}, failures=${probe_fail_count}";
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
        finally
        {
            if (!bootstrapTunnelAlreadyEstablished)
            {
                await StopTemporaryBootstrapTunnelAsync(bootstrapTunnelProcess, progress);
            }
        }
    }

    public async Task<GatewayOperationResult> TestGatewayTunnelAsync(
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
                Phase = DeploymentPhases.GatewayHealth,
                Percent = 10,
                Message = "Checking FRPS service readiness"
            });

            var frpPort = request.Config.FrpServerPort.ToString();
            var frpsCheckCommand = """
                set -euo pipefail;
                systemctl is-active omnirelay-frps.service >/dev/null 2>&1 || { echo 'FRPS service is not active'; exit 71; };
                ss -lnt '( sport = :__FRP_PORT__ )' 2>/dev/null | awk 'NR>1{found=1} END{exit found?0:1}' || { echo 'FRPS control port is not listening on __FRP_PORT__'; exit 72; };
                echo 'FRPS service is active and listening.';
                """.Replace("__FRP_PORT__", frpPort);

            var frpsReady = await ExecuteCommandAsync(
                request.Config,
                frpsCheckCommand,
                sudoPassword,
                line =>
                {
                    var clean = SanitizeTerminalLine(line);
                    if (!string.IsNullOrWhiteSpace(clean))
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

            if (!frpsReady.Success)
            {
                return new GatewayOperationResult(false, $"FRP server is not ready: {frpsReady.ErrorMessage}");
            }

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayHealth,
                Percent = 60,
                Message = "Checking runtime tunnel health"
            });

            var health = await GetHealthAsync(request, sudoPassword, progress, cancellationToken);
            if (!health.TunnelHealthy)
            {
                var reason = string.IsNullOrWhiteSpace(health.TunnelReason) ? "unknown" : health.TunnelReason;
                return new GatewayOperationResult(false, $"Tunnel is unhealthy: {reason}");
            }

            if (!health.BackendListener)
            {
                return new GatewayOperationResult(false, "Tunnel backend listener is down.");
            }

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayHealth,
                Percent = 100,
                Message = "FRP tunnel test passed"
            });
            return new GatewayOperationResult(true, "FRP tunnel test passed.");
        }
        catch (Exception ex)
        {
            return new GatewayOperationResult(false, $"FRP tunnel test failed: {ex.Message}");
        }
    }

    public async Task<GatewayOperationResult> InstallGatewayAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsConnectorCoreGatewayCutoverEnabled())
        {
            return await InstallGatewayWithConnectorCoreAsync(request, sudoPassword, progress, cancellationToken);
        }

        Process? bootstrapTunnelProcess = null;
        int? bootstrapSocksRemotePortOverride = null;
        try
        {
            ValidateRequest(request);
            EnsureSudoPassword(sudoPassword);

            if (IsTunnelBootstrapMode(request))
            {
                var bootstrapSession = await StartTemporaryBootstrapTunnelAsync(request, progress, cancellationToken);
                if (!bootstrapSession.Success || bootstrapSession.Process is null)
                {
                    return new GatewayOperationResult(false, bootstrapSession.Message);
                }

                bootstrapTunnelProcess = bootstrapSession.Process;
                bootstrapSocksRemotePortOverride = bootstrapSession.RemotePort;

                if (bootstrapSession.RemotePort <= 0)
                {
                    return new GatewayOperationResult(false, "Temporary bootstrap tunnel started without a valid VPS remote port.");
                }

                var bootstrap = await CheckGatewayBootstrapInternalAsync(
                    request,
                    sudoPassword,
                    progress,
                    cancellationToken,
                    bootstrapTunnelAlreadyEstablished: true,
                    bootstrapSocksRemotePortOverride: bootstrapSocksRemotePortOverride);
                if (!bootstrap.Success)
                {
                    return new GatewayOperationResult(false, $"Gateway bootstrap preflight failed: {bootstrap.Message}");
                }
            }
            else
            {
                var bootstrap = await CheckGatewayBootstrapInternalAsync(
                    request,
                    sudoPassword,
                    progress,
                    cancellationToken,
                    bootstrapTunnelAlreadyEstablished: true);
                if (!bootstrap.Success)
                {
                    return new GatewayOperationResult(false, $"Gateway bootstrap preflight failed: {bootstrap.Message}");
                }
            }

            await EnsureTunnelModuleInstalledAsync(request, sudoPassword, DeploymentPhases.GatewayInstall, progress, cancellationToken);
            await EnsureCleanProtocolSwitchAsync(request, sudoPassword, progress, cancellationToken);
            if (!string.Equals(GatewayTypes.Normalize(request.Config.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
            {
                await EnsureRuntimeSocksBackendReadyForInstallAsync(request, sudoPassword, progress, cancellationToken);
            }
            else
            {
                progress?.Report(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayInstall,
                    Percent = 4,
                    Message = "Skipping pre-install backend probe for remote FRP runtime"
                });
            }

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
                uploadedOpenVpnSharedTlsCryptKeyRemotePath,
                bootstrapSocksRemotePortOverride);
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

            if (string.Equals(GatewayTypes.Normalize(request.Config.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
            {
                await WaitForFrpBackendListenerAfterInstallAsync(request, sudoPassword, progress, cancellationToken);
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
        finally
        {
            await StopTemporaryBootstrapTunnelAsync(bootstrapTunnelProcess, progress);
        }
    }

    private async Task<GatewayOperationResult> InstallGatewayWithConnectorCoreAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        Process? bootstrapTunnelProcess = null;
        string? localSpecPath = null;
        var migrationApplied = false;
        var migrationFinalized = false;
        var rollbackAttempted = false;
        var remoteCleanupPaths = new List<string>();
        try
        {
            ValidateRequest(request);
            EnsureSudoPassword(sudoPassword);

            var releaseChannel = ResolveGatewayAssetReleaseChannel();
            var releaseBaseUrl = ResolveConnectorCoreReleaseBaseUrl();
            var publicKeyPath = ResolveConnectorCoreReleasePublicKeyPath();
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 1,
                Message = $"Resolving signed connector-core and OmniPanel release metadata ({releaseChannel})"
            });
            var release = await ConnectorCoreReleaseMetadataResolver.ResolveAsync(
                ReleaseHttpClient,
                releaseBaseUrl,
                releaseChannel,
                cancellationToken);

            int? bootstrapSocksPort = null;
            if (IsTunnelBootstrapMode(request))
            {
                var bootstrapSession = await StartTemporaryBootstrapTunnelAsync(request, progress, cancellationToken);
                if (!bootstrapSession.Success || bootstrapSession.Process is null || bootstrapSession.RemotePort <= 0)
                {
                    return new GatewayOperationResult(false, bootstrapSession.Message);
                }
                bootstrapTunnelProcess = bootstrapSession.Process;
                bootstrapSocksPort = bootstrapSession.RemotePort;
            }

            var bootstrapCheck = await CheckGatewayBootstrapInternalAsync(
                request,
                sudoPassword,
                progress,
                cancellationToken,
                bootstrapTunnelAlreadyEstablished: true,
                bootstrapSocksRemotePortOverride: bootstrapSocksPort);
            if (!bootstrapCheck.Success)
            {
                return new GatewayOperationResult(false, $"Gateway bootstrap preflight failed: {bootstrapCheck.Message}");
            }

            var relayId = NormalizeRelayId(request.RelayId).ToLowerInvariant();
            var remoteBootstrapPath = $"/tmp/omnirelay-connector-bootstrap-{relayId}.sh";
            var remoteSpecPath = $"/tmp/omnirelay-gateway-spec-{relayId}.json";
            var remotePublicKeyPath = $"/tmp/omnirelay-connector-release-{relayId}.pem";
            remoteCleanupPaths.AddRange([remoteBootstrapPath, remoteSpecPath, remotePublicKeyPath]);
            var panelCertRemotePath = string.Empty;
            var panelKeyRemotePath = string.Empty;
            var openVpnCaRemotePath = string.Empty;
            var openVpnClientCertRemotePath = string.Empty;
            var openVpnClientKeyRemotePath = string.Empty;
            var openVpnTlsCryptRemotePath = string.Empty;

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 5,
                Message = "Uploading signed connector-core bootstrap inputs"
            });
            await UploadFileAsync(request, ResolveConnectorCoreBootstrapScriptPath(), remoteBootstrapPath, progress, cancellationToken);
            await UploadFileAsync(request, publicKeyPath, remotePublicKeyPath, progress, cancellationToken);

            if (request.GatewayPanelSslEnabled &&
                string.Equals(request.GatewayPanelSslMode, "uploaded", StringComparison.OrdinalIgnoreCase))
            {
                panelCertRemotePath = $"/tmp/omnirelay-panel-{relayId}.crt";
                panelKeyRemotePath = $"/tmp/omnirelay-panel-{relayId}.key";
                remoteCleanupPaths.AddRange([panelCertRemotePath, panelKeyRemotePath]);
                await UploadFileAsync(request, request.GatewayPanelCertLocalPath, panelCertRemotePath, progress, cancellationToken);
                await UploadFileAsync(request, request.GatewayPanelKeyLocalPath, panelKeyRemotePath, progress, cancellationToken);
            }

            if (GatewayProtocols.Normalize(request.SelectedGatewayProtocol) == GatewayProtocols.OpenVpnTcpSingbox)
            {
                openVpnCaRemotePath = $"/tmp/omnirelay-openvpn-{relayId}-ca.crt";
                openVpnClientCertRemotePath = $"/tmp/omnirelay-openvpn-{relayId}-client.crt";
                openVpnClientKeyRemotePath = $"/tmp/omnirelay-openvpn-{relayId}-client.key";
                openVpnTlsCryptRemotePath = $"/tmp/omnirelay-openvpn-{relayId}-tls-crypt.key";
                remoteCleanupPaths.AddRange([openVpnCaRemotePath, openVpnClientCertRemotePath, openVpnClientKeyRemotePath, openVpnTlsCryptRemotePath]);
                await UploadFileAsync(request, request.OpenVpnSharedCaCertLocalPath, openVpnCaRemotePath, progress, cancellationToken);
                await UploadFileAsync(request, request.OpenVpnSharedClientCertLocalPath, openVpnClientCertRemotePath, progress, cancellationToken);
                await UploadFileAsync(request, request.OpenVpnSharedClientKeyLocalPath, openVpnClientKeyRemotePath, progress, cancellationToken);
                await UploadFileAsync(request, request.OpenVpnSharedTlsCryptKeyLocalPath, openVpnTlsCryptRemotePath, progress, cancellationToken);
            }

            var panelUser = string.IsNullOrWhiteSpace(request.GatewayPanelUser)
                ? $"omniadmin_{RandomAlphaNum(6)}"
                : request.GatewayPanelUser.Trim();
            var panelPassword = string.IsNullOrWhiteSpace(request.GatewayPanelPassword)
                ? RandomAlphaNum(24)
                : request.GatewayPanelPassword.Trim();
            var specJson = ConnectorCoreGatewaySpecBuilder.BuildJson(request, new ConnectorCoreGatewaySpecOptions
            {
                ConnectorCoreVersion = release.Version,
                ReleaseChannel = release.Channel,
                PanelPublicHost = FirstNonEmpty(request.GatewayPanelDomain, request.Config.TunnelHost) ?? string.Empty,
                PanelCertRemotePath = panelCertRemotePath,
                PanelKeyRemotePath = panelKeyRemotePath,
                PanelArtifactUrl = release.PanelArtifactUrl,
                PanelArtifactSha256 = release.PanelArtifactSha256,
                PanelUsername = panelUser,
                PanelPassword = panelPassword,
                OpenVpnSharedCaCertRemotePath = openVpnCaRemotePath,
                OpenVpnSharedClientCertRemotePath = openVpnClientCertRemotePath,
                OpenVpnSharedClientKeyRemotePath = openVpnClientKeyRemotePath,
                OpenVpnSharedTlsCryptKeyRemotePath = openVpnTlsCryptRemotePath
            });
            localSpecPath = Path.Combine(Path.GetTempPath(), $"omnirelay-gateway-spec-{relayId}-{Guid.NewGuid():N}.json");
            File.WriteAllText(localSpecPath, specJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await UploadFileAsync(request, localSpecPath, remoteSpecPath, progress, cancellationToken);
            var sensitiveRemotePaths = remoteCleanupPaths
                .Where(path => !string.Equals(path, remoteBootstrapPath, StringComparison.Ordinal) &&
                               !string.Equals(path, remotePublicKeyPath, StringComparison.Ordinal))
                .Select(ShellQuote);
            var secureUploadCommand =
                $"chmod 0600 {string.Join(" ", sensitiveRemotePaths)}; " +
                $"chmod 0644 {ShellQuote(remotePublicKeyPath)}; " +
                $"chmod 0700 {ShellQuote(remoteBootstrapPath)}";
            var secureUploadResult = await ExecuteCommandAsync(
                request.Config,
                secureUploadCommand,
                sudoPassword,
                null,
                cancellationToken);
            if (!secureUploadResult.Success)
            {
                return new GatewayOperationResult(false, $"Failed to secure uploaded connector-core bootstrap inputs: {secureUploadResult.ErrorMessage}");
            }

            var bootstrapMode = NormalizeBootstrapMode(request.BootstrapMode);
            var effectiveSocksPort = bootstrapSocksPort ?? request.Config.TunnelRemotePort;
            var bootstrapCommand =
                "set -euo pipefail; " +
                $"chmod 0700 {ShellQuote(remoteBootstrapPath)}; " +
                $"sed -i 's/\\r$//' {ShellQuote(remoteBootstrapPath)}; " +
                $"bash {ShellQuote(remoteBootstrapPath)} " +
                $"--base-url {ShellQuote(releaseBaseUrl)} " +
                $"--spec {ShellQuote(remoteSpecPath)} " +
                $"--channel {ShellQuote(release.Channel)} " +
                "--operation migrate " +
                $"--bootstrap-mode {ShellQuote(bootstrapMode)} " +
                $"--socks-port {effectiveSocksPort} " +
                $"--public-key-file {ShellQuote(remotePublicKeyPath)}";

            using var installTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            installTimeoutCts.CancelAfter(TimeSpan.FromMinutes(30));
            var bootstrapResult = await ExecuteConnectorCoreCommandAsync(
                request,
                sudoPassword,
                bootstrapCommand,
                DeploymentPhases.GatewayInstall,
                progress,
                installTimeoutCts.Token);
            if (!bootstrapResult.Success)
            {
                return new GatewayOperationResult(false, bootstrapResult.ErrorMessage);
            }
            migrationApplied = true;

            var start = await RunConnectorCoreGatewayCommandAsync(
                request,
                sudoPassword,
                "gateway start",
                DeploymentPhases.GatewayInstall,
                progress,
                cancellationToken);
            if (!start.Success)
            {
                return start;
            }
            if (GatewayTypes.Normalize(request.Config.GatewayType) == GatewayTypes.Remote)
            {
                await WaitForFrpBackendListenerAfterInstallAsync(request, sudoPassword, progress, cancellationToken);
            }
            var panel = await RunConnectorCoreGatewayCommandAsync(
                request,
                sudoPassword,
                "panel activate",
                DeploymentPhases.GatewayInstall,
                progress,
                cancellationToken);
            if (!panel.Success)
            {
                return panel;
            }
            var acceptance = await WaitForConnectorCoreAcceptanceAsync(
                request,
                sudoPassword,
                progress,
                cancellationToken);
            if (!acceptance.Success)
            {
                var rollback = await RunConnectorCoreGatewayCommandAsync(
                    request,
                    sudoPassword,
                    "gateway rollback-migration",
                    DeploymentPhases.GatewayInstall,
                    progress,
                    cancellationToken);
                rollbackAttempted = true;
                var rollbackDetail = rollback.Success ? "Legacy gateway state was restored." : $"Legacy rollback failed: {rollback.Message}";
                return new GatewayOperationResult(false, $"{acceptance.Message} {rollbackDetail}");
            }
            var finalize = await RunConnectorCoreGatewayCommandAsync(
                request,
                sudoPassword,
                "gateway finalize-migration",
                DeploymentPhases.GatewayInstall,
                progress,
                cancellationToken);
            if (!finalize.Success)
            {
                return new GatewayOperationResult(false, $"Connector-core acceptance passed, but legacy migration finalization failed: {finalize.Message}");
            }
            migrationFinalized = true;

            var panelHost = FirstNonEmpty(request.GatewayPanelDomain, request.Config.TunnelHost) ?? request.Config.TunnelHost;
            var panelScheme = request.GatewayPanelSslEnabled ? "https" : "http";
            var panelUrl = $"{panelScheme}://{panelHost}:{request.GatewayPanelPort}/";
            return new GatewayOperationResult(
                true,
                $"Gateway install completed through connector-core. Panel URL: {panelUrl} | Username: {panelUser} | Password: {panelPassword}",
                panelUrl,
                panelUser,
                panelPassword);
        }
        catch (Exception ex)
        {
            return new GatewayOperationResult(false, $"Connector-core gateway install failed: {ex.Message}");
        }
        finally
        {
            if (migrationApplied && !migrationFinalized && !rollbackAttempted)
            {
                try
                {
                    using var rollbackCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await RunConnectorCoreGatewayCommandAsync(
                        request,
                        sudoPassword,
                        "gateway rollback-migration",
                        DeploymentPhases.GatewayInstall,
                        progress,
                        rollbackCts.Token);
                }
                catch
                {
                }
            }
            if (!string.IsNullOrWhiteSpace(localSpecPath))
            {
                try
                {
                    File.Delete(localSpecPath);
                }
                catch
                {
                }
            }
            if (remoteCleanupPaths.Count > 0)
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    await ExecuteCommandAsync(
                        request.Config,
                        "rm -f -- " + string.Join(" ", remoteCleanupPaths.Distinct(StringComparer.Ordinal).Select(ShellQuote)),
                        sudoPassword,
                        null,
                        cleanupCts.Token);
                }
                catch
                {
                }
            }
            await StopTemporaryBootstrapTunnelAsync(bootstrapTunnelProcess, progress);
        }
    }

    private async Task<GatewayOperationResult> WaitForConnectorCoreAcceptanceAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        string lastReason = "gateway health probe did not return a result";
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (status, _, healthy) = await GetConnectorCoreStatusWithRawAsync(
                request,
                sudoPassword,
                health: true,
                progress: progress,
                cancellationToken: cancellationToken);
            if (healthy)
            {
                progress?.Report(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayHealth,
                    Percent = 100,
                    Message = "Connector-core migration acceptance probes passed"
                });
                return new GatewayOperationResult(true, "Connector-core migration acceptance probes passed.");
            }

            lastReason = FirstNonEmpty(status.TunnelReason, "gateway is not healthy") ?? "gateway is not healthy";
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayHealth,
                Percent = Math.Clamp(attempt * 10, 10, 90),
                Message = $"Waiting for connector-core migration acceptance ({attempt}/8): {lastReason}"
            });
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        return new GatewayOperationResult(false, $"Connector-core migration acceptance failed: {lastReason}");
    }

    public Task<GatewayOperationResult> StartGatewayAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsConnectorCoreGatewayCutoverEnabled())
        {
            return RunConnectorCoreGatewayCommandAsync(request, sudoPassword, "gateway start", DeploymentPhases.GatewayCommand, progress, cancellationToken);
        }
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "start", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public Task<GatewayOperationResult> StopGatewayAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsConnectorCoreGatewayCutoverEnabled())
        {
            return RunConnectorCoreGatewayCommandAsync(request, sudoPassword, "gateway stop", DeploymentPhases.GatewayCommand, progress, cancellationToken);
        }
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "stop", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public Task<GatewayOperationResult> UninstallGatewayAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsConnectorCoreGatewayCutoverEnabled())
        {
            return RunConnectorCoreGatewayCommandAsync(request, sudoPassword, "gateway uninstall", DeploymentPhases.GatewayCommand, progress, cancellationToken);
        }
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "uninstall", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public Task<GatewayOperationResult> ApplyGatewayDnsAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsConnectorCoreGatewayCutoverEnabled())
        {
            return RunConnectorCoreGatewayCommandAsync(request, sudoPassword, "dns apply", DeploymentPhases.GatewayCommand, progress, cancellationToken);
        }
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "dns-apply", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public async Task<GatewayOperationResult> CheckGatewayDnsAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsConnectorCoreGatewayCutoverEnabled())
        {
            return await CheckConnectorCoreGatewayDnsAsync(request, sudoPassword, progress, cancellationToken);
        }

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
        if (IsConnectorCoreGatewayCutoverEnabled())
        {
            return RunConnectorCoreGatewayCommandAsync(request, sudoPassword, "gateway repair --level safe", DeploymentPhases.GatewayCommand, progress, cancellationToken);
        }
        return RunSimpleGatewayCommandAsync(request, sudoPassword, "dns-repair", DeploymentPhases.GatewayCommand, progress, cancellationToken);
    }

    public async Task<GatewayServiceStatus> GetStatusAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        CancellationToken cancellationToken = default)
    {
        if (IsConnectorCoreGatewayCutoverEnabled())
        {
            return await GetConnectorCoreStatusAsync(request, sudoPassword, health: false, cancellationToken);
        }

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
        if (IsConnectorCoreGatewayCutoverEnabled())
        {
            var (status, rawJson, healthy) = await GetConnectorCoreStatusWithRawAsync(request, sudoPassword, health: true, progress, cancellationToken);
            return ToConnectorCoreHealthReport(status, rawJson, healthy);
        }

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
        var uninstallProtocol = probe.ProtocolDetermined
            ? GatewayProtocols.Normalize(probe.CurrentProtocol)
            : selectedProtocol;

        if (string.IsNullOrWhiteSpace(uninstallProtocol))
        {
            uninstallProtocol = selectedProtocol;
        }

        var uninstallProtocolArg = BuildProtocolIdArg(uninstallProtocol);
        if (string.IsNullOrWhiteSpace(uninstallProtocolArg))
        {
            throw new InvalidOperationException("Cannot determine protocol for strict uninstall.");
        }

        // Keep pre-install uninstall backward-compatible with older gatewayctl versions
        // that don't understand newer protocol-specific flags.
        var args = BuildCommonArgs(request, includeBootstrapMode: true).Trim();
        var uninstallCommand =
            "set -euo pipefail; " +
            $"[ -x {ShellQuote(gatewayCtlPath)} ] || {{ echo 'Gateway control script not found during pre-install switch cleanup.'; exit 31; }}; " +
            $"{ShellQuote(gatewayCtlPath)} uninstall {args} {uninstallProtocolArg}";

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

        if (!uninstallResult.Success &&
            LooksLikeUnknownProtocolArgument(uninstallResult.ErrorMessage))
        {
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 3,
                Message = "Strict uninstall fallback: legacy gatewayctl detected, retrying without --protocol"
            });

            var fallbackUninstallCommand =
                "set -euo pipefail; " +
                $"[ -x {ShellQuote(gatewayCtlPath)} ] || {{ echo 'Gateway control script not found during pre-install switch cleanup.'; exit 31; }}; " +
                $"{ShellQuote(gatewayCtlPath)} uninstall {args}";

            uninstallResult = await ExecuteCommandAsync(
                request.Config,
                fallbackUninstallCommand,
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
                            Message = $"[switch-uninstall-legacy] {message}"
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
        }

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

        if (!await GatewayCtlExistsAsync(request, sudoPassword, cancellationToken))
        {
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 4,
                Message = "Skipping runtime backend preflight for first-time install (gatewayctl not present yet)"
            });
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

    private async Task<bool> GatewayCtlExistsAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        CancellationToken cancellationToken)
    {
        var gatewayCtlPath = GetGatewayCtlPath(request);
        var command = $"set -euo pipefail; [ -x {ShellQuote(gatewayCtlPath)} ]";
        var result = await ExecuteCommandAsync(
            request.Config,
            command,
            sudoPassword,
            null,
            cancellationToken);
        return result.Success;
    }

    private async Task WaitForFrpBackendListenerAfterInstallAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var backendPort = request.Config.TunnelRemotePort;
        if (backendPort <= 0 || backendPort > 65535)
        {
            return;
        }

        var command =
            "set -euo pipefail; " +
            $"ss -lnt '( sport = :{backendPort} )' 2>/dev/null | awk 'NR>1{{found=1}} END{{exit found?0:1}}'";

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteCommandAsync(
                request.Config,
                command,
                sudoPassword,
                null,
                cancellationToken);

            if (result.Success)
            {
                progress?.Report(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayInstall,
                    Percent = 95,
                    Message = $"FRP backend listener is active on VPS (127.0.0.1:{backendPort})"
                });
                return;
            }

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 90,
                Message = $"Waiting for FRP backend listener on VPS (127.0.0.1:{backendPort}) ({attempt}/6)"
            });

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private async Task<(bool Success, Process? Process, string Message, int RemotePort)> StartTemporaryBootstrapTunnelAsync(
        GatewayDeploymentRequest request,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var localSocksPort = await ResolveTemporaryBootstrapLocalSocksPortAsync(request.Config, cancellationToken);
        var attemptedRepair = false;
        foreach (var temporaryRemotePort in BuildTemporaryBootstrapPortCandidates(request))
        {
            var forwards = new List<(int RemotePort, int LocalPort)>
            {
                (temporaryRemotePort, localSocksPort)
            };

            if (!SshCliStartInfoFactory.TryCreateBoundSshReverseForwardStartInfo(
                    request.Config,
                    forwards,
                    out var startInfo,
                    out var bindIp,
                    out var error) || startInfo is null)
            {
                return (false, null, error ?? "Cannot prepare temporary bootstrap reverse tunnel command.", 0);
            }

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayBootstrap,
                Percent = 5,
                Message = $"Opening temporary bootstrap SOCKS reverse tunnel from IC1 IPv4 {bindIp} (local 127.0.0.1:{localSocksPort})"
            });

            Process? process = null;
            try
            {
                process = Process.Start(startInfo);
                if (process is null)
                {
                    return (false, null, "Failed to start temporary bootstrap reverse tunnel process.", 0);
                }

                process.StandardInput.Close();
                await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
                if (process.HasExited)
                {
                    var stderr = (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim();
                    var stdout = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
                    var detail = FirstNonEmpty(stderr, stdout) ?? $"ssh exited with code {process.ExitCode}.";
                    process.Dispose();

                    if (!attemptedRepair && SshCliStartInfoFactory.LooksLikeHostKeyMismatch(detail))
                    {
                        attemptedRepair = true;
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = DeploymentPhases.GatewayBootstrap,
                            Percent = 5,
                            Message = "Detected SSH host-key mismatch; repairing known_hosts entry and retrying bootstrap tunnel"
                        });

                        var repair = await SshCliStartInfoFactory.TryRepairKnownHostEntryAsync(request.Config, cancellationToken);
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = DeploymentPhases.GatewayBootstrap,
                            Percent = 5,
                            Message = repair.Message
                        });
                        if (repair.Success)
                        {
                            continue;
                        }

                        return (false, null, $"Temporary bootstrap tunnel failed: {detail} | {repair.Message}", 0);
                    }

                    if (LooksLikeRemoteForwardPortInUse(detail))
                    {
                        progress?.Report(new DeploymentProgressSnapshot
                        {
                            Phase = DeploymentPhases.GatewayBootstrap,
                            Percent = 5,
                            Message = $"Temporary bootstrap port {temporaryRemotePort} is busy on VPS; trying next candidate"
                        });
                        continue;
                    }

                    return (false, null, $"Temporary bootstrap tunnel failed: {detail}", 0);
                }

                progress?.Report(new DeploymentProgressSnapshot
                {
                    Phase = DeploymentPhases.GatewayBootstrap,
                    Percent = 8,
                    Message = $"Temporary bootstrap SOCKS reverse tunnel established on VPS port {temporaryRemotePort}"
                });

                return (true, process, "Temporary bootstrap SOCKS reverse tunnel established.", temporaryRemotePort);
            }
            catch (OperationCanceledException)
            {
                if (process is not null)
                {
                    await StopProcessAsync(process);
                    process.Dispose();
                }

                throw;
            }
            catch (Exception ex)
            {
                if (process is not null)
                {
                    await StopProcessAsync(process);
                    process.Dispose();
                }

                return (false, null, $"Temporary bootstrap tunnel failed: {ex.Message}", 0);
            }
        }

        return (false, null, "Temporary bootstrap tunnel failed: no available temporary SSH bootstrap port on VPS.", 0);
    }

    private static async Task<int> ResolveTemporaryBootstrapLocalSocksPortAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        var dataPort = config.LocalProxyListenPort;
        if (dataPort > 0 && await IsLoopbackTcpListeningAsync(dataPort, cancellationToken))
        {
            return dataPort;
        }

        return dataPort;
    }

    private static async Task<bool> IsLoopbackTcpListeningAsync(int port, CancellationToken cancellationToken)
    {
        if (port <= 0 || port > 65535)
        {
            return false;
        }

        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(800));
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StopTemporaryBootstrapTunnelAsync(
        Process? process,
        IProgress<DeploymentProgressSnapshot>? progress)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayBootstrap,
                Percent = 100,
                Message = "Closing temporary bootstrap reverse tunnel"
            });
            await StopProcessAsync(process);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static IEnumerable<int> BuildTemporaryBootstrapPortCandidates(GatewayDeploymentRequest request)
    {
        var reserved = new HashSet<int>
        {
            request.Config.TunnelRemotePort,
            request.Config.TunnelRemotePort
        };

        for (var port = 26080; port <= 26120; port++)
        {
            if (!reserved.Contains(port))
            {
                yield return port;
            }
        }

        for (var port = 20080; port <= 20120; port++)
        {
            if (!reserved.Contains(port))
            {
                yield return port;
            }
        }
    }

    private static bool LooksLikeRemoteForwardPortInUse(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        return detail.Contains("remote port forwarding failed for listen port", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("cannot listen to port", StringComparison.OrdinalIgnoreCase);
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
        var tunnelCtlPath = GetTunnelCtlPath(request);
        var tunnelCtlConfigDir = GetTunnelCtlConfigDir(request);
        var tunnelCtlQuoted = ShellQuote(tunnelCtlPath);
        var tunnelCtlPrefix = $"TUNNELCTL_CONFIG_DIR={ShellQuote(tunnelCtlConfigDir)}";
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
                $"{tunnelCtlPrefix} {tunnelCtlQuoted} remediate --level {ShellQuote(level)} --backend-host 127.0.0.1 --backend-port {backendPort} --json || true; " +
                $"{tunnelCtlPrefix} {tunnelCtlQuoted} probe --backend-host 127.0.0.1 --backend-port {backendPort} --json || true";

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

        var probeUrl = string.IsNullOrWhiteSpace(request.TunnelProbeUrl)
            ? "https://1.1.1.1/cdn-cgi/trace"
            : request.TunnelProbeUrl.Trim();
        var tunnelCtlPath = GetTunnelCtlPath(request);
        var tunnelCtlConfigDir = GetTunnelCtlConfigDir(request);
        var relayId = NormalizeRelayId(request.RelayId);
        var command =
            "set -euo pipefail; " +
            $"chmod +x {ShellQuote(RemoteTunnelModuleScriptPath)}; " +
            $"sed -i 's/\\r$//' {ShellQuote(RemoteTunnelModuleScriptPath)} || true; " +
            $"bash -n {ShellQuote(RemoteTunnelModuleScriptPath)} >/tmp/omnirelay-tunnelctl.syntax.log 2>&1 || {{ cat /tmp/omnirelay-tunnelctl.syntax.log; exit 43; }}; " +
            $"bash {ShellQuote(RemoteTunnelModuleScriptPath)} install " +
            $"--install-path {ShellQuote(tunnelCtlPath)} " +
            $"--config-dir {ShellQuote(tunnelCtlConfigDir)} " +
            (string.IsNullOrWhiteSpace(relayId) ? string.Empty : $"--relay-id {ShellQuote(relayId)} ") +
            $"--backend-host '127.0.0.1' " +
            $"--backend-port {request.Config.TunnelRemotePort} " +
            $"--probe-url {ShellQuote(probeUrl)} " +
            $"--timeout 12 --json; " +
            $"[ -x {ShellQuote(tunnelCtlPath)} ] || {{ echo 'Tunnel module did not install correctly.'; exit 44; }}; " +
            $"sed -i 's/\\r$//' {ShellQuote(tunnelCtlPath)} || true; " +
            $"TUNNELCTL_CONFIG_DIR={ShellQuote(tunnelCtlConfigDir)} /usr/bin/env bash {ShellQuote(tunnelCtlPath)} status --json >/tmp/omnirelay-tunnelctl.status.log 2>&1 || {{ cat /tmp/omnirelay-tunnelctl.status.log; exit 45; }}";

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

    private static string ResolveConnectorCoreBootstrapScriptPath()
    {
        var baseDir = AppContext.BaseDirectory;
        const string scriptFileName = "bootstrap_omnirelay_connector_core.sh";
        var candidates = new[]
        {
            Path.Combine(baseDir, "GatewayScripts", scriptFileName),
            Path.Combine(baseDir, scriptFileName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "scripts", scriptFileName))
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Connector-core bootstrap script not found. Expected {scriptFileName} in app GatewayScripts content.");
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

    private static string GetTunnelCtlPath(GatewayDeploymentRequest request)
    {
        var relayId = NormalizeRelayId(request.RelayId);
        return string.IsNullOrWhiteSpace(relayId)
            ? TunnelCtlPath
            : $"/usr/local/sbin/omnirelay-tunnelctl-{relayId}";
    }

    private static string GetTunnelCtlConfigDir(GatewayDeploymentRequest request)
    {
        var relayId = NormalizeRelayId(request.RelayId);
        return string.IsNullOrWhiteSpace(relayId)
            ? "/etc/omnirelay/tunnelctl"
            : $"/etc/omnirelay/relays/{relayId}/tunnelctl";
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

    private async Task<GatewayOperationResult> RunConnectorCoreGatewayCommandAsync(
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
            var relayId = NormalizeRelayId(request.RelayId).ToLowerInvariant();
            var command =
                "set -euo pipefail; " +
                $"[ -x {ShellQuote(ConnectorCorePath)} ] || {{ echo 'connector-core is not installed. Run Install Gateway first.'; exit 31; }}; " +
                $"{ShellQuote(ConnectorCorePath)} {operation} --relay-id {ShellQuote(relayId)} --json";
            var result = await ExecuteConnectorCoreCommandAsync(request, sudoPassword, command, phase, progress, cancellationToken);
            return result.Success
                ? new GatewayOperationResult(true, $"Connector-core {operation} completed.")
                : new GatewayOperationResult(false, result.ErrorMessage);
        }
        catch (Exception ex)
        {
            return new GatewayOperationResult(false, $"Connector-core {operation} failed: {ex.Message}");
        }
    }

    private async Task<GatewayOperationResult> CheckConnectorCoreGatewayDnsAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateRequest(request);
            EnsureSudoPassword(sudoPassword);
            var relayId = NormalizeRelayId(request.RelayId).ToLowerInvariant();
            var command =
                "set -euo pipefail; " +
                $"[ -x {ShellQuote(ConnectorCorePath)} ] || {{ echo 'connector-core is not installed. Run Install Gateway first.'; exit 31; }}; " +
                $"{ShellQuote(ConnectorCorePath)} dns status --relay-id {ShellQuote(relayId)} --json";
            var result = await ExecuteConnectorCoreCommandAsync(
                request,
                sudoPassword,
                command,
                DeploymentPhases.GatewayHealth,
                progress,
                cancellationToken);
            var json = ExtractLastJsonLine(result.Output);
            var envelope = DeserializeConnectorCoreEnvelope<ConnectorCoreDnsStatusDto>(json);
            var healthy = envelope?.Data?.Healthy == true;
            return new GatewayOperationResult(
                healthy,
                healthy ? "Gateway DNS path check passed." : $"Gateway DNS path check failed: {envelope?.Data?.ReasonCode ?? result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            return new GatewayOperationResult(false, $"Gateway DNS check failed: {ex.Message}");
        }
    }

    private async Task<GatewayServiceStatus> GetConnectorCoreStatusAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        bool health,
        CancellationToken cancellationToken)
    {
        var (status, _, _) = await GetConnectorCoreStatusWithRawAsync(
            request,
            sudoPassword,
            health,
            progress: null,
            cancellationToken: cancellationToken);
        return status;
    }

    private async Task<(GatewayServiceStatus Status, string RawJson, bool Healthy)> GetConnectorCoreStatusWithRawAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        bool health,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        EnsureSudoPassword(sudoPassword);
        var relayId = NormalizeRelayId(request.RelayId).ToLowerInvariant();
        var operation = health ? "health" : "status";
        var command =
            "set -euo pipefail; " +
            $"[ -x {ShellQuote(ConnectorCorePath)} ] || {{ echo 'connector-core is not installed. Run Install Gateway first.'; exit 31; }}; " +
            $"{ShellQuote(ConnectorCorePath)} gateway {operation} --relay-id {ShellQuote(relayId)} --json";
        var result = await ExecuteConnectorCoreCommandAsync(
            request,
            sudoPassword,
            command,
            DeploymentPhases.GatewayHealth,
            progress,
            cancellationToken);
        var rawJson = ExtractLastJsonLine(result.Output) ?? "{}";
        var envelope = DeserializeConnectorCoreEnvelope<ConnectorCoreGatewayStatusDto>(rawJson);
        if (envelope?.Data is null)
        {
            return (new GatewayServiceStatus
            {
                SshState = result.Success ? "unknown" : "error",
                SingBoxState = result.Success ? "unknown" : "error",
                TunnelReason = result.ErrorMessage
            }, rawJson, false);
        }
        return (MapConnectorCoreStatus(request, envelope.Data), rawJson, envelope.Data.Healthy);
    }

    private async Task<CommandExecutionResult> ExecuteConnectorCoreCommandAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        string command,
        string phase,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        return await ExecuteCommandAsync(
            request.Config,
            command,
            sudoPassword,
            line =>
            {
                var clean = SanitizeTerminalLine(line);
                if (TryParseConnectorCoreProgressLine(clean, out var pct, out var message))
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
    }

    private static GatewayServiceStatus MapConnectorCoreStatus(
        GatewayDeploymentRequest request,
        ConnectorCoreGatewayStatusDto data)
    {
        var backend = data.Backend;
        var dns = data.Dns;
        var targetActive = string.Equals(data.TargetState, "active", StringComparison.OrdinalIgnoreCase);
        return new GatewayServiceStatus
        {
            ActiveProtocol = string.IsNullOrWhiteSpace(data.Protocol) ? GatewayProtocols.Normalize(request.SelectedGatewayProtocol) : data.Protocol,
            SshState = "active",
            SingBoxState = data.ConnectorState,
            OpenVpnState = data.OpenVpnState,
            IpsecState = data.IpsecState,
            Xl2tpdState = data.Xl2tpdState,
            OmniPanelState = data.PanelState,
            NginxState = data.NginxState,
            Fail2BanState = "disabled",
            BackendPort = backend?.BackendPort ?? request.Config.TunnelRemotePort,
            PublicPort = request.GatewayPublicPort,
            PanelPort = request.GatewayPanelPort,
            BackendListener = backend?.BackendListener ?? targetActive,
            PublicListener = targetActive,
            PanelListener = string.Equals(data.PanelState, "active", StringComparison.OrdinalIgnoreCase),
            DnsConfigPresent = dns is not null,
            DnsRuleActive = dns?.Healthy == true,
            DohReachableViaTunnel = backend?.EgressReachable == true,
            DnsPathHealthy = dns?.Healthy == true,
            DohEndpoints = request.GatewayDohEndpoints,
            TunnelHealthy = backend?.Healthy ?? targetActive,
            TunnelReason = backend?.ReasonCode ?? data.ReasonCode,
            TunnelBackendProtocol = backend?.BackendProtocol ?? "unknown",
            TunnelEgressReachable = backend?.EgressReachable == true
        };
    }

    private static GatewayHealthReport ToConnectorCoreHealthReport(
        GatewayServiceStatus status,
        string rawJson,
        bool healthy)
    {
        return new GatewayHealthReport
        {
            Healthy = healthy,
            RawJson = rawJson,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            ActiveProtocol = status.ActiveProtocol,
            SshState = status.SshState,
            SingBoxState = status.SingBoxState,
            OpenVpnState = status.OpenVpnState,
            IpsecState = status.IpsecState,
            Xl2tpdState = status.Xl2tpdState,
            OmniPanelState = status.OmniPanelState,
            NginxState = status.NginxState,
            Fail2BanState = status.Fail2BanState,
            BackendPort = status.BackendPort,
            PublicPort = status.PublicPort,
            PanelPort = status.PanelPort,
            OmniPanelInternalPort = status.OmniPanelInternalPort,
            BackendListener = status.BackendListener,
            PublicListener = status.PublicListener,
            PanelListener = status.PanelListener,
            OmniPanelInternalListener = status.OmniPanelInternalListener,
            DnsConfigPresent = status.DnsConfigPresent,
            DnsRuleActive = status.DnsRuleActive,
            DohReachableViaTunnel = status.DohReachableViaTunnel,
            DnsPathHealthy = status.DnsPathHealthy,
            InboundId = status.InboundId,
            DohEndpoints = status.DohEndpoints,
            TunnelHealthy = status.TunnelHealthy,
            TunnelReason = status.TunnelReason,
            TunnelBackendProtocol = status.TunnelBackendProtocol,
            TunnelEgressReachable = status.TunnelEgressReachable
        };
    }

    private static ConnectorCoreEnvelope<T>? DeserializeConnectorCoreEnvelope<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        return JsonSerializer.Deserialize<ConnectorCoreEnvelope<T>>(json, JsonOptions);
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
        string openVpnSharedTlsCryptKeyRemotePath,
        int? bootstrapSocksRemotePortOverride = null)
    {
        var args =
            $"install {BuildCommonArgs(request, includeBootstrapMode: true, bootstrapSocksRemotePortOverride: bootstrapSocksRemotePortOverride)} {BuildProtocolArgs(request)} " +
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

    private static string BuildCommonArgs(
        GatewayDeploymentRequest request,
        bool includeBootstrapMode = false,
        int? bootstrapSocksRemotePortOverride = null)
    {
        var releaseChannel = ResolveGatewayAssetReleaseChannel();
        var bootstrapSocksPort = bootstrapSocksRemotePortOverride is > 0 and <= 65535
            ? bootstrapSocksRemotePortOverride.Value
            : (request.Config.TunnelRemotePort is > 0 and <= 65535 ? request.Config.TunnelRemotePort : request.Config.TunnelRemotePort);
        var args = new List<string>
        {
            "--public-port", request.GatewayPublicPort.ToString(),
            "--panel-port", request.GatewayPanelPort.ToString(),
            "--backend-port", request.Config.TunnelRemotePort.ToString(),
            "--ssh-port", request.Config.TunnelSshPort.ToString(),
            "--frp-server-port", request.Config.FrpServerPort.ToString(),
            "--frp-auth-token", ShellQuote((request.Config.FrpRuntimeToken ?? string.Empty).Trim()),
            "--release-channel", ShellQuote(releaseChannel),
            "--bootstrap-socks-port", bootstrapSocksPort.ToString(),
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

    private static string ResolveGatewayAssetReleaseChannel()
    {
        var raw = Environment.GetEnvironmentVariable("OMNIRELAY_GATEWAY_ASSET_CHANNEL");
        var normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            try
            {
                var markerPath = Path.Combine(AppContext.BaseDirectory, "gateway_asset_channel.txt");
                if (File.Exists(markerPath))
                {
                    normalized = (File.ReadAllText(markerPath) ?? string.Empty).Trim().ToLowerInvariant();
                }
            }
            catch
            {
                normalized = string.Empty;
            }
        }

        return normalized == "beta" ? "beta" : "stable";
    }

    private static bool IsConnectorCoreGatewayCutoverEnabled()
    {
        var value = (Environment.GetEnvironmentVariable("OMNIRELAY_CONNECTOR_CORE_GATEWAY_CUTOVER") ?? string.Empty).Trim();
        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return File.Exists(Path.Combine(AppContext.BaseDirectory, "connector_core_release_public_key.pem"));
    }

    private static string ResolveConnectorCoreReleaseBaseUrl()
    {
        var value = (Environment.GetEnvironmentVariable("OMNIRELAY_RELEASE_BASE_URL") ?? "https://omnirelay.net").Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw new InvalidOperationException("OMNIRELAY_RELEASE_BASE_URL must be an absolute HTTPS URL.");
        }
        return value;
    }

    private static string ResolveConnectorCoreReleasePublicKeyPath()
    {
        var path = (Environment.GetEnvironmentVariable("OMNIRELAY_CONNECTOR_CORE_RELEASE_PUBLIC_KEY_FILE") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "connector_core_release_public_key.pem");
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                "OMNIRELAY_CONNECTOR_CORE_RELEASE_PUBLIC_KEY_FILE must reference the trusted connector-core release public key before enabling cutover.");
        }
        var content = File.ReadAllText(path);
        if (!content.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Connector-core release public key file is not a PEM public key.");
        }
        return path;
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

    private static string BuildProtocolIdArg(string? protocol)
    {
        var normalized = GatewayProtocols.Normalize(protocol);
        if (string.Equals(normalized, GatewayProtocols.VlessTlsSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.MixedSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.SocksSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.HttpSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.Hysteria2Singbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.TrojanSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.NaiveSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, GatewayProtocols.IpsecL2tpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return $"--protocol {ShellQuote(normalized)}";
        }

        return string.Empty;
    }

    private static bool LooksLikeUnknownProtocolArgument(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("Unknown argument: --protocol", StringComparison.OrdinalIgnoreCase);
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

        if (GatewayTypes.Normalize(request.Config.GatewayType) == GatewayTypes.Remote &&
            (request.Config.FrpServerPort <= 0 || request.Config.FrpServerPort > 65535))
        {
            throw new InvalidOperationException("FRP server port is invalid.");
        }

        if (GatewayTypes.Normalize(request.Config.GatewayType) == GatewayTypes.Remote &&
            string.IsNullOrWhiteSpace(request.Config.FrpRuntimeToken))
        {
            throw new InvalidOperationException("FRP auth token is missing for the selected VPS profile.");
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
        else if (string.Equals(selectedProtocol, GatewayProtocols.IpsecL2tpSingbox, StringComparison.OrdinalIgnoreCase)
                 && request.IpsecL2tpPreSharedKey.Trim().Length < 16)
        {
            throw new InvalidOperationException("IPSec/L2TP pre-shared key must contain at least 16 characters.");
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

        if (!Uri.TryCreate(request.TunnelProbeUrl, UriKind.Absolute, out var probeUri) ||
            (probeUri.Scheme != Uri.UriSchemeHttps && probeUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Tunnelctl probe URL must be a valid absolute http/https URL.");
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

    private static bool TryParseConnectorCoreProgressLine(string line, out int percent, out string message)
    {
        percent = 0;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                !string.Equals(type.GetString(), "progress", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            percent = root.TryGetProperty("percent", out var percentElement) && percentElement.TryGetInt32(out var value)
                ? Math.Clamp(value, 0, 100)
                : 0;
            message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
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

    private sealed class ConnectorCoreEnvelope<T>
    {
        public bool Ok { get; set; }
        public string Command { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public T? Data { get; set; }
    }

    private sealed class ConnectorCoreGatewayStatusDto
    {
        public string RelayId { get; set; } = string.Empty;
        public string Protocol { get; set; } = string.Empty;
        public bool Healthy { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public string TargetState { get; set; } = "unknown";
        public string ConnectorState { get; set; } = "unknown";
        public string OpenVpnState { get; set; } = "unknown";
        public string IpsecState { get; set; } = "unknown";
        public string Xl2tpdState { get; set; } = "unknown";
        public string PanelState { get; set; } = "unknown";
        public string NginxState { get; set; } = "unknown";
        public ConnectorCoreBackendStatusDto? Backend { get; set; }
        public ConnectorCoreDnsStatusDto? Dns { get; set; }
    }

    private sealed class ConnectorCoreBackendStatusDto
    {
        public bool Healthy { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public int BackendPort { get; set; }
        public bool BackendListener { get; set; }
        public string BackendProtocol { get; set; } = string.Empty;
        public bool EgressReachable { get; set; }
    }

    private sealed class ConnectorCoreDnsStatusDto
    {
        public bool Healthy { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public string ListenAddress { get; set; } = string.Empty;
        public int ListenPort { get; set; }
    }

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



