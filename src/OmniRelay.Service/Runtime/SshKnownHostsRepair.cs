using System.ComponentModel;
using System.Diagnostics;
using OmniRelay.Core.Configuration;

namespace OmniRelay.Service.Runtime;

internal static class SshKnownHostsRepair
{
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

    public static async Task<(bool Success, string Message)> TryRepairAsync(
        ServiceConfig config,
        CancellationToken cancellationToken = default)
    {
        var host = (config.TunnelHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return (false, "Cannot repair SSH known_hosts: tunnel host is empty.");
        }

        var knownHostsPath = Path.Combine(ServicePaths.RootDirectory, "known_hosts");
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
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
