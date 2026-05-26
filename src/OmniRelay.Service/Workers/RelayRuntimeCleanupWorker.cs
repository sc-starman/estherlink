using OmniRelay.Service.Runtime;

namespace OmniRelay.Service.Workers;

public sealed class RelayRuntimeCleanupWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private readonly GatewayRuntime _runtime;
    private readonly FileLogWriter _log;

    public RelayRuntimeCleanupWorker(GatewayRuntime runtime, FileLogWriter log)
    {
        _runtime = runtime;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RunSweepSafely();

        using var timer = new PeriodicTimer(SweepInterval);
        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken))
        {
            RunSweepSafely();
        }
    }

    private void RunSweepSafely()
    {
        try
        {
            var root = ServicePaths.RelayGatewayRuntimeRootDirectory;
            Directory.CreateDirectory(root);

            var allowedNames = _runtime.GetConfigSnapshot().Relays
                .Select(relay => Path.GetFileName(ServicePaths.GetRelayLocalGatewayDirectory(relay.Id)))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var removed = 0;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(name) || allowedNames.Contains(name))
                {
                    continue;
                }

                if (!IsUnderRoot(root, directory))
                {
                    _log.Warn($"Skipped stale relay directory cleanup outside root boundary: {directory}");
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                    _log.Info($"Removed stale relay runtime directory '{name}'.");
                }
                catch (Exception ex)
                {
                    _log.Warn($"Failed to remove stale relay runtime directory '{name}': {ex.Message}");
                }
            }

            if (removed > 0)
            {
                _log.Info($"Relay runtime cleanup sweep completed. removed={removed}");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Relay runtime cleanup sweep failed.", ex);
        }
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
