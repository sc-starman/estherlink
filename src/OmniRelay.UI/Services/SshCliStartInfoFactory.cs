using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace OmniRelay.UI.Services;

internal static class SshCliStartInfoFactory
{
    private const string AskPassSecretEnv = "OmniRelay_UI_SSH_SECRET";

    public static string GetKnownHostsPath()
    {
        return EnsureKnownHostsPath();
    }

    public static bool LooksLikeHostKeyMismatch(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Offending", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<(bool Success, string Message)> TryRepairKnownHostEntryAsync(
        ServiceConfig config,
        CancellationToken cancellationToken = default)
    {
        var host = (config.TunnelHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return (false, "Cannot repair SSH known_hosts: tunnel host is empty.");
        }

        var knownHostsPath = EnsureKnownHostsPath();
        var patterns = BuildKnownHostPatterns(host, config.TunnelSshPort);
        var details = new List<string>();
        var anySuccess = false;

        foreach (var pattern in patterns)
        {
            var (success, message) = await RemoveKnownHostPatternAsync(pattern, knownHostsPath, cancellationToken);
            details.Add(message);
            if (success)
            {
                anySuccess = true;
            }
        }

        var combined = details.Count == 0
            ? "No host patterns available to clear."
            : string.Join(" | ", details);

        return (anySuccess, combined);
    }

    public static bool TryResolveIc1BindIp(ServiceConfig config, out string bindIp, out string? error)
    {
        bindIp = string.Empty;
        error = null;

        if (config.WhitelistAdapterIfIndex <= 0)
        {
            error = "VPS Network (IC1) adapter is not selected.";
            return false;
        }

        if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(config.WhitelistAdapterIfIndex, out var ip) || ip is null || IPAddress.IsLoopback(ip))
        {
            error = $"VPS Network (IC1) adapter has no usable IPv4 address (IfIndex={config.WhitelistAdapterIfIndex}).";
            return false;
        }

