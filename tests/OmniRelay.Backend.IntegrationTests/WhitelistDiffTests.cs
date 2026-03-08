using System.Net.Http.Json;
using OmniRelay.Backend.Contracts.Whitelist;
using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Utilities;

namespace OmniRelay.Backend.IntegrationTests;

public sealed class WhitelistDiffTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public WhitelistDiffTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Diff_ShouldReturnAddedAndRemovedEntries()
    {
        await _factory.ResetDatabaseAsync();
        var setId = Guid.NewGuid();

        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            var v1 = new WhitelistSetEntity
            {
                Id = Guid.NewGuid(),
                SetGroupId = setId,
                CountryCode = "IR",
                Name = "IR Core",
                Category = "core",
                Version = 1,
                Sha256 = Sha256Util.HashHex("v1"),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            };

            var v2 = new WhitelistSetEntity
            {
                Id = Guid.NewGuid(),
                SetGroupId = setId,
                CountryCode = "IR",
                Name = "IR Core",
                Category = "core",
                Version = 2,
                Sha256 = Sha256Util.HashHex("v2"),
                CreatedAt = DateTimeOffset.UtcNow
            };

            dbContext.WhitelistSets.AddRange(v1, v2);
            dbContext.WhitelistEntries.AddRange(
                new WhitelistEntryEntity { Id = Guid.NewGuid(), WhitelistSetId = v1.Id, Cidr = "1.1.1.0/24" },
                new WhitelistEntryEntity { Id = Guid.NewGuid(), WhitelistSetId = v1.Id, Cidr = "2.2.2.0/24" },
                new WhitelistEntryEntity { Id = Guid.NewGuid(), WhitelistSetId = v2.Id, Cidr = "2.2.2.0/24" },
                new WhitelistEntryEntity { Id = Guid.NewGuid(), WhitelistSetId = v2.Id, Cidr = "3.3.3.0/24" });

            await dbContext.SaveChangesAsync();
        });

        var client = _factory.CreateClient();
        var response = await client.GetFromJsonAsync<WhitelistDiffResponse>($"/api/whitelist/{setId}/diff?fromVersion=1");

        Assert.NotNull(response);
        Assert.Equal(1, response.FromVersion);
        Assert.Equal(2, response.ToVersion);
        Assert.Contains("3.3.3.0/24", response.Added);
        Assert.Contains("1.1.1.0/24", response.Removed);
    }
}
