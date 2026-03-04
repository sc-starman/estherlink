using System.Net.Http.Json;
using EstherLink.Backend.Contracts.Licensing;
using EstherLink.Backend.Data.Entities;
using EstherLink.Backend.Data.Enums;

namespace EstherLink.Backend.IntegrationTests;

public sealed class LicenseVerifyTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public LicenseVerifyTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Verify_ShouldReturnValidAndThenDeviceLimit_WhenMaxDevicesExceeded()
    {
        await _factory.ResetDatabaseAsync();
        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Licenses.Add(new LicenseEntity
            {
                Id = Guid.NewGuid(),
                LicenseKey = "TEST-LIC-001",
                Status = LicenseStatus.Active,
                Plan = "pro",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(10),
                MaxDevices = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        });

        var client = _factory.CreateClient();
        var firstRequest = new LicenseVerifyRequest
        {
            LicenseKey = "TEST-LIC-001",
            AppVersion = "1.2.3",
            Nonce = "nonce-a",
            Fingerprint = ParseJson("""{"machineGuid":"m1","nicMac":"aa-bb","osVersion":"win11"}""")
        };

        var firstResponse = await client.PostAsJsonAsync("/api/license/verify", firstRequest);
        firstResponse.EnsureSuccessStatusCode();
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<LicenseVerifyResponse>();

        Assert.NotNull(firstBody);
        Assert.True(firstBody.Valid);
        Assert.Equal("OK", firstBody.Reason);
        Assert.False(string.IsNullOrWhiteSpace(firstBody.Signature));

        var secondRequest = new LicenseVerifyRequest
        {
            LicenseKey = "TEST-LIC-001",
            AppVersion = "1.2.4",
            Nonce = "nonce-b",
            Fingerprint = ParseJson("""{"machineGuid":"m2","nicMac":"cc-dd","osVersion":"win11"}""")
        };

        var secondResponse = await client.PostAsJsonAsync("/api/license/verify", secondRequest);
        secondResponse.EnsureSuccessStatusCode();
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<LicenseVerifyResponse>();

        Assert.NotNull(secondBody);
        Assert.False(secondBody.Valid);
        Assert.Equal("DEVICE_LIMIT", secondBody.Reason);
        Assert.False(string.IsNullOrWhiteSpace(secondBody.Signature));
    }

    private static System.Text.Json.JsonElement ParseJson(string json)
    {
        return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
    }
}
