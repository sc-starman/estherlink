using System.Diagnostics;
using System.ComponentModel;
using EstherLink.Core.Configuration;

namespace EstherLink.Service.Runtime;

public sealed class TunnelConnectionTester
{
    private readonly FileLogWriter _fileLog;
    private readonly ILogger<TunnelConnectionTester> _logger;

    public TunnelConnectionTester(FileLogWriter fileLog, ILogger<TunnelConnectionTester> logger)
    {
        _fileLog = fileLog;
        _logger = logger;
    }

    public async Task<(bool Success, string Message)> TestAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        if (!SshTunnelProcessFactory.TryCreateConnectionTestStartInfo(config, out var startInfo, out var error) || startInfo is null)
        {
            return (false, error ?? "Tunnel configuration is invalid.");
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "Unable to start ssh process.");
            }

            process.StandardInput.Close();

            var waitTask = process.WaitForExitAsync(cancellationToken);
            var runningDelay = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var completed = await Task.WhenAny(waitTask, runningDelay);

            if (completed == waitTask)
            {
                var stderr = (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim();
                var stdout = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();

                if (process.ExitCode == 0)
                {
                    return (true, "SSH connection established.");
                }

                var message = FirstNonEmpty(stderr, stdout) ?? $"SSH exited with code {process.ExitCode}.";
                return (false, message);
            }

            await StopProcessAsync(process);
            return (true, "SSH connection established.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Tunnel test failed.");
            _fileLog.Warn($"Tunnel connection test failed: {ex.Message}");
            return (false, ex.Message);
        }
        finally
        {
            if (process is not null)
            {
                await StopProcessAsync(process);
                process.Dispose();
            }
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

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
