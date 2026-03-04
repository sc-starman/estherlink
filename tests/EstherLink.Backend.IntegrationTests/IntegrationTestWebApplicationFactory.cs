using EstherLink.Backend.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EstherLink.Backend.IntegrationTests;

public sealed class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = $"estherlink-backend-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=unused;Port=5432;Database=unused;Username=unused;Password=unused",
                ["ConnectionStrings:Redis"] = string.Empty,
                ["Database:ApplyMigrationsOnStartup"] = "false",
                ["Admin:ApiKeys:0"] = "dev-admin-key",
                ["Licensing:SigningSecret"] = "test-signing-secret",
                ["Licensing:OfflineCacheTtlHours"] = "24"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            services.RemoveAll(typeof(AppDbContext));

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
            });
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.LicenseActivations.RemoveRange(dbContext.LicenseActivations);
        dbContext.Licenses.RemoveRange(dbContext.Licenses);
        dbContext.WhitelistEntries.RemoveRange(dbContext.WhitelistEntries);
        dbContext.WhitelistSets.RemoveRange(dbContext.WhitelistSets);
        dbContext.AppReleases.RemoveRange(dbContext.AppReleases);
        await dbContext.SaveChangesAsync();
    }

    public async Task ExecuteDbContextAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(dbContext);
    }

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
    }

    public new async Task DisposeAsync()
    {
        await ResetDatabaseAsync();
        await base.DisposeAsync();
    }
}
