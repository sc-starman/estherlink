using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Models;
using System.Net.Http.Json;
using System.Text;

namespace OmniRelay.Backend.IntegrationTests;

public sealed class WebhookFulfillmentTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public WebhookFulfillmentTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Webhook_ShouldMarkOrderPaid_AndIssueLicense_Once()
    {
        await _factory.ResetDatabaseAsync();

        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var intentId = "pi_test_confirmed_001";

        _factory.PayKryptClient.Intents[intentId] = new OmniRelay.Backend.Services.Commerce.PayKryptIntentResponse
        {
            Id = intentId,
            Status = "confirmed",
            Amount = "149.00",
            Currency = "USD",
            TransactionsSummary = new OmniRelay.Backend.Services.Commerce.PayKryptTransactionsSummary
            {
                IsFullyPaid = true,
                OutstandingFiat = "0"
            }
        };

        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Users.Add(new ApplicationUser
            {
                Id = userId,
                Email = "buyer@example.com",
                UserName = "buyer@example.com",
                CreatedAt = DateTimeOffset.UtcNow,
                EmailConfirmed = true
            });

            dbContext.CommerceOrders.Add(new CommerceOrderEntity
            {
                Id = orderId,
                UserId = userId,
                OrderType = "license_purchase",
                FiatAmount = 149m,
                Currency = "USD",
                Status = "awaiting_payment",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            dbContext.PayKryptIntents.Add(new PayKryptIntentEntity
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                PayKryptIntentId = intentId,
                Status = "awaiting_payment",
                RawJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            await dbContext.SaveChangesAsync();
        });

        var client = _factory.CreateClient();
        var payload = "{\"paymentIntentId\":\"pi_test_confirmed_001\"}";

        var request1 = new HttpRequestMessage(HttpMethod.Post, "/webhooks/paykrypt")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request1.Headers.Add("X-PayKrypt-Event-Id", "evt_test_001");

        var response1 = await client.SendAsync(request1);
        response1.EnsureSuccessStatusCode();

        var request2 = new HttpRequestMessage(HttpMethod.Post, "/webhooks/paykrypt")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request2.Headers.Add("X-PayKrypt-Event-Id", "evt_test_001");

        var response2 = await client.SendAsync(request2);
        response2.EnsureSuccessStatusCode();

        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            var order = await dbContext.CommerceOrders.FindAsync(orderId);
            Assert.NotNull(order);
            Assert.Equal("paid", order!.Status);
            Assert.NotNull(order.IssuedLicenseId);

            var licenses = dbContext.Licenses.Count();
            Assert.Equal(1, licenses);

            var userLicenses = dbContext.UserLicenses.Count();
            Assert.Equal(1, userLicenses);

            var webhookEvents = dbContext.PayKryptWebhookEvents.Count();
            Assert.Equal(1, webhookEvents);
        });
    }
}