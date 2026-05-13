using OmniRelay.Service.Runtime;

namespace OmniRelay.Service.Workers;

public sealed class LogRetentionWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(12);
    private readonly FileLogWriter _log;

    public LogRetentionWorker(FileLogWriter log)
    {
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
            _log.RunRetentionSweep();
            _log.Info($"Log retention sweep completed. retentionDays={FileLogWriter.RetentionDays}");
        }
        catch (Exception ex)
        {
            _log.Error("Log retention sweep failed.", ex);
        }
    }
}
