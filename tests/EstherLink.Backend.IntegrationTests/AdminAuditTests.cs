using System.Net.Http.Json;
using EstherLink.Backend.Contracts.Licensing;

namespace EstherLink.Backend.IntegrationTests;

public sealed class AdminAuditTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public AdminAuditTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminPost_ShouldWriteAuditEvent()
    {
        await _factory.ResetDatabaseAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", "dev-admin-key");

        var request = new AdminCreateLicenseRequest
        {
            LicenseKey = "AUDIT-TEST-001",
            Status = "active",
            Plan = "pro",
            MaxDevices = 2,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };

        var response = await client.PostAsJsonAsync("/api/admin/licenses", request);
        response.EnsureSuccessStatusCode();

        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            var count = dbContext.AuditEvents.Count();
            Assert.True(count >= 1);
            await Task.CompletedTask;
        });
    }
}
