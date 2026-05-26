using System.Diagnostics;
using System.Text;
using OmniRelay.Core.Configuration;

namespace OmniRelay.Service.Runtime;

internal static class FrpTunnelProcessFactory
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal sealed record LaunchContext(
        string ConfigPath,
        string StatePath,
        string DataProxyName);

    public static bool TryCreateReverseTunnelStartInfo(
        ServiceConfig config,
        string relayId,
        out ProcessStartInfo? startInfo,
        out LaunchContext? launchContext,
        out string? error)
    {
        startInfo = null;
        launchContext = null;
        error = ValidateRequiredFields(config);
        if (error is not null)
        {
            return false;
        }
        var normalizedRelayId = NormalizeRelayId(relayId);
        var dataProxyName = $"relay-{normalizedRelayId}-data-{config.TunnelRemotePort}";
        var configPath = ServicePaths.GetRelayFrpcConfigPath(relayId);
        var statePath = ServicePaths.GetRelayFrpTunnelStatePath(relayId);

        try
        {
            ServicePaths.EnsureDirectories();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? ServicePaths.RootDirectory);
            File.WriteAllText(
                configPath,
                BuildFrpcConfig(config, dataProxyName),
                Utf8NoBom);
        }
        catch (Exception ex)
        {
            error = $"Failed to write FRP runtime config: {ex.Message}";
            return false;
        }

        var executable = ServicePaths.ResolveConnectorCoreExecutablePath();
        if (!File.Exists(executable))
        {
            error = $"Connector runtime is missing: {executable}. Reinstall OmniRelay to restore connector-core.";
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("tunnel");
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add("--state-file");
        psi.ArgumentList.Add(statePath);
        psi.ArgumentList.Add("--strict-config=true");

        startInfo = psi;
        launchContext = new LaunchContext(configPath, statePath, dataProxyName);
        return true;
    }

    private static string? ValidateRequiredFields(ServiceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.TunnelHost))
        {
            return "Tunnel host is required.";
        }

        if (config.FrpServerPort <= 0 || config.FrpServerPort > 65535)
        {
            return "FRP server port must be between 1 and 65535.";
        }

        if (string.IsNullOrWhiteSpace(config.FrpRuntimeToken))
        {
            return "FRP auth token is required.";
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

    private static string NormalizeRelayId(string relayId)
    {
        var raw = (relayId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "default";
        }

        var filtered = new string(raw.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "default" : filtered;
    }

    private static string BuildFrpcConfig(
        ServiceConfig config,
        string dataProxyName)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"serverAddr = {ToTomlString(config.TunnelHost.Trim())}");
        builder.AppendLine($"serverPort = {config.FrpServerPort}");
        builder.AppendLine("auth.method = \"token\"");
        builder.AppendLine($"auth.token = {ToTomlString(config.FrpRuntimeToken.Trim())}");
        builder.AppendLine("transport.protocol = \"tcp\"");
        builder.AppendLine("loginFailExit = false");
        builder.AppendLine();
        builder.AppendLine("[[proxies]]");
        builder.AppendLine($"name = {ToTomlString(dataProxyName)}");
        builder.AppendLine("type = \"tcp\"");
        builder.AppendLine("localIP = \"127.0.0.1\"");
        builder.AppendLine($"localPort = {config.LocalProxyListenPort}");
        builder.AppendLine($"remotePort = {config.TunnelRemotePort}");
        return builder.ToString();
    }

    private static string ToTomlString(string value)
    {
        var safe = (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{safe}\"";
    }
}

