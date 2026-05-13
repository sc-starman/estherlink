using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace OmniRelay.Backend.Services;

public sealed class NewsletterDispatchWorker : BackgroundService
{
    private static readonly TimeSpan DispatchInterval = TimeSpan.FromMinutes(5);
    private const int DispatchBatchSize = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NewsletterDispatchWorker> _logger;

    public NewsletterDispatchWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<NewsletterDispatchWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DispatchOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(DispatchInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await DispatchOnceAsync(stoppingToken);
        }
    }

    private async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var emailDeliveryService = scope.ServiceProvider.GetRequiredService<IEmailDeliveryService>();
            var newsletterService = scope.ServiceProvider.GetRequiredService<NewsletterService>();

            var campaign = await dbContext.Newsletters
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(x => x.State == NewsletterState.Running || x.State == NewsletterState.Pending, cancellationToken);

            if (campaign is null)
            {
                return;
            }

            if (campaign.State == NewsletterState.Pending)
            {
                campaign.State = NewsletterState.Running;
                campaign.StartedAt = DateTimeOffset.UtcNow;
                campaign.LastError = null;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var pendingClients = await dbContext.NewsletterClients
                .Where(x => x.NewsletterId == campaign.Id && x.State == NewsletterClientState.Pending)
                .OrderBy(x => x.Email)
                .Take(DispatchBatchSize)
                .ToListAsync(cancellationToken);

            foreach (var client in pendingClients)
            {
                var body = newsletterService.BuildBody(client.Email, client.Id);
                var subject = NewsletterService.BuildSubject(campaign.Version);

                try
                {
                    await emailDeliveryService.SendAsync(
                        new EmailDeliveryMessage(client.Email, subject, body, ToName: client.Email),
                        cancellationToken);

                    client.State = NewsletterClientState.Sent;
                    client.SentAt = DateTimeOffset.UtcNow;
                    client.Error = null;
                }
                catch (Exception ex)
                {
                    client.State = NewsletterClientState.Failed;
                    client.Error = ex.Message;
                    campaign.LastError = ex.Message;
                    _logger.LogError(ex, "Newsletter email delivery failed. campaignId={CampaignId} clientId={ClientId}", campaign.Id, client.Id);
                }
                finally
                {
                    client.SendAttempts += 1;
                }
            }

            campaign.TotalVisited = await dbContext.NewsletterClients
                .CountAsync(x => x.NewsletterId == campaign.Id && x.VisitedAt != null, cancellationToken);

            var remaining = await dbContext.NewsletterClients
                .AnyAsync(x => x.NewsletterId == campaign.Id && x.State == NewsletterClientState.Pending, cancellationToken);

            if (!remaining)
            {
                campaign.State = NewsletterState.Done;
                campaign.CompletedAt = DateTimeOffset.UtcNow;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Newsletter dispatch worker iteration failed.");
        }
    }
}
