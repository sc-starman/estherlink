using System.Diagnostics;
using EstherLink.Core.Configuration;

namespace EstherLink.Service.Runtime;

internal static class SshTunnelProcessFactory
{
    private const string AskPassSecretEnv = "ESTHERLINK_SSH_SECRET";

    public static bool TryCreateReverseTunnelStartInfo(
        ServiceConfig config,
        out ProcessStartInfo? startInfo,
        out string? error)
    {
        var args = new List<string>
        {
            "-NT",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=30",
            "-o", "ServerAliveCountMax=3",
            "-o", "TCPKeepAlive=yes"
        };

        args.Add("-R");
        args.Add($"127.0.0.1:{config.TunnelRemotePort}:127.0.0.1:{config.LocalProxyListenPort}");

        return TryCreateStartInfo(config, args, out startInfo, out error);
    }

    public static bool TryCreateConnectionTestStartInfo(
        ServiceConfig config,
        out ProcessStartInfo? startInfo,
        out string? error)
    {
        var args = new List<string>
        {
            "-N",
            "-o", "ServerAliveInterval=10",
            "-o", "ServerAliveCountMax=1",
            "-o", "TCPKeepAlive=yes"
        };

        return TryCreateStartInfo(config, args, out startInfo, out error);
    }

    private static bool TryCreateStartInfo(
        ServiceConfig config,
        List<string> args,
        out ProcessStartInfo? startInfo,
        out string? error)
    {
        startInfo = null;
        error = ValidateRequiredFields(config);
        if (error is not null)
        {
            return false;
        }

        args.Add("-o");
        args.Add("ConnectTimeout=10");
        args.Add("-o");
        args.Add("StrictHostKeyChecking=accept-new");
        args.Add("-o");
        args.Add($"UserKnownHostsFile={Path.Combine(ServicePaths.RootDirectory, "known_hosts")}");

        if (!TryConfigureAuthentication(config, args, out var secret, out error))
        {
            return false;
        }

        args.Add($"{config.TunnelUser.Trim()}@{config.TunnelHost.Trim()}");
        args.Add("-p");
        args.Add(config.TunnelSshPort.ToString());

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(secret))
        {
            ConfigureAskPass(psi, secret);
        }

        startInfo = psi;
        return true;
    }

    private static string? ValidateRequiredFields(ServiceConfig config)
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

        if (config.TunnelRemotePort <= 0 || config.TunnelRemotePort > 65535)
        {
            return "Tunnel remote port must be between 1 and 65535.";
        }

        if (config.LocalProxyListenPort <= 0 || config.LocalProxyListenPort > 65535)
        {
            return "Local proxy listen port must be between 1 and 65535.";
        }

        return null;
    }

    private static bool TryConfigureAuthentication(
        ServiceConfig config,
        List<string> args,
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

            args.Add("-o");
            args.Add("PreferredAuthentications=password,keyboard-interactive");
            args.Add("-o");
            args.Add("PubkeyAuthentication=no");
            args.Add("-o");
            args.Add("NumberOfPasswordPrompts=1");
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

        args.Add("-i");
        args.Add(keyPath);
        args.Add("-o");
        args.Add("PreferredAuthentications=publickey");

        if (string.IsNullOrWhiteSpace(config.TunnelPrivateKeyPassphrase))
        {
            args.Add("-o");
            args.Add("BatchMode=yes");
        }
        else
        {
            secret = config.TunnelPrivateKeyPassphrase;
        }

        return true;
    }

    private static void ConfigureAskPass(ProcessStartInfo startInfo, string secret)
    {
        var launcherPath = EnsureAskPassLauncher();
        startInfo.Environment["SSH_ASKPASS"] = launcherPath;
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        startInfo.Environment["DISPLAY"] = "estherlink:0";
        startInfo.Environment[AskPassSecretEnv] = secret;
    }

    private static string EnsureAskPassLauncher()
    {
        ServicePaths.EnsureDirectories();

        var scriptPath = Path.Combine(ServicePaths.RootDirectory, "ssh-askpass.ps1");
        var launcherPath = Path.Combine(ServicePaths.RootDirectory, "ssh-askpass.cmd");

        if (!File.Exists(scriptPath))
        {
            var scriptContent = "$secret = $env:ESTHERLINK_SSH_SECRET; if ($null -ne $secret) { [Console]::Out.Write($secret) }";
            File.WriteAllText(scriptPath, scriptContent);
        }

        if (!File.Exists(launcherPath))
        {
            var launcherContent = "@echo off\r\npowershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"%~dp0ssh-askpass.ps1\"";
            File.WriteAllText(launcherPath, launcherContent);
        }

        return launcherPath;
    }
}
