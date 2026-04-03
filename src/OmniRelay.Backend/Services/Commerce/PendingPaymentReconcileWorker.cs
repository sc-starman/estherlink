namespace OmniRelay.Backend.Services.Commerce;

public sealed class PendingPaymentReconcileWorker : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PendingPaymentReconcileWorker> _logger;

    public PendingPaymentReconcileWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PendingPaymentReconcileWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(ReconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ReconcileOnceAsync(stoppingToken);
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var commerceService = scope.ServiceProvider.GetRequiredService<ICommerceService>();
            var reconciledCount = await commerceService.ReconcilePendingPaymentsAsync(cancellationToken);

            if (reconciledCount > 0)
            {
                _logger.LogInformation("Pending-payment reconcile completed. reconciled={Count}", reconciledCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pending-payment reconcile iteration failed.");
        }
    }
}
