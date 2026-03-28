using System.Net;
using System.Security.Claims;
using System.Text.Json;
using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Data.Enums;
using OmniRelay.Backend.Models;
using OmniRelay.Backend.Services.Commerce;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.IntegrationTests;

public sealed class AdminPanelTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public AdminPanelTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminPages_Unauthenticated_ShouldRedirectToLogin()
    {
        await _factory.ResetDatabaseAsync();

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/admin/trials");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var redirect = response.Headers.Location!.ToString();
        Assert.Contains("/account/login", redirect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminPages_NonAdmin_ShouldReturnForbidden()
    {
        await using var authFactory = new AdminPanelTestWebApplicationFactory();
        await authFactory.InitializeAsync();
        await authFactory.ResetDatabaseAsync();
        var nonAdminUserId = Guid.NewGuid();

        await authFactory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Users.Add(new ApplicationUser
            {
                Id = nonAdminUserId,
                Email = "nonadmin@example.com",
                UserName = "nonadmin@example.com",
                EmailConfirmed = true,
                IsAdmin = false,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        });

        var client = authFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-UserId", nonAdminUserId.ToString());

        var response = await client.GetAsync("/admin/trials");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminPages_AdminUser_ShouldShowTrialPaidAndBillingSlices()
    {
        await using var authFactory = new AdminPanelTestWebApplicationFactory();
        await authFactory.InitializeAsync();
        await authFactory.ResetDatabaseAsync();

        var adminUserId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var trialLicenseId = Guid.NewGuid();
        var paidLicenseId = Guid.NewGuid();
        var trialUserLicenseId = Guid.NewGuid();
        var paidUserLicenseId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await authFactory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Users.AddRange(
                new ApplicationUser
                {
                    Id = adminUserId,
                    Email = "admin@example.com",
                    UserName = "admin@example.com",
                    EmailConfirmed = true,
                    IsAdmin = true,
                    CreatedAt = now
                },
                new ApplicationUser
                {
                    Id = customerId,
                    Email = "buyer@example.com",
                    UserName = "buyer@example.com",
                    EmailConfirmed = true,
                    IsAdmin = false,
                    CreatedAt = now
                });

            dbContext.Licenses.AddRange(
                new LicenseEntity
                {
                    Id = trialLicenseId,
                    LicenseKey = "OMNI-TRIAL-AAAA-BBBB-CCCC",
                    Status = LicenseStatus.Active,
                    Plan = "trial",
                    ExpiresAt = now.AddDays(2),
                    MaxDevices = 1,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new LicenseEntity
                {
                    Id = paidLicenseId,
                    LicenseKey = "OMNI-PAID-DDDD-EEEE-FFFF",
                    Status = LicenseStatus.Active,
                    Plan = "professional",
                    ExpiresAt = null,
                    MaxDevices = 1,
                    CreatedAt = now,
                    UpdatedAt = now
                });

            dbContext.UserLicenses.AddRange(
                new UserLicenseEntity
                {
                    Id = trialUserLicenseId,
                    UserId = customerId,
                    LicenseId = trialLicenseId,
                    Source = "trial",
                    CreatedAt = now,
                    UpdatesEntitledUntil = null
                },
                new UserLicenseEntity
                {
                    Id = paidUserLicenseId,
                    UserId = customerId,
                    LicenseId = paidLicenseId,
                    Source = "purchase",
                    CreatedAt = now.AddMinutes(1),
                    UpdatesEntitledUntil = now.AddMonths(12)
                });

            dbContext.CommerceOrders.Add(new CommerceOrderEntity
            {
                Id = orderId,
                UserId = customerId,
                OrderType = "license_purchase",
                FiatAmount = 149m,
                Currency = "USD",
                Status = "awaiting_payment",
                IssuedLicenseId = paidLicenseId,
                CreatedAt = now,
                UpdatedAt = now
            });

            dbContext.PayKryptIntents.Add(new PayKryptIntentEntity
            {
                Id = intentId,
                OrderId = orderId,
                PayKryptIntentId = "pi_admin_test_1",
                Status = "awaiting_payment",
                ExpiresAt = now.AddMinutes(45),
                RawJson = JsonSerializer.Serialize(new { message = "Awaiting payment" }),
                CreatedAt = now,
                UpdatedAt = now
            });

            await dbContext.SaveChangesAsync();
        });

        var client = authFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-UserId", adminUserId.ToString());

        var trialsResponse = await client.GetAsync("/admin/trials");
        var trialsHtml = await trialsResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, trialsResponse.StatusCode);
        Assert.Contains("OMNI-TRIAL-AAAA-BBBB-CCCC", trialsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("OMNI-PAID-DDDD-EEEE-FFFF", trialsHtml, StringComparison.Ordinal);

        var paidResponse = await client.GetAsync("/admin/paid-licenses");
        var paidHtml = await paidResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, paidResponse.StatusCode);
        Assert.Contains("OMNI-PAID-DDDD-EEEE-FFFF", paidHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("OMNI-TRIAL-AAAA-BBBB-CCCC", paidHtml, StringComparison.Ordinal);

        var billingResponse = await client.GetAsync("/admin/billing");
        var billingHtml = await billingResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, billingResponse.StatusCode);
        Assert.Contains(orderId.ToString(), billingHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Awaiting payment", billingHtml, StringComparison.Ordinal);
    }
}

internal sealed class AdminPanelTestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = $"OmniRelay-admin-tests-{Guid.NewGuid():N}";
    private readonly string _installerRoot = Path.Combine(Path.GetTempPath(), $"omnirelay-installers-admin-tests-{Guid.NewGuid():N}");
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
                ["EmailDelivery:Provider"] = "smtp",
                ["Smtp:Host"] = "smtp.test.local",
                ["Smtp:Port"] = "587",
                ["Smtp:FromEmail"] = "noreply@test.local",
                ["Smtp:RequireAuthentication"] = "false",
                ["Web:DocumentationUrl"] = "https://docs.example",
                ["Web:DownloadChannel"] = "stable",
                ["InstallerStorage:RootPath"] = _installerRoot,
                ["InstallerStorage:MaxUploadMb"] = "8"
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

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestHeaderAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestHeaderAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestHeaderAuthHandler.SchemeName;
                options.DefaultScheme = TestHeaderAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestHeaderAuthHandler>(
                TestHeaderAuthHandler.SchemeName,
                _ => { });
        });
    }

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
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

    public new async Task DisposeAsync()
    {
        await ResetDatabaseAsync();
        await base.DisposeAsync();
        if (Directory.Exists(_installerRoot))
        {
            Directory.Delete(_installerRoot, recursive: true);
        }
    }
}

internal sealed class TestHeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestHeaderAuth";

    public TestHeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-UserId", out var raw))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!Guid.TryParse(raw.ToString(), out var userId))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid test user id."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, userId.ToString())
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
