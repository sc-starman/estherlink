using System.Net.Http.Json;
using EstherLink.Backend.Contracts.App;
using EstherLink.Backend.Data.Entities;

namespace EstherLink.Backend.IntegrationTests;

public sealed class AppLatestTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public AppLatestTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Latest_ShouldReportUpdateAvailable_WhenCurrentOlderThanLatest()
    {
        await _factory.ResetDatabaseAsync();
        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.AppReleases.AddRange(
                new AppReleaseEntity
                {
                    Id = Guid.NewGuid(),
                    Channel = "stable",
                    Version = "1.2.3",
                    PublishedAt = DateTimeOffset.UtcNow.AddDays(-2),
                    Notes = "Older release",
                    DownloadUrl = "https://downloads.example.com/1.2.3.msi",
                    Sha256 = new string('a', 64),
                    MinSupportedVersion = "1.0.0"
                },
                new AppReleaseEntity
                {
                    Id = Guid.NewGuid(),
                    Channel = "stable",
                    Version = "1.3.0",
                    PublishedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    Notes = "Latest release",
                    DownloadUrl = "https://downloads.example.com/1.3.0.msi",
                    Sha256 = new string('b', 64),
                    MinSupportedVersion = "1.1.0"
                });
            await dbContext.SaveChangesAsync();
        });

        var client = _factory.CreateClient();
        var response = await client.GetFromJsonAsync<AppLatestResponse>("/api/app/latest?channel=stable&current=1.2.3");

        Assert.NotNull(response);
        Assert.True(response.UpdateAvailable);
        Assert.Equal("1.3.0", response.LatestVersion);
        Assert.Equal("1.1.0", response.MinSupportedVersion);
    }
}
