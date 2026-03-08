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

            var command =
                "set -euo pipefail; " +
                $"ss -lnt '( sport = :{request.Config.BootstrapSocksRemotePort} )' 2>/dev/null | awk 'NR>1 {{print $0}}' | grep -q . || {{ echo 'SOCKS listener is not present on 127.0.0.1:{request.Config.BootstrapSocksRemotePort}'; exit 41; }}; " +
                "ok=0; " +
                "for i in 1 2 3; do " +
                $"  if curl --fail --silent --show-error --max-time 45 --connect-timeout 20 --retry 0 --socks5-hostname 127.0.0.1:{request.Config.BootstrapSocksRemotePort} https://deb.debian.org/ >/dev/null; then ok=1; break; fi; " +
                "  echo \"SOCKS egress probe attempt ${i}/3 failed, retrying...\"; " +
                "  sleep 3; " +
                "done; " +
                "if [ \"$ok\" != \"1\" ]; then echo 'SOCKS egress probe failed after retries.'; exit 42; fi; " +
                "echo 'Gateway bootstrap SOCKS check passed.'";

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

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayInstall,
                Percent = 5,
                Message = "Uploading gateway installer script"
            });

            await UploadInstallerScriptAsync(request, progress, cancellationToken);

            var panelUser = $"omniadmin_{RandomAlphaNum(6)}";
            var panelPassword = RandomAlphaNum(24);
            var panelBasePath = $"omni{RandomAlphaNum(14).ToLowerInvariant()}";
            var installArgs = BuildInstallArgs(request, panelUser, panelPassword, panelBasePath);
            var command =
                "set -euo pipefail; " +
                $"chmod +x {ShellQuote(RemoteInstallScriptPath)}; " +
                $"{ShellQuote(RemoteInstallScriptPath)} {installArgs}";

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

            var panelUrl = $"https://{request.Config.TunnelHost}:{request.GatewayPanelPort}/{panelBasePath}/";
            return new GatewayOperationResult(
                true,
                $"Gateway install completed. Panel URL: {panelUrl} | Username: {panelUser} | Password: {panelPassword}");
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
            $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo '{{\"sshState\":\"missing\",\"xuiState\":\"missing\",\"fail2banState\":\"disabled\",\"backendPort\":0,\"publicPort\":0,\"panelPort\":0,\"backendListener\":false,\"publicListener\":false,\"panelListener\":false,\"dnsConfigPresent\":false,\"dnsRuleActive\":false,\"dohReachableViaTunnel\":false,\"udp53PathReady\":false,\"dnsPathHealthy\":false}}'; exit 0; }}; " +
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
            $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo '{{\"healthy\":false,\"sshState\":\"missing\",\"xuiState\":\"missing\",\"fail2banState\":\"disabled\",\"backendPort\":0,\"publicPort\":0,\"panelPort\":0,\"backendListener\":false,\"publicListener\":false,\"panelListener\":false,\"dnsConfigPresent\":false,\"dnsRuleActive\":false,\"dohReachableViaTunnel\":false,\"udp53PathReady\":false,\"dnsPathHealthy\":false}}'; exit 0; }}; " +
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

    private async Task UploadInstallerScriptAsync(
        GatewayDeploymentRequest request,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var localScript = ResolveInstallerScriptPath();
        ProcessStartInfo? BuildStartInfo(out string bindIp, out string? error)
        {
            if (!SshCliStartInfoFactory.TryCreateBoundScpUploadStartInfo(
                    request.Config,
                    localScript,
                    RemoteInstallScriptPath,
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

    private static string ResolveInstallerScriptPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "GatewayScripts", "setup_omnirelay_vps_3xui.sh"),
            Path.Combine(baseDir, "setup_omnirelay_vps_3xui.sh"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "scripts", "setup_omnirelay_vps_3xui.sh"))
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Gateway installer script not found. Expected setup_omnirelay_vps_3xui.sh in app GatewayScripts content.");
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
        string panelBasePath)
    {
        return
            $"install {BuildCommonArgs(request)} " +
            $"--tunnel-auth {ShellQuote(MapTunnelAuth(request.Config))} " +
            $"--panel-user {ShellQuote(panelUser)} " +
            $"--panel-password {ShellQuote(panelPassword)} " +
            $"--panel-base-path {ShellQuote(panelBasePath)}";
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
        public string SshState { get; set; } = "unknown";
        public string XuiState { get; set; } = "unknown";
        public string Fail2banState { get; set; } = "unknown";
        public int BackendPort { get; set; }
        public int PublicPort { get; set; }
        public int PanelPort { get; set; }
        public bool BackendListener { get; set; }
        public bool PublicListener { get; set; }
        public bool PanelListener { get; set; }
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
            SshState = SshState,
            XuiState = XuiState,
            Fail2BanState = Fail2banState,
            BackendPort = BackendPort,
            PublicPort = PublicPort,
            PanelPort = PanelPort,
            BackendListener = BackendListener,
            PublicListener = PublicListener,
            PanelListener = PanelListener,
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
                SshState = baseModel.SshState,
                XuiState = baseModel.XuiState,
                Fail2BanState = baseModel.Fail2BanState,
                BackendPort = baseModel.BackendPort,
                PublicPort = baseModel.PublicPort,
                PanelPort = baseModel.PanelPort,
                BackendListener = baseModel.BackendListener,
                PublicListener = baseModel.PublicListener,
                PanelListener = baseModel.PanelListener,
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
