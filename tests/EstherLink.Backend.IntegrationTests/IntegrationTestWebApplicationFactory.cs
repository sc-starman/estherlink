using EstherLink.Backend.Data;
using EstherLink.Backend.Services.Commerce;
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
    private readonly TestPayKryptClient _testPayKryptClient = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=unused;Port=5432;Database=unused;Username=unused;Password=unused",
                ["Database:ApplyMigrationsOnStartup"] = "false",
                ["Admin:ApiKeys:0"] = "dev-admin-key",
                ["Admin:ApiKeyPepper"] = "test-admin-pepper",
                ["Licensing:SigningKeyRotationDays"] = "90",
                ["Licensing:OfflineCacheTtlHours"] = "24",
                ["PayKrypt:BaseUrl"] = "https://api-sandbox.paykrypt.io",
                ["PayKrypt:SecretApiKey"] = "sk_test_abc",
                ["PayKrypt:PriceUsd"] = "149",
                ["Commerce:PaidLicensePlan"] = "professional",
                ["Commerce:PaidMaxDevices"] = "3",
                ["Commerce:TrialDays"] = "2",
                ["Commerce:UpdateEntitlementMonths"] = "12",
                ["Web:DocumentationUrl"] = "https://docs.example",
                ["Web:DownloadChannel"] = "stable"
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

            services.RemoveAll(typeof(IPayKryptClient));
            services.AddSingleton<IPayKryptClient>(_testPayKryptClient);
        });
    }

    public TestPayKryptClient PayKryptClient => _testPayKryptClient;

    public async Task ResetDatabaseAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.LicenseActivations.RemoveRange(dbContext.LicenseActivations);
        dbContext.Licenses.RemoveRange(dbContext.Licenses);
        dbContext.WhitelistEntries.RemoveRange(dbContext.WhitelistEntries);
        dbContext.WhitelistSets.RemoveRange(dbContext.WhitelistSets);
        dbContext.AppReleases.RemoveRange(dbContext.AppReleases);
        dbContext.AuditEvents.RemoveRange(dbContext.AuditEvents);
        dbContext.PayKryptIntents.RemoveRange(dbContext.PayKryptIntents);
        dbContext.CommerceOrders.RemoveRange(dbContext.CommerceOrders);
        dbContext.UserLicenses.RemoveRange(dbContext.UserLicenses);
        dbContext.PayKryptWebhookEvents.RemoveRange(dbContext.PayKryptWebhookEvents);
        dbContext.Users.RemoveRange(dbContext.Users);
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

public sealed class TestPayKryptClient : IPayKryptClient
{
    public Dictionary<string, PayKryptIntentResponse> Intents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<PayKryptIntentResponse> CreatePaymentIntentAsync(PayKryptCreateIntentRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        var intent = new PayKryptIntentResponse
        {
            Id = $"pi_{Guid.NewGuid():N}",
            Status = "awaiting_payment",
            Amount = request.Amount,
            Currency = request.Currency,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(45),
            DepositAddresses = []
        };

        Intents[intent.Id] = intent;
        return Task.FromResult(intent);
    }

    public Task<PayKryptIntentResponse?> GetPaymentIntentAsync(string intentId, CancellationToken cancellationToken)
    {
        Intents.TryGetValue(intentId, out var intent);
        return Task.FromResult(intent);
    }
}
