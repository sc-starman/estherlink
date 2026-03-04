using EstherLink.Core.Configuration;
using EstherLink.UI.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace EstherLink.UI.Services;

public sealed class GatewayDeploymentService : IGatewayDeploymentService, IGatewayHealthService
{
    private const string GatewayCtlPath = "/usr/local/sbin/omnirelay-gatewayctl";

    private readonly ISshHostKeyTrustStore _hostKeyTrustStore;

    public GatewayDeploymentService(ISshHostKeyTrustStore hostKeyTrustStore)
    {
        _hostKeyTrustStore = hostKeyTrustStore;
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

            var remoteRoot = GetRemoteRoot(request.Config);
            var remoteBundleTar = $"{remoteRoot}/omnirelay-vps-bundle-x64.tar.gz";

            progress?.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.Upload,
                Percent = 0,
                Message = "Connecting to VPS for upload"
            });

            await UploadBundleAsync(request, remoteRoot, remoteBundleTar, progress, cancellationToken);

            var installArgs = BuildInstallArgs(request, remoteRoot);
            var command =
                $"set -euo pipefail; " +
                $"mkdir -p {ShellQuote(remoteRoot)}; " +
                $"cd {ShellQuote(remoteRoot)}; " +
                $"calc=$(sha256sum {ShellQuote(remoteBundleTar)} | awk '{{print $1}}'); " +
                $"if [ \"$calc\" != {ShellQuote(request.BundleSha256)} ]; then echo 'Bundle checksum mismatch.'; exit 20; fi; " +
                $"rm -rf bundle; " +
                $"tar -xzf {ShellQuote(remoteBundleTar)}; " +
                $"chmod +x bundle/scripts/setup_omnirelay_vps_3xui.sh; " +
                $"bundle/scripts/setup_omnirelay_vps_3xui.sh {installArgs}";

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
                            Phase = DeploymentPhases.GatewayInstall,
                            Percent = pct,
                            Message = message
                        });
                    }
                },
                cancellationToken);

            if (!result.Success)
            {
                return new GatewayOperationResult(false, result.ErrorMessage);
            }

            return new GatewayOperationResult(true, "Gateway install completed.");
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

    public async Task<GatewayServiceStatus> GetStatusAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        EnsureSudoPassword(sudoPassword);

        var args = BuildCommonArgs(request) + " --json";
        var command =
            $"set -euo pipefail; " +
            $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo '{{\"sshState\":\"missing\",\"xuiState\":\"missing\",\"fail2banState\":\"missing\",\"backendPort\":0,\"publicPort\":0,\"panelPort\":0,\"backendListener\":false,\"publicListener\":false,\"panelListener\":false}}'; exit 0; }}; " +
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
            $"set -euo pipefail; " +
            $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo '{{\"healthy\":false,\"sshState\":\"missing\",\"xuiState\":\"missing\",\"fail2banState\":\"missing\",\"backendPort\":0,\"publicPort\":0,\"panelPort\":0,\"backendListener\":false,\"publicListener\":false,\"panelListener\":false}}'; exit 0; }}; " +
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
                $"set -euo pipefail; " +
                $"[ -x {ShellQuote(GatewayCtlPath)} ] || {{ echo 'Gateway control script is not installed on VPS.'; exit 31; }}; " +
                $"{ShellQuote(GatewayCtlPath)} {operation} {args}";

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
                            Phase = phase,
                            Percent = pct,
                            Message = message
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

    private async Task UploadBundleAsync(
        GatewayDeploymentRequest request,
        string remoteRoot,
        string remoteBundleTar,
        IProgress<DeploymentProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var sftp = new SftpClient(CreateConnectionInfo(request.Config));
            AttachHostKeyTrust(sftp, request.Config);

            sftp.Connect();
            try
            {
                EnsureRemoteDirectory(sftp, remoteRoot);

                using var fs = File.OpenRead(request.BundleLocalPath);
                var totalBytes = fs.Length <= 0 ? 1L : fs.Length;

                sftp.UploadFile(fs, remoteBundleTar, uploaded =>
                {
                    var pct = (int)Math.Clamp(Math.Round(uploaded * 100d / totalBytes, MidpointRounding.AwayFromZero), 0, 100);
                    progress?.Report(new DeploymentProgressSnapshot
                    {
                        Phase = DeploymentPhases.Upload,
                        Percent = pct,
                        Message = $"Uploading gateway bundle ({pct}%)"
                    });
                });
            }
            finally
            {
                if (sftp.IsConnected)
                {
                    sftp.Disconnect();
                }
            }
        }, cancellationToken);
    }

    private async Task<CommandExecutionResult> ExecuteCommandAsync(
        ServiceConfig config,
        string command,
        string sudoPassword,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var ssh = new SshClient(CreateConnectionInfo(config));
            AttachHostKeyTrust(ssh, config);
            ssh.Connect();

            try
            {
                var wrapped = WrapCommand(command, sudoPassword);
                using var cmd = ssh.CreateCommand(wrapped);
                var output = new StringBuilder();
                var asyncResult = cmd.BeginExecute();

                using var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8, true, 1024, leaveOpen: true);
                while (!asyncResult.IsCompleted || !reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = reader.ReadLine();
                    if (line is null)
                    {
                        Thread.Sleep(40);
                        continue;
                    }

                    output.AppendLine(line);
                    onLine?.Invoke(line);
                }

                cmd.EndExecute(asyncResult);

                if (!string.IsNullOrWhiteSpace(cmd.Error))
                {
                    foreach (var line in SplitLines(cmd.Error))
                    {
                        output.AppendLine(line);
                        onLine?.Invoke(line);
                    }
                }

                var success = cmd.ExitStatus == 0;
                return new CommandExecutionResult(success, output.ToString(), success ? string.Empty : FirstMeaningfulLine(output.ToString(), cmd.Error));
            }
            finally
            {
                if (ssh.IsConnected)
                {
                    ssh.Disconnect();
                }
            }
        }, cancellationToken);
    }

    private void AttachHostKeyTrust(BaseClient client, ServiceConfig config)
    {
        client.HostKeyReceived += (_, e) =>
        {
            var ok = _hostKeyTrustStore.ValidateAndRemember(config.TunnelHost.Trim(), config.TunnelSshPort, e.FingerPrint);
            e.CanTrust = ok;
        };
    }

    private static ConnectionInfo CreateConnectionInfo(ServiceConfig config)
    {
        var host = (config.TunnelHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("Tunnel host is required.");
        }

        var user = (config.TunnelUser ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new InvalidOperationException("Tunnel user is required.");
        }

        var port = config.TunnelSshPort;
        if (port <= 0 || port > 65535)
        {
            throw new InvalidOperationException("Tunnel SSH port is invalid.");
        }

        var authMethod = TunnelAuthMethods.Normalize(config.TunnelAuthMethod);
        AuthenticationMethod method;
        if (string.Equals(authMethod, TunnelAuthMethods.Password, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(config.TunnelPassword))
            {
                throw new InvalidOperationException("Tunnel password is required for password authentication.");
            }

            method = new PasswordAuthenticationMethod(user, config.TunnelPassword);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.TunnelPrivateKeyPath))
            {
                throw new InvalidOperationException("Tunnel private key path is required for host-key authentication.");
            }

            if (!File.Exists(config.TunnelPrivateKeyPath))
            {
                throw new InvalidOperationException($"Tunnel private key file not found: {config.TunnelPrivateKeyPath}");
            }

            PrivateKeyFile keyFile;
            if (string.IsNullOrWhiteSpace(config.TunnelPrivateKeyPassphrase))
            {
                keyFile = new PrivateKeyFile(config.TunnelPrivateKeyPath);
            }
            else
            {
                keyFile = new PrivateKeyFile(config.TunnelPrivateKeyPath, config.TunnelPrivateKeyPassphrase);
            }

            method = new PrivateKeyAuthenticationMethod(user, keyFile);
        }

        var connection = new ConnectionInfo(host, port, user, method)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        return connection;
    }

    private static void EnsureRemoteDirectory(SftpClient sftp, string absolutePath)
    {
        var normalized = absolutePath.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
        {
            return;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = normalized.StartsWith('/') ? "/" : string.Empty;

        foreach (var part in parts)
        {
            current = current.Length == 0 || current == "/"
                ? $"/{part}"
                : $"{current}/{part}";

            if (!sftp.Exists(current))
            {
                sftp.CreateDirectory(current);
            }
        }
    }

    private static string BuildInstallArgs(GatewayDeploymentRequest request, string remoteRoot)
    {
        return
            $"install --bundle-dir {ShellQuote($"{remoteRoot}/bundle")} {BuildCommonArgs(request)} --tunnel-auth {ShellQuote(MapTunnelAuth(request.Config))}";
    }

    private static string BuildCommonArgs(GatewayDeploymentRequest request)
    {
        return string.Join(" ", new[]
        {
            "--public-port", ShellQuote(request.GatewayPublicPort.ToString()),
            "--panel-port", ShellQuote(request.GatewayPanelPort.ToString()),
            "--backend-port", ShellQuote(request.Config.TunnelRemotePort.ToString()),
            "--ssh-port", ShellQuote(request.Config.TunnelSshPort.ToString()),
            "--tunnel-user", ShellQuote(request.Config.TunnelUser.Trim())
        });
    }

    private static string GetRemoteRoot(ServiceConfig config)
    {
        var user = (config.TunnelUser ?? string.Empty).Trim();
        return $"/home/{user}/omnirelay-gateway";
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

        if (request.GatewayPublicPort == request.GatewayPanelPort)
        {
            throw new InvalidOperationException("Gateway public and panel ports must be different.");
        }

        if (string.IsNullOrWhiteSpace(request.BundleLocalPath) || !File.Exists(request.BundleLocalPath))
        {
            throw new InvalidOperationException($"Gateway bundle file is missing: {request.BundleLocalPath}");
        }

        if (string.IsNullOrWhiteSpace(request.BundleSha256))
        {
            throw new InvalidOperationException("Gateway bundle checksum is missing.");
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
        var inner = $"printf '%s\\n' {ShellQuote(sudoPassword)} | sudo -S -p '' {command} 2>&1";
        return $"bash -lc {ShellQuote(inner)}";
    }

    private static string ShellQuote(string value)
    {
        return "'" + (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal) + "'";
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
        var first = SplitLines(output).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        first = SplitLines(error).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return string.IsNullOrWhiteSpace(first) ? "Remote command failed." : first.Trim();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CommandExecutionResult(bool Success, string Output, string ErrorMessage);

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
            PanelListener = PanelListener
        };
    }

    private sealed class GatewayHealthDto : GatewayStatusDto
    {
        public bool Healthy { get; set; }

        public GatewayHealthReport ToModel(string rawJson)
        {
            var baseModel = ToModel();
            return new GatewayHealthReport
            {
                Healthy = Healthy,
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
                PanelListener = baseModel.PanelListener
            };
        }
    }
}
