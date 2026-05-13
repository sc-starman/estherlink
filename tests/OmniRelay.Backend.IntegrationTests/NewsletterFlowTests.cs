using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Data.Enums;
using OmniRelay.Backend.Models;
using OmniRelay.Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace OmniRelay.Backend.IntegrationTests;

public sealed class NewsletterFlowTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public NewsletterFlowTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateCampaign_ShouldUseConfirmedEmailsOnly_AndRejectWhenRunningExists()
    {
        await _factory.ResetDatabaseAsync();

        var confirmedA = Guid.NewGuid();
        var confirmedB = Guid.NewGuid();

        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Users.AddRange(
                new ApplicationUser { Id = confirmedA, Email = "a@user.local", UserName = "a@user.local", EmailConfirmed = true, CreatedAt = DateTimeOffset.UtcNow },
                new ApplicationUser { Id = confirmedB, Email = "b@user.local", UserName = "b@user.local", EmailConfirmed = true, CreatedAt = DateTimeOffset.UtcNow },
                new ApplicationUser { Id = Guid.NewGuid(), Email = "c@user.local", UserName = "c@user.local", EmailConfirmed = false, CreatedAt = DateTimeOffset.UtcNow },
                new ApplicationUser { Id = Guid.NewGuid(), Email = "", UserName = "empty@user.local", EmailConfirmed = true, CreatedAt = DateTimeOffset.UtcNow }
            );
            await dbContext.SaveChangesAsync();
        });

        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<INewsletterService>();
        var db = scope.ServiceProvider.GetRequiredService<OmniRelay.Backend.Data.AppDbContext>();

        var createResult = await service.CreateLatestCampaignAsync(CancellationToken.None);
        Assert.True(createResult.Success);

        var campaign = await db.Newsletters.AsNoTracking().SingleAsync();
        Assert.Equal("2.1.24", campaign.Version);
        Assert.Equal(2, campaign.TotalUsers);
        Assert.Equal(NewsletterState.Pending, campaign.State);

        var clients = await db.NewsletterClients.AsNoTracking().Where(x => x.NewsletterId == campaign.Id).ToListAsync();
        Assert.Equal(2, clients.Count);
        Assert.Contains(clients, x => x.Email == "a@user.local");
        Assert.Contains(clients, x => x.Email == "b@user.local");

        await _factory.ExecuteDbContextAsync(async ctx =>
        {
            var item = await ctx.Newsletters.FirstAsync(x => x.Id == campaign.Id);
            item.State = NewsletterState.Running;
            await ctx.SaveChangesAsync();
        });

        var rejectResult = await service.CreateLatestCampaignAsync(CancellationToken.None);
        Assert.False(rejectResult.Success);
    }

    [Fact]
    public async Task TrackingEndpoint_ShouldMarkVisitedOnlyOnce_AndRedirectToDownload()
    {
        await _factory.ResetDatabaseAsync();

        Guid newsletterId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid clientId = Guid.NewGuid();

        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Users.Add(new ApplicationUser
            {
                Id = userId,
                Email = "visitor@user.local",
                UserName = "visitor@user.local",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow
            });

            dbContext.Newsletters.Add(new NewsletterEntity
            {
                Id = newsletterId,
                Version = "2.1.24",
                State = NewsletterState.Running,
                TotalUsers = 1,
                TotalVisited = 0,
                CreatedAt = DateTimeOffset.UtcNow
            });

            dbContext.NewsletterClients.Add(new NewsletterClientEntity
            {
                Id = clientId,
                NewsletterId = newsletterId,
                UserId = userId,
                Email = "visitor@user.local",
                State = NewsletterClientState.Sent,
                SendAttempts = 1,
                SentAt = DateTimeOffset.UtcNow
            });

            await dbContext.SaveChangesAsync();
        });

        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response1 = await client.GetAsync($"/nl/{clientId:D}");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response1.StatusCode);
        Assert.Equal("/download", response1.Headers.Location?.ToString());

        var response2 = await client.GetAsync($"/nl/{clientId:D}");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response2.StatusCode);

        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            var campaign = await dbContext.Newsletters.AsNoTracking().FirstAsync(x => x.Id == newsletterId);
            var recipient = await dbContext.NewsletterClients.AsNoTracking().FirstAsync(x => x.Id == clientId);

            Assert.Equal(1, campaign.TotalVisited);
            Assert.NotNull(recipient.VisitedAt);
            Assert.Equal(NewsletterClientState.Visited, recipient.State);
        });
    }
}