        bindIp = ip.ToString();
        return true;
    }

    public static bool TryCreateBoundSshCommandStartInfo(
        ServiceConfig config,
        string remoteCommand,
        out ProcessStartInfo? startInfo,
        out string bindIp,
        out string? error)
    {
        var preTargetArgs = new[]
        {
            "-o", "ConnectTimeout=10",
            "-o", "StrictHostKeyChecking=accept-new"
        };

        return TryCreateSshStartInfo(
            config,
            preTargetArgs,
            remoteCommand,
            out startInfo,
            out bindIp,
            out error);
    }

    public static bool TryCreateBoundSshConnectionTestStartInfo(
        ServiceConfig config,
        out ProcessStartInfo? startInfo,
        out string bindIp,
        out string? error)
    {
        var preTargetArgs = new[]
        {
            "-N",
            "-o", "ServerAliveInterval=10",
            "-o", "ServerAliveCountMax=1",
            "-o", "TCPKeepAlive=yes",
            "-o", "ConnectTimeout=10",
            "-o", "StrictHostKeyChecking=accept-new"
        };

        return TryCreateSshStartInfo(
            config,
            preTargetArgs,
            remoteCommand: null,
            out startInfo,
            out bindIp,
            out error);
    }

    public static bool TryCreateBoundScpUploadStartInfo(
        ServiceConfig config,
        string localFilePath,
        string remoteFilePath,
        out ProcessStartInfo? startInfo,
        out string bindIp,
        out string? error)
    {
        startInfo = null;
        bindIp = string.Empty;
        error = ValidateRequiredTunnelFields(config);
        if (error is not null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            error = $"Local file for upload was not found: {localFilePath}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(remoteFilePath))
        {
            error = "Remote upload path is required.";
            return false;
        }

        if (!TryResolveIc1BindIp(config, out bindIp, out error))
        {
            return false;
        }

        var knownHostsPath = EnsureKnownHostsPath();
        var sshConfigPath = EnsureSshConfigPath();
        var psi = CreateBaseProcessStartInfo("scp");
        psi.ArgumentList.Add("-F");
        psi.ArgumentList.Add(sshConfigPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=10");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"UserKnownHostsFile={knownHostsPath}");
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(config.TunnelSshPort.ToString());

        if (!TryAppendAuthenticationArgs(config, psi, out var secret, out error))
        {
            return false;
        }

        psi.ArgumentList.Add(localFilePath);
        psi.ArgumentList.Add($"{config.TunnelUser.Trim()}@{config.TunnelHost.Trim()}:{remoteFilePath}");

        if (!string.IsNullOrWhiteSpace(secret))
        {
            ConfigureAskPass(psi, secret);
        }

        startInfo = psi;
        return true;
    }

    private static bool TryCreateSshStartInfo(
        ServiceConfig config,
        IReadOnlyList<string> preTargetArgs,
        string? remoteCommand,
        out ProcessStartInfo? startInfo,
        out string bindIp,
        out string? error)
    {
        startInfo = null;
        bindIp = string.Empty;
        error = ValidateRequiredTunnelFields(config);
        if (error is not null)
        {
            return false;
        }

        if (!TryResolveIc1BindIp(config, out bindIp, out error))
        {
            return false;
        }

        var knownHostsPath = EnsureKnownHostsPath();
        var sshConfigPath = EnsureSshConfigPath();
        var psi = CreateBaseProcessStartInfo("ssh");
        psi.ArgumentList.Add("-F");
        psi.ArgumentList.Add(sshConfigPath);
        foreach (var arg in preTargetArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"UserKnownHostsFile={knownHostsPath}");

        if (!TryAppendAuthenticationArgs(config, psi, out var secret, out error))
        {
            return false;
        }

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(config.TunnelSshPort.ToString());
        psi.ArgumentList.Add($"{config.TunnelUser.Trim()}@{config.TunnelHost.Trim()}");

        if (!string.IsNullOrWhiteSpace(remoteCommand))
        {
            psi.ArgumentList.Add(remoteCommand);
        }

        if (!string.IsNullOrWhiteSpace(secret))
        {
            ConfigureAskPass(psi, secret);
        }

        startInfo = psi;
        return true;
    }

    private static bool TryAppendAuthenticationArgs(
        ServiceConfig config,
        ProcessStartInfo startInfo,
        out string? secret,
        out string? error)
    {
        secret = null;
        error = null;

        var method = TunnelAuthMethods.Normalize(config.TunnelAuthMethod);
        if (string.Equals(method, TunnelAuthMethods.Password, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(config.TunnelPassword))
            {
                error = "Tunnel password is required for password authentication.";
                return false;
            }

            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("PreferredAuthentications=password,keyboard-interactive");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("PubkeyAuthentication=no");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("NumberOfPasswordPrompts=1");
            secret = config.TunnelPassword;
            return true;
        }

        var keyPath = (config.TunnelPrivateKeyPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            error = "Tunnel host key file path is required for host-key authentication.";
            return false;
        }

        if (!File.Exists(keyPath))
        {
            error = $"Tunnel host key file not found: {keyPath}";
            return false;
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(keyPath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("PreferredAuthentications=publickey");

        if (string.IsNullOrWhiteSpace(config.TunnelPrivateKeyPassphrase))
        {
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("BatchMode=yes");
        }
        else
        {
            secret = config.TunnelPrivateKeyPassphrase;
        }

        return true;
    }

    private static string? ValidateRequiredTunnelFields(ServiceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.TunnelHost))
        {
            return "Tunnel host is required.";
        }

        if (string.IsNullOrWhiteSpace(config.TunnelUser))
        {
            return "Tunnel user is required.";
        }

        if (config.TunnelSshPort <= 0 || config.TunnelSshPort > 65535)
        {
            return "Tunnel SSH port must be between 1 and 65535.";
        }

        return null;
    }

    private static ProcessStartInfo CreateBaseProcessStartInfo(string fileName)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
    }

    private static void ConfigureAskPass(ProcessStartInfo startInfo, string secret)
    {
        var launcherPath = EnsureAskPassLauncher();
        startInfo.Environment["SSH_ASKPASS"] = launcherPath;
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        startInfo.Environment["DISPLAY"] = "OmniRelay-ui:0";
        startInfo.Environment[AskPassSecretEnv] = secret;
    }

    private static string EnsureKnownHostsPath()
    {
        var root = EnsureSshUiRoot();
        return Path.Combine(root, "known_hosts_ui");
    }

    private static string EnsureSshConfigPath()
    {
        var root = EnsureSshUiRoot();
        var configPath = Path.Combine(root, "ssh_config");
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, string.Empty);
        }

        return configPath;
    }

    private static string EnsureAskPassLauncher()
    {
        var root = EnsureSshUiRoot();
        var scriptPath = Path.Combine(root, "ssh-askpass.ps1");
        var launcherPath = Path.Combine(root, "ssh-askpass.cmd");

        if (!File.Exists(scriptPath))
        {
            var scriptContent = $"$secret = $env:{AskPassSecretEnv}; if ($null -ne $secret) {{ [Console]::Out.Write($secret) }}";
            File.WriteAllText(scriptPath, scriptContent);
        }

        if (!File.Exists(launcherPath))
        {
            var launcherContent = "@echo off\r\npowershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"%~dp0ssh-askpass.ps1\"";
            File.WriteAllText(launcherPath, launcherContent);
        }

        return launcherPath;
    }

    private static string EnsureSshUiRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniRelay",
            "ssh-ui");
        Directory.CreateDirectory(root);
        return root;
    }

    private static IReadOnlyList<string> BuildKnownHostPatterns(string host, int port)
    {
        var cleanHost = host.Trim();
        if (cleanHost.StartsWith("[", StringComparison.Ordinal) && cleanHost.EndsWith("]", StringComparison.Ordinal) && cleanHost.Length > 2)
        {
            cleanHost = cleanHost[1..^1];
        }

        var patterns = new List<string>
        {
            cleanHost,
            $"[{cleanHost}]:{port}"
        };

        return patterns
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<(bool Success, string Message)> RemoveKnownHostPatternAsync(
        string pattern,
        string knownHostsPath,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-R");
        psi.ArgumentList.Add(pattern);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(knownHostsPath);

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return (false, $"ssh-keygen failed to start while clearing '{pattern}'.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode == 0)
            {
                return (true, $"Cleared stale known_hosts entry for '{pattern}'.");
            }

            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = $"ssh-keygen exit code {process.ExitCode}";
            }

            return (false, $"Failed to clear '{pattern}': {reason}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or TaskCanceledException or OperationCanceledException)
        {
            return (false, $"Failed to clear '{pattern}': {ex.Message}");
        }
    }
}
