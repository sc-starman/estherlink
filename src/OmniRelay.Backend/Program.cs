using System.Security.Claims;
using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using OmniRelay.Backend.Configuration;
using OmniRelay.Backend.Contracts.App;
using OmniRelay.Backend.Contracts.Licensing;
using OmniRelay.Backend.Contracts.Whitelist;
using OmniRelay.Backend.Data;
using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Data.Enums;
using OmniRelay.Backend.Health;
using OmniRelay.Backend.Models;
using OmniRelay.Backend.Security;
using OmniRelay.Backend.Services;
using OmniRelay.Backend.Services.Commerce;
using OmniRelay.Backend.Services.Installers;
using OmniRelay.Backend.Swagger;
using OmniRelay.Backend.Utilities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using NuGet.Versioning;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

var supportedCultures = new[]
{
    new CultureInfo("en"),
    new CultureInfo("ru")
};

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<AdminSecurityOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.Configure<LicensingOptions>(builder.Configuration.GetSection("Licensing"));
builder.Services.Configure<PayKryptOptions>(builder.Configuration.GetSection("PayKrypt"));
builder.Services.PostConfigure<PayKryptOptions>(options =>
{
    options.BaseUrl = ParseStringOrDefault(
        Environment.GetEnvironmentVariable("PAYKRYPT_BASE_URL"),
        options.BaseUrl);
    options.SecretApiKey = ParseStringOrDefault(
        Environment.GetEnvironmentVariable("PAYKRYPT_SECRET_API_KEY"),
        options.SecretApiKey);

    var webhookSecret = Environment.GetEnvironmentVariable("PAYKRYPT_WEBHOOK_SECRET");
    if (webhookSecret is not null)
    {
        options.WebhookSecret = webhookSecret.Trim();
    }

    options.PriceUsd = ParseDecimalOrDefault(
        Environment.GetEnvironmentVariable("PAYKRYPT_PRICE_USD"),
        options.PriceUsd);
    options.OriginalPrice = ParseDecimalOrDefault(
        Environment.GetEnvironmentVariable("ORIGINAL_PRICE"),
        options.OriginalPrice);
    options.ExpiresInMinutes = ParseIntOrDefault(
        Environment.GetEnvironmentVariable("PAYKRYPT_EXPIRES_IN_MINUTES"),
        options.ExpiresInMinutes);
    options.AllowedChains = ParseStringListOrDefault(
        Environment.GetEnvironmentVariable("PAYKRYPT_ALLOWED_CHAINS"),
        options.AllowedChains);
    options.AllowedAssets = ParseStringListOrDefault(
        Environment.GetEnvironmentVariable("PAYKRYPT_ALLOWED_ASSETS"),
        options.AllowedAssets);
});
builder.Services.Configure<CommerceOptions>(builder.Configuration.GetSection("Commerce"));
builder.Services.Configure<WebOptions>(builder.Configuration.GetSection("Web"));
builder.Services.Configure<EmailDeliveryOptions>(builder.Configuration.GetSection("EmailDelivery"));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<MailServiceOptions>(builder.Configuration.GetSection("MailService"));
builder.Services.Configure<InstallerStorageOptions>(builder.Configuration.GetSection("InstallerStorage"));
builder.Services.AddOptions<SpamProtectionOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        var section = configuration.GetSection("SpamProtection");
        options.EnableRecaptcha = ParseBooleanOrDefault(section["EnableRecaptcha"], false);
        options.RecaptchaSiteKey = section["RecaptchaSiteKey"] ?? string.Empty;
        options.RecaptchaSecretKey = section["RecaptchaSecretKey"] ?? string.Empty;
        options.RecaptchaVerifyUrl = ParseStringOrDefault(
            section["RecaptchaVerifyUrl"],
            "https://www.google.com/recaptcha/api/siteverify");
        options.RecaptchaExpectedAction = ParseStringOrDefault(section["RecaptchaExpectedAction"], "contact_form");
        options.RecaptchaMinimumScore = ParseDoubleOrDefault(section["RecaptchaMinimumScore"], 0.5);
    });
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/app/data/dpkeys"))
    .SetApplicationName("OmniRelay.Backend");

var installerMaxUploadMb = builder.Configuration.GetValue<int?>("InstallerStorage:MaxUploadMb");
if (installerMaxUploadMb.HasValue && installerMaxUploadMb.Value > 0)
{
    var installerMaxUploadBytes = installerMaxUploadMb.Value * 1024L * 1024L;
    var multipartLimitBytes = installerMaxUploadBytes + (2L * 1024L * 1024L); // allow multipart overhead

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = multipartLimitBytes;
    });

    builder.Services.Configure<FormOptions>(options =>
    {
        options.MultipartBodyLengthLimit = multipartLimitBytes;
    });
}

var postgresConnection =
    builder.Configuration.GetConnectionString("Postgres") ??
    Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ??
    Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__POSTGRES") ??
    Environment.GetEnvironmentVariable("OmniRelay_DB_CONNECTION") ??
    throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(postgresConnection);
});

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = false;

        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = true;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "omnirelay.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = true;
    options.LoginPath = "/account/login";
    options.LogoutPath = "/account/logout";
    options.AccessDeniedPath = "/account/access-denied";
});

builder.Services.AddAuthorization(AdminAuthorizationPolicy.Configure);
builder.Services.AddScoped<IAuthorizationHandler, IsAdminAuthorizationHandler>();

builder.Services.AddHttpClient(nameof(PayKryptClient));
builder.Services.AddHttpClient<IRecaptchaVerifier, RecaptchaVerifier>();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders =
    [
        new QueryStringRequestCultureProvider
        {
            QueryStringKey = "lang",
            UIQueryStringKey = "lang"
        },
        new CookieRequestCultureProvider()
    ];
});
builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/App");
        options.Conventions.AuthorizeFolder("/Admin", AdminAuthorizationPolicy.Name);
        options.Conventions.AllowAnonymousToFolder("/Account");
        options.Conventions.AllowAnonymousToPage("/Index");
        options.Conventions.AllowAnonymousToPage("/Docs");
        options.Conventions.AllowAnonymousToPage("/Contact");
        options.Conventions.AllowAnonymousToPage("/Download");
        options.Conventions.AllowAnonymousToPage("/Changelogs");
    })
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();

builder.Services.AddScoped<LicenseService>();
builder.Services.AddScoped<WhitelistService>();
builder.Services.AddScoped<AppReleaseService>();
builder.Services.AddScoped<SampleDataSeeder>();
builder.Services.AddScoped<SigningKeyService>();
builder.Services.AddScoped<SecurityBootstrapper>();
builder.Services.AddScoped<LicenseResponseSigner>();
builder.Services.AddSingleton<LicenseCertificateSigner>();
builder.Services.AddScoped<IPayKryptClient, PayKryptClient>();
builder.Services.AddScoped<ICommerceService, CommerceService>();
builder.Services.AddHostedService<PendingPaymentReconcileWorker>();
builder.Services.AddScoped<ILicenseIssuanceService, LicenseIssuanceService>();
builder.Services.AddScoped<ITrialPolicyService, TrialPolicyService>();
builder.Services.AddScoped<IDownloadCatalogService, DownloadCatalogService>();
builder.Services.AddScoped<INewsletterContentProvider, NewsletterContentProvider>();
builder.Services.AddScoped<NewsletterService>();
builder.Services.AddScoped<INewsletterService, NewsletterService>();
builder.Services.AddHostedService<NewsletterDispatchWorker>();
builder.Services.AddScoped<SmtpEmailDeliveryService>();
builder.Services.AddHttpClient<MailServiceEmailDeliveryService>();
builder.Services.AddScoped<IEmailDeliveryService>(serviceProvider =>
{
    var provider = NormalizeEmailProvider(serviceProvider
        .GetRequiredService<IOptions<EmailDeliveryOptions>>()
        .Value
        .Provider);

    return provider switch
    {
        "smtp" => serviceProvider.GetRequiredService<SmtpEmailDeliveryService>(),
        "mail_service" => serviceProvider.GetRequiredService<MailServiceEmailDeliveryService>(),
        _ => throw new InvalidOperationException($"Unsupported email provider '{provider}'.")
    };
});
builder.Services.AddScoped<IContactEmailSender, SmtpContactEmailSender>();
builder.Services.AddSingleton<IInstallerStorageService, FileSystemInstallerStorageService>();
builder.Services.AddSingleton<IInstallerVersionResolver, WindowsInstallerVersionResolver>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddFixedWindowLimiter("public", limiter =>
    {
        limiter.PermitLimit = 120;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });

    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });

    options.AddFixedWindowLimiter("checkout", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

builder.Services.AddHealthChecks().AddCheck<DbReadyHealthCheck>("db_ready");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "OmniRelay Backend API",
        Version = "v1"
    });

    options.AddSecurityDefinition("AdminApiKey", new OpenApiSecurityScheme
    {
        Name = "X-ADMIN-API-KEY",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Admin API key for protected endpoints."
    });

    options.OperationFilter<AdminApiKeyOperationFilter>();
});

var app = builder.Build();

ValidateEmailDeliveryConfiguration(app.Services);

var localizationOptions = app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;

app.Use(async (context, next) =>
{
    if (context.Request.Query.TryGetValue("lang", out var langValues))
    {
        var requestedLanguage = langValues.ToString().Trim().ToLowerInvariant();
        if (requestedLanguage is "en" or "ru")
        {
            context.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(requestedLanguage)),
                new CookieOptions
                {
                    IsEssential = true,
                    Path = "/",
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    SameSite = SameSiteMode.Lax
                });
        }
    }

    await next();
});
app.UseRequestLocalization(localizationOptions);
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (builder.Configuration.GetValue("Database:ApplyMigrationsOnStartup", true))
    {
        await db.Database.MigrateAsync();
    }

    var bootstrapper = scope.ServiceProvider.GetRequiredService<SecurityBootstrapper>();
    await bootstrapper.EnsureInitializedAsync(CancellationToken.None);
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = async (context, _) =>
    {
        await context.Response.WriteAsJsonAsync(new
        {
            status = "live",
            utc = DateTimeOffset.UtcNow
        });
    }
});

app.MapGet("/metrics", async (AppDbContext dbContext, CancellationToken cancellationToken) =>
{
    var licenses = await dbContext.Licenses.CountAsync(cancellationToken);
    var activations = await dbContext.LicenseActivations.CountAsync(cancellationToken);
    var whitelistSets = await dbContext.WhitelistSets.CountAsync(cancellationToken);
    var releases = await dbContext.AppReleases.CountAsync(cancellationToken);

    var lines = new[]
    {
        "# TYPE OmniRelay_licenses_total gauge",
        $"OmniRelay_licenses_total {licenses}",
        "# TYPE OmniRelay_license_activations_total gauge",
        $"OmniRelay_license_activations_total {activations}",
        "# TYPE OmniRelay_whitelist_sets_total gauge",
        $"OmniRelay_whitelist_sets_total {whitelistSets}",
        "# TYPE OmniRelay_app_releases_total gauge",
        $"OmniRelay_app_releases_total {releases}"
    };

    return Results.Text(string.Join('\n', lines) + "\n", "text/plain; version=0.0.4");
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            utc = DateTimeOffset.UtcNow,
            checks = report.Entries.Select(x => new
            {
                name = x.Key,
                status = x.Value.Status.ToString(),
                description = x.Value.Description
            })
        };
        await context.Response.WriteAsJsonAsync(payload);
    }
});

app.MapGet("/download/windows", async (
        AppReleaseService appReleaseService,
        IInstallerStorageService installerStorageService,
        CancellationToken cancellationToken) =>
    {
        return await DownloadWindowsByChannelAsync("stable", appReleaseService, installerStorageService, cancellationToken);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/windows/beta", async (
        AppReleaseService appReleaseService,
        IInstallerStorageService installerStorageService,
        CancellationToken cancellationToken) =>
    {
        return await DownloadWindowsByChannelAsync("beta", appReleaseService, installerStorageService, cancellationToken);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/omni-gateway", (
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadOmniGatewayByChannel("stable", installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/omni-gateway/beta", (
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadOmniGatewayByChannel("beta", installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/connector-core/{os}/{arch}", (
        string os,
        string arch,
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadConnectorCoreByChannel("stable", os, arch, installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/omni-gateway/manifest", (
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadOmniGatewayManifestByChannel("stable", installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/omni-gateway/beta/manifest", (
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadOmniGatewayManifestByChannel("beta", installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/connector-core/{os}/{arch}/manifest", (
        string os,
        string arch,
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadConnectorCoreReleaseFileByChannel("stable", os, arch, "manifest", installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/connector-core/{os}/{arch}/signature", (
        string os,
        string arch,
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadConnectorCoreReleaseFileByChannel("stable", os, arch, "signature", installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/connector-core/beta/{os}/{arch}", (
        string os,
        string arch,
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadConnectorCoreByChannel("beta", os, arch, installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/connector-core/beta/{os}/{arch}/manifest", (
        string os,
        string arch,
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadConnectorCoreReleaseFileByChannel("beta", os, arch, "manifest", installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapGet("/download/connector-core/beta/{os}/{arch}/signature", (
        string os,
        string arch,
        IInstallerStorageService installerStorageService) =>
    {
        return DownloadConnectorCoreReleaseFileByChannel("beta", os, arch, "signature", installerStorageService);
    })
    .RequireRateLimiting("public");

app.MapMethods("/app", new[] { "GET", "HEAD" }, () =>
        Results.Redirect("/dashboard", permanent: true, preserveMethod: true))
    .AllowAnonymous();
app.MapMethods("/app/dashboard", new[] { "GET", "HEAD" }, () =>
        Results.Redirect("/dashboard", permanent: true, preserveMethod: true))
    .AllowAnonymous();
app.MapMethods("/app/licenses", new[] { "GET", "HEAD" }, () =>
        Results.Redirect("/dashboard/licenses", permanent: true, preserveMethod: true))
    .AllowAnonymous();
app.MapMethods("/app/billing", new[] { "GET", "HEAD" }, () =>
        Results.Redirect("/dashboard/billing", permanent: true, preserveMethod: true))
    .AllowAnonymous();

app.MapGet("/nl/{newsletterClientId:guid}", async (
    Guid newsletterClientId,
    AppDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    var client = await dbContext.NewsletterClients
        .FirstOrDefaultAsync(x => x.Id == newsletterClientId, cancellationToken);

    if (client is not null && client.VisitedAt is null)
    {
        client.VisitedAt = DateTimeOffset.UtcNow;
        if (client.State != NewsletterClientState.Failed)
        {
            client.State = NewsletterClientState.Visited;
        }

        var campaign = await dbContext.Newsletters.FirstOrDefaultAsync(x => x.Id == client.NewsletterId, cancellationToken);
        if (campaign is not null)
        {
            campaign.TotalVisited += 1;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    return Results.Redirect("/download");
})
.AllowAnonymous()
.RequireRateLimiting("public");

app.MapRazorPages();

var appApi = app.MapGroup("/app/api")
    .RequireAuthorization()
    .RequireRateLimiting("checkout");

appApi.MapPost("/trial/request", async (
    HttpContext httpContext,
    ICommerceService commerceService,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var email = httpContext.User.FindFirstValue(ClaimTypes.Email) ?? "unknown@example.com";
    var result = await commerceService.StartTrialAsync(userId.Value, email, cancellationToken);

    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(result);
});

appApi.MapPost("/checkout/quote", async (
    HttpContext httpContext,
    ICommerceService commerceService,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var request = await ReadCheckoutRequestAsync(httpContext, cancellationToken);
    var result = await commerceService.QuoteCheckoutAsync(userId.Value, request?.CouponCode, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

appApi.MapPost("/checkout/create-intent", async (
    HttpContext httpContext,
    ICommerceService commerceService,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var email = httpContext.User.FindFirstValue(ClaimTypes.Email) ?? "unknown@example.com";

    try
    {
        var request = await ReadCheckoutRequestAsync(httpContext, cancellationToken);
        var result = await commerceService.CreateCheckoutIntentAsync(userId.Value, email, request?.CouponCode, cancellationToken);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

appApi.MapGet("/checkout/{orderId:guid}/status", async (
    Guid orderId,
    bool refresh,
    HttpContext httpContext,
    ICommerceService commerceService,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var result = await commerceService.GetOrderStatusAsync(userId.Value, orderId, refresh, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/webhooks/paykrypt", async (
    HttpContext httpContext,
    ICommerceService commerceService,
    CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(httpContext.Request.Body);
    var payload = await reader.ReadToEndAsync(cancellationToken);

    if (string.IsNullOrWhiteSpace(payload))
    {
        return Results.BadRequest(new { message = "Payload is required." });
    }

    var eventId = httpContext.Request.Headers["X-PayKrypt-Event-Id"].FirstOrDefault();
    var result = await commerceService.ProcessWebhookAsync(payload, eventId, cancellationToken);
    return Results.Ok(result);
})
.RequireRateLimiting("public");

var api = app.MapGroup("/api");

var installerApi = api.MapGroup("/installer")
    .WithMetadata(new AdminEndpointMetadata())
    .AddEndpointFilter<AdminApiKeyEndpointFilter>()
    .AddEndpointFilter<AdminAuditEndpointFilter>();

installerApi.MapPost("/upload-windows", async (
        HttpRequest request,
        IInstallerStorageService installerStorageService,
        IInstallerVersionResolver installerVersionResolver,
        AppReleaseService appReleaseService,
        CancellationToken cancellationToken) =>
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { message = "Content-Type must be multipart/form-data." });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var installer = form.Files.GetFile("installer");
        if (installer is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["installer"] = ["installer (.msi) file is required."]
            });
        }

        if (installer.Length <= 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["installer"] = ["installer file must not be empty."]
            });
        }

        if (!string.Equals(Path.GetExtension(installer.FileName), ".msi", StringComparison.OrdinalIgnoreCase))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["installer"] = ["installer file extension must be .msi."]
            });
        }

        if (installer.Length > installerStorageService.MaxUploadBytes)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["installer"] =
                [
                    $"installer file exceeds max upload size of {installerStorageService.MaxUploadBytes / (1024L * 1024L)} MB."
                ]
            });
        }

        var channel = (form["channel"].FirstOrDefault() ?? "stable").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(channel))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["channel"] = ["channel is required."]
            });
        }

        var providedVersion = (form["version"].FirstOrDefault() ?? string.Empty).Trim();
        var resolvedVersion = providedVersion;
        var minSupportedVersion = (form["minSupportedVersion"].FirstOrDefault() ?? string.Empty).Trim();
        var notes = (form["notes"].FirstOrDefault() ?? string.Empty).Trim();
        var publishedAtRaw = (form["publishedAt"].FirstOrDefault() ?? string.Empty).Trim();

        DateTimeOffset? publishedAt = null;
        if (!string.IsNullOrWhiteSpace(publishedAtRaw))
        {
            if (!DateTimeOffset.TryParse(
                    publishedAtRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedPublishedAt))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["publishedAt"] = ["publishedAt must be a valid ISO-8601 datetime."]
                });
            }

            publishedAt = parsedPublishedAt;
        }

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"omnirelay-upload-{Guid.NewGuid():N}.msi");

        try
        {
            await using (var tempStream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous))
            {
                await installer.CopyToAsync(tempStream, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(resolvedVersion))
            {
                if (!installerVersionResolver.TryResolveWindowsMsiVersion(tempPath, out resolvedVersion, out var versionError))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["version"] = [versionError]
                    });
                }
            }

            if (!IsValidSemVer(resolvedVersion))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["version"] = ["version must be valid semver."]
                });
            }

            if (string.IsNullOrWhiteSpace(minSupportedVersion))
            {
                minSupportedVersion = resolvedVersion;
            }

            if (!IsValidSemVer(minSupportedVersion))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["minSupportedVersion"] = ["minSupportedVersion must be valid semver."]
                });
            }

            var saveResult = await installerStorageService.SaveWindowsInstallerAsync(
                tempPath,
                channel,
                resolvedVersion,
                cancellationToken);

            var release = await appReleaseService.UpsertReleaseAsync(
                channel: channel,
                version: resolvedVersion,
                minSupportedVersion: minSupportedVersion,
                downloadUrl: channel == "beta" ? "/download/windows/beta" : "/download/windows",
                sha256: saveResult.Sha256,
                notes: notes,
                publishedAt: publishedAt,
                cancellationToken: cancellationToken);

            return Results.Ok(new
            {
                message = "Windows installer uploaded successfully.",
                channel = release.Channel,
                version = release.Version,
                minSupportedVersion = release.MinSupportedVersion,
                sha256 = release.Sha256,
                downloadUrl = release.DownloadUrl,
                publishedAt = release.PublishedAt,
                fileSizeBytes = saveResult.FileSizeBytes
            });
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    });

installerApi.MapPost("/upload-omni-gateway", async (
        HttpRequest request,
        IInstallerStorageService installerStorageService,
        CancellationToken cancellationToken) =>
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { message = "Content-Type must be multipart/form-data." });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var artifact = form.Files.GetFile("artifact");
        if (artifact is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["artifact"] = ["artifact (.tar.gz) file is required."]
            });
        }

        if (artifact.Length <= 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["artifact"] = ["artifact file must not be empty."]
            });
        }

        if (!artifact.FileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["artifact"] = ["artifact file name must end with .tar.gz."]
            });
        }

        if (artifact.Length > installerStorageService.MaxUploadBytes)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["artifact"] =
                [
                    $"artifact file exceeds max upload size of {installerStorageService.MaxUploadBytes / (1024L * 1024L)} MB."
                ]
            });
        }

        var channel = NormalizeReleaseChannel(form["channel"].FirstOrDefault());
        if (channel is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["channel"] = ["channel must be stable or beta."]
            });
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"omnirelay-omni-gateway-{channel}-{Guid.NewGuid():N}.tar.gz");
        try
        {
            await using (var tempStream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous))
            {
                await artifact.CopyToAsync(tempStream, cancellationToken);
            }

            if (!IsGzipFile(tempPath))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["artifact"] = ["artifact must be a valid gzip archive."]
                });
            }

            var saveResult = await installerStorageService.SaveOmniGatewayArtifactAsync(tempPath, channel, cancellationToken);
            var downloadUrl = channel == "beta"
                ? "/download/omni-gateway/beta"
                : "/download/omni-gateway";
            return Results.Ok(new
            {
                message = "Gateway panel artifact uploaded successfully.",
                channel,
                sha256 = saveResult.Sha256,
                fileSizeBytes = saveResult.FileSizeBytes,
                downloadUrl
            });
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    });

installerApi.MapPost("/upload-connector-core", async (
        HttpRequest request,
        IInstallerStorageService installerStorageService,
        CancellationToken cancellationToken) =>
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { message = "Content-Type must be multipart/form-data." });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var artifact = form.Files.GetFile("artifact");
        var manifest = form.Files.GetFile("manifest");
        var signature = form.Files.GetFile("signature");
        if (artifact is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["artifact"] = ["artifact (.tar.gz) file is required."]
            });
        }

        if (artifact.Length <= 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["artifact"] = ["artifact file must not be empty."]
            });
        }

        if (manifest is null || manifest.Length <= 0 || manifest.Length > 64 * 1024)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["manifest"] = ["A non-empty manifest.json file up to 64 KiB is required."]
            });
        }

        if (signature is null || signature.Length <= 0 || signature.Length > 64 * 1024)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["signature"] = ["A non-empty detached manifest signature up to 64 KiB is required."]
            });
        }

        if (!artifact.FileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["artifact"] = ["artifact file name must end with .tar.gz."]
            });
        }

        if (artifact.Length > installerStorageService.MaxUploadBytes)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["artifact"] =
                [
                    $"artifact file exceeds max upload size of {installerStorageService.MaxUploadBytes / (1024L * 1024L)} MB."
                ]
            });
        }

        var channel = NormalizeReleaseChannel(form["channel"].FirstOrDefault());
        if (channel is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["channel"] = ["channel must be stable or beta."]
            });
        }

        var os = (form["os"].FirstOrDefault() ?? "linux").Trim().ToLowerInvariant();
        if (os != "linux")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["os"] = ["os must be linux."]
            });
        }

        var arch = (form["arch"].FirstOrDefault() ?? "amd64").Trim().ToLowerInvariant();
        if (arch is not ("amd64" or "arm64"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["arch"] = ["arch must be amd64 or arm64."]
            });
        }

        var tempPrefix = Path.Combine(Path.GetTempPath(), $"omnirelay-connector-core-{channel}-{os}-{arch}-{Guid.NewGuid():N}");
        var tempPath = $"{tempPrefix}.tar.gz";
        var tempManifestPath = $"{tempPrefix}.manifest.json";
        var tempSignaturePath = $"{tempPrefix}.manifest.sig";
        try
        {
            await using (var tempStream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous))
            {
                await artifact.CopyToAsync(tempStream, cancellationToken);
            }

            await using (var tempStream = new FileStream(
                             tempManifestPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await manifest.CopyToAsync(tempStream, cancellationToken);
            }

            await using (var tempStream = new FileStream(
                             tempSignaturePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await signature.CopyToAsync(tempStream, cancellationToken);
            }

            if (!IsGzipFile(tempPath))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["artifact"] = ["artifact must be a valid gzip archive."]
                });
            }

            ConnectorCoreReleaseManifest? releaseManifest;
            try
            {
                await using var manifestStream = File.OpenRead(tempManifestPath);
                releaseManifest = await JsonSerializer.DeserializeAsync<ConnectorCoreReleaseManifest>(
                    manifestStream,
                    cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                releaseManifest = null;
            }

            await using var artifactStream = File.OpenRead(tempPath);
            var artifactSha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(
                artifactStream,
                cancellationToken)).ToLowerInvariant();
            var artifactSize = new FileInfo(tempPath).Length;
            var expectedArtifactName = installerStorageService.GetConnectorCoreDownloadFileName(os, arch);
            var manifestError = ValidateConnectorCoreReleaseManifest(
                releaseManifest,
                channel,
                os,
                arch,
                expectedArtifactName,
                artifactSha256,
                artifactSize);
            if (manifestError is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["manifest"] = [manifestError]
                });
            }

            var saveResult = await installerStorageService.SaveConnectorCoreReleaseAsync(
                tempPath,
                tempManifestPath,
                tempSignaturePath,
                channel,
                os,
                arch,
                cancellationToken);
            var downloadUrl = channel == "beta"
                ? $"/download/connector-core/beta/{os}/{arch}"
                : $"/download/connector-core/{os}/{arch}";
            return Results.Ok(new
            {
                channel,
                message = "Signed connector-core release uploaded successfully.",
                os,
                arch,
                version = releaseManifest!.Version,
                sha256 = saveResult.Artifact.Sha256,
                fileSizeBytes = saveResult.Artifact.FileSizeBytes,
                downloadUrl,
                manifestUrl = $"{downloadUrl}/manifest",
                signatureUrl = $"{downloadUrl}/signature"
            });
        }
        finally
        {
            foreach (var path in new[] { tempPath, tempManifestPath, tempSignaturePath })
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch
                    {
                        // Best-effort temp cleanup.
                    }
                }
            }
        }
    });

api.MapPost("/license/verify", async (
        LicenseVerifyRequest request,
        HttpContext httpContext,
        LicenseService licenseService,
        CancellationToken cancellationToken) =>
    {
        var errors = ValidationHelpers.ValidateLicenseVerify(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var response = await licenseService.VerifyAsync(request, httpContext.TraceIdentifier, cancellationToken);
        return Results.Ok(response);
    })
    .RequireRateLimiting("public")
    ;

api.MapGet("/license/public-keys", async (
        SigningKeyService signingKeyService,
        CancellationToken cancellationToken) =>
    {
        var keys = await signingKeyService.GetPublicKeysAsync(cancellationToken);
        return Results.Ok(new LicensePublicKeysResponse
        {
            ServerTime = DateTimeOffset.UtcNow,
            Keys = keys
        });
    })
    .RequireRateLimiting("public")
    ;

api.MapGet("/whitelist/sets", async (
        string? country,
        string? category,
        WhitelistService whitelistService,
        CancellationToken cancellationToken) =>
    {
        var response = await whitelistService.GetLatestSummariesAsync(country, category, cancellationToken);
        return Results.Ok(response);
    })
    .RequireRateLimiting("public")
    ;

api.MapGet("/whitelist/{setId:guid}/latest", async (
        Guid setId,
        WhitelistService whitelistService,
        CancellationToken cancellationToken) =>
    {
        var response = await whitelistService.GetLatestAsync(setId, cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    })
    .RequireRateLimiting("public")
    ;

api.MapGet("/whitelist/{setId:guid}/diff", async (
        Guid setId,
        int fromVersion,
        WhitelistService whitelistService,
        CancellationToken cancellationToken) =>
    {
        if (fromVersion <= 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["fromVersion"] = ["fromVersion must be > 0."]
            });
        }

        var response = await whitelistService.GetDiffAsync(setId, fromVersion, cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    })
    .RequireRateLimiting("public")
    ;

api.MapGet("/app/latest", async (
        string channel,
        string? current,
        AppReleaseService releaseService,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            channel = "stable";
        }

        var response = await releaseService.GetLatestAsync(channel, current, cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    })
    .RequireRateLimiting("public")
    ;

var admin = api.MapGroup("/admin")
    .WithMetadata(new AdminEndpointMetadata())
    .AddEndpointFilter<AdminApiKeyEndpointFilter>()
    .AddEndpointFilter<AdminAuditEndpointFilter>();

admin.MapPost("/licenses", async (
        AdminCreateLicenseRequest request,
        AppDbContext dbContext,
        CancellationToken cancellationToken) =>
    {
        var errors = ValidationHelpers.ValidateCreateLicense(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!Enum.TryParse<LicenseStatus>(request.Status, ignoreCase: true, out var status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Status)] = ["Status must be one of: active, suspended, revoked."]
            });
        }

        var normalizedKey = request.LicenseKey.Trim();
        var exists = await dbContext.Licenses.AnyAsync(x => x.LicenseKey == normalizedKey, cancellationToken);
        if (exists)
        {
            return Results.Conflict(new { message = "License key already exists." });
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new LicenseEntity
        {
            Id = Guid.NewGuid(),
            LicenseKey = normalizedKey,
            Status = status,
            Plan = request.Plan.Trim(),
            ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
            MaxDevices = request.MaxDevices,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.Licenses.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/admin/licenses/{entity.Id}", ToAdminResponse(entity, 0));
    })
    ;

admin.MapPost("/licenses/{id:guid}/revoke", async (
        Guid id,
        AppDbContext dbContext,
        CancellationToken cancellationToken) =>
    {
        var license = await dbContext.Licenses.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (license is null)
        {
            return Results.NotFound();
        }

        license.Status = LicenseStatus.Revoked;
        license.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToAdminResponse(license, 0));
    })
    ;

admin.MapGet("/licenses/{id:guid}", async (
        Guid id,
        AppDbContext dbContext,
        CancellationToken cancellationToken) =>
    {
        var license = await dbContext.Licenses
            .Include(x => x.Activations)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        return license is null
            ? Results.NotFound()
            : Results.Ok(ToAdminResponse(license, license.Activations.Count));
    })
    ;

admin.MapPost("/whitelist/sets", async (
        AdminCreateWhitelistSetRequest request,
        WhitelistService whitelistService,
        CancellationToken cancellationToken) =>
    {
        var errors = ValidationHelpers.ValidateWhitelistCreate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        try
        {
            var response = await whitelistService.CreateSetAsync(request, cancellationToken);
            return Results.Created($"/api/whitelist/{response.SetId}/latest", response);
        }
        catch (InvalidOperationException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Entries)] = [ex.Message]
            });
        }
    })
    ;

admin.MapPost("/whitelist/{setId:guid}/publish", async (
        Guid setId,
        AdminPublishWhitelistRequest request,
        WhitelistService whitelistService,
        CancellationToken cancellationToken) =>
    {
        var errors = ValidationHelpers.ValidateWhitelistPublish(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        try
        {
            var response = await whitelistService.PublishAsync(setId, request, cancellationToken);
            return response is null ? Results.NotFound() : Results.Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Entries)] = [ex.Message]
            });
        }
    })
    ;

admin.MapPost("/app/releases", async (
        AdminCreateReleaseRequest request,
        AppReleaseService appReleaseService,
        CancellationToken cancellationToken) =>
    {
        var errors = ValidationHelpers.ValidateCreateRelease(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        try
        {
            var release = await appReleaseService.CreateReleaseAsync(request, cancellationToken);
            return Results.Created($"/api/app/latest?channel={release.Channel}", release);
        }
        catch (InvalidOperationException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Version)] = [ex.Message]
            });
        }
    })
    ;

admin.MapPost("/seed/sample", async (
        SampleDataSeeder seeder,
        CancellationToken cancellationToken) =>
    {
        var result = await seeder.SeedAsync(cancellationToken);
        return Results.Ok(new
        {
            message = "Sample dataset seeded.",
            result.Created,
            result.Skipped
        });
    })
    ;

app.Run();

static async Task<IResult> DownloadWindowsByChannelAsync(
    string channel,
    AppReleaseService appReleaseService,
    IInstallerStorageService installerStorageService,
    CancellationToken cancellationToken)
{
    var normalizedChannel = NormalizeReleaseChannel(channel) ?? "stable";
    var latest = await appReleaseService.GetLatestAsync(normalizedChannel, null, cancellationToken);
    if (latest is null)
    {
        return Results.NotFound(new { message = $"No {normalizedChannel} Windows installer release is available yet." });
    }

    var installerPath = installerStorageService.GetWindowsInstallerPath(normalizedChannel, latest.LatestVersion);
    if (!File.Exists(installerPath))
    {
        return Results.NotFound(new
        {
            message = $"Installer artifact for latest {normalizedChannel} release was not found in storage.",
            version = latest.LatestVersion
        });
    }

    var downloadFileName = installerStorageService.GetWindowsDownloadFileName(latest.LatestVersion);
    return Results.File(installerPath, "application/x-msi", downloadFileName);
}

static IResult DownloadOmniGatewayByChannel(string channel, IInstallerStorageService installerStorageService)
{
    var normalizedChannel = NormalizeReleaseChannel(channel) ?? "stable";
    var artifactPath = installerStorageService.GetOmniGatewayArtifactPath(normalizedChannel);
    var manifestPath = installerStorageService.GetOmniGatewayManifestPath(normalizedChannel);
    if (!File.Exists(artifactPath) || !File.Exists(manifestPath))
    {
        return Results.NotFound(new { message = $"Gateway panel artifact for channel '{normalizedChannel}' is not available yet." });
    }

    return Results.File(
        artifactPath,
        "application/gzip",
        installerStorageService.GetOmniGatewayDownloadFileName());
}

static IResult DownloadOmniGatewayManifestByChannel(string channel, IInstallerStorageService installerStorageService)
{
    var normalizedChannel = NormalizeReleaseChannel(channel) ?? "stable";
    var artifactPath = installerStorageService.GetOmniGatewayArtifactPath(normalizedChannel);
    var manifestPath = installerStorageService.GetOmniGatewayManifestPath(normalizedChannel);
    if (!File.Exists(artifactPath) || !File.Exists(manifestPath))
    {
        return Results.NotFound(new { message = $"Gateway panel manifest for channel '{normalizedChannel}' is not available yet." });
    }

    return Results.File(
        manifestPath,
        "application/json",
        installerStorageService.GetOmniGatewayManifestDownloadFileName());
}

static IResult DownloadConnectorCoreByChannel(
    string channel,
    string os,
    string arch,
    IInstallerStorageService installerStorageService)
{
    var normalizedChannel = NormalizeReleaseChannel(channel) ?? "stable";
    var normalizedOs = (os ?? string.Empty).Trim().ToLowerInvariant();
    var normalizedArch = (arch ?? string.Empty).Trim().ToLowerInvariant();

    if (normalizedOs != "linux")
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["os"] = ["os must be linux."]
        });
    }

    if (normalizedArch is not ("amd64" or "arm64"))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["arch"] = ["arch must be amd64 or arm64."]
        });
    }

    var artifactPath = installerStorageService.GetConnectorCoreArtifactPath(normalizedChannel, normalizedOs, normalizedArch);
    var manifestPath = installerStorageService.GetConnectorCoreManifestPath(normalizedChannel, normalizedOs, normalizedArch);
    var signaturePath = installerStorageService.GetConnectorCoreSignaturePath(normalizedChannel, normalizedOs, normalizedArch);
    if (!File.Exists(artifactPath) || !File.Exists(manifestPath) || !File.Exists(signaturePath))
    {
        return Results.NotFound(new
        {
            message = $"Connector-core artifact for channel '{normalizedChannel}' is not available yet.",
            channel = normalizedChannel,
            os = normalizedOs,
            arch = normalizedArch
        });
    }

    return Results.File(
        artifactPath,
        "application/gzip",
        installerStorageService.GetConnectorCoreDownloadFileName(normalizedOs, normalizedArch));
}

static IResult DownloadConnectorCoreReleaseFileByChannel(
    string channel,
    string os,
    string arch,
    string kind,
    IInstallerStorageService installerStorageService)
{
    var normalizedChannel = NormalizeReleaseChannel(channel) ?? "stable";
    var normalizedOs = (os ?? string.Empty).Trim().ToLowerInvariant();
    var normalizedArch = (arch ?? string.Empty).Trim().ToLowerInvariant();
    if (normalizedOs != "linux" || normalizedArch is not ("amd64" or "arm64"))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["platform"] = ["Only linux amd64 and linux arm64 connector-core releases are available."]
        });
    }

    var path = kind == "manifest"
        ? installerStorageService.GetConnectorCoreManifestPath(normalizedChannel, normalizedOs, normalizedArch)
        : installerStorageService.GetConnectorCoreSignaturePath(normalizedChannel, normalizedOs, normalizedArch);
    if (!File.Exists(path))
    {
        return Results.NotFound(new
        {
            message = $"Connector-core {kind} for channel '{normalizedChannel}' is not available yet.",
            channel = normalizedChannel,
            os = normalizedOs,
            arch = normalizedArch
        });
    }

    return kind == "manifest"
        ? Results.File(path, "application/json", installerStorageService.GetConnectorCoreManifestDownloadFileName(normalizedOs, normalizedArch))
        : Results.File(path, "application/octet-stream", installerStorageService.GetConnectorCoreSignatureDownloadFileName(normalizedOs, normalizedArch));
}

static string? ValidateConnectorCoreReleaseManifest(
    ConnectorCoreReleaseManifest? manifest,
    string channel,
    string os,
    string arch,
    string artifactName,
    string artifactSha256,
    long artifactSize)
{
    if (manifest is null)
    {
        return "manifest must be valid JSON.";
    }

    if (manifest.SchemaVersion != 1 ||
        string.IsNullOrWhiteSpace(manifest.Version) ||
        manifest.Channel != channel ||
        manifest.Os != os ||
        manifest.Arch != arch ||
        manifest.Artifact != artifactName ||
        !string.Equals(manifest.Sha256, artifactSha256, StringComparison.OrdinalIgnoreCase) ||
        manifest.SizeBytes != artifactSize ||
        manifest.PublishedAtUtc == default)
    {
        return "manifest metadata, artifact SHA256, or artifact size does not match the uploaded release.";
    }

    return null;
}

static AdminLicenseResponse ToAdminResponse(LicenseEntity entity, int activationCount)
{
    return new AdminLicenseResponse
    {
        Id = entity.Id,
        LicenseKey = entity.LicenseKey,
        Status = entity.Status.ToString().ToLowerInvariant(),
        Plan = entity.Plan,
        ExpiresAt = entity.ExpiresAt,
        MaxDevices = entity.MaxDevices,
        ActivationCount = activationCount,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}

static Guid? GetUserId(ClaimsPrincipal principal)
{
    var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    return Guid.TryParse(value, out var parsed) ? parsed : null;
}

static async Task<CheckoutRequest?> ReadCheckoutRequestAsync(HttpContext httpContext, CancellationToken cancellationToken)
{
    if ((httpContext.Request.ContentLength ?? 0) <= 0)
    {
        return null;
    }

    try
    {
        return await httpContext.Request.ReadFromJsonAsync<CheckoutRequest>(cancellationToken: cancellationToken);
    }
    catch
    {
        return null;
    }
}

static bool IsValidSemVer(string value)
{
    return !string.IsNullOrWhiteSpace(value) && NuGetVersion.TryParse(value.Trim(), out _);
}

static string? NormalizeReleaseChannel(string? channel)
{
    var normalized = (channel ?? "stable").Trim().ToLowerInvariant();
    return normalized is "stable" or "beta" ? normalized : null;
}

static bool IsGzipFile(string filePath)
{
    try
    {
        using var stream = File.OpenRead(filePath);
        if (stream.Length < 2)
        {
            return false;
        }

        var first = stream.ReadByte();
        var second = stream.ReadByte();
        return first == 0x1F && second == 0x8B;
    }
    catch
    {
        return false;
    }
}

static bool ParseBooleanOrDefault(string? value, bool defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
}

static double ParseDoubleOrDefault(string? value, double defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : defaultValue;
}

static int ParseIntOrDefault(string? value, int defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : defaultValue;
}

static decimal ParseDecimalOrDefault(string? value, decimal defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : defaultValue;
}

static List<string> ParseStringListOrDefault(string? value, IEnumerable<string>? defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return NormalizeStringList(defaultValue);
    }

    var trimmed = value.Trim();
    List<string>? parsed = null;

    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
    {
        try
        {
            parsed = JsonSerializer.Deserialize<List<string>>(trimmed);
        }
        catch
        {
            parsed = null;
        }
    }

    parsed ??= trimmed
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Trim())
        .ToList();

    return parsed.Count == 0 ? NormalizeStringList(defaultValue) : NormalizeStringList(parsed);
}

static List<string> NormalizeStringList(IEnumerable<string>? values)
{
    return values?
        .Select(item => item?.Trim())
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Select(item => item!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList() ?? [];
}

static string ParseStringOrDefault(string? value, string defaultValue)
{
    return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
}

static string NormalizeEmailProvider(string? value)
{
    var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
    return string.IsNullOrWhiteSpace(normalized) ? "smtp" : normalized;
}

static void ValidateEmailDeliveryConfiguration(IServiceProvider serviceProvider)
{
    var provider = NormalizeEmailProvider(ServiceProviderServiceExtensions
        .GetRequiredService<IOptions<EmailDeliveryOptions>>(serviceProvider)
        .Value
        .Provider);

    if (provider == "smtp")
    {
        var smtp = ServiceProviderServiceExtensions.GetRequiredService<IOptions<SmtpOptions>>(serviceProvider).Value;
        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            throw new InvalidOperationException("EmailDelivery provider 'smtp' requires Smtp:Host.");
        }

        if (smtp.Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("EmailDelivery provider 'smtp' requires a valid Smtp:Port.");
        }

        if (string.IsNullOrWhiteSpace(smtp.FromEmail))
        {
            throw new InvalidOperationException("EmailDelivery provider 'smtp' requires Smtp:FromEmail.");
        }

        if (smtp.RequireAuthentication &&
            (string.IsNullOrWhiteSpace(smtp.Username) || string.IsNullOrWhiteSpace(smtp.Password)))
        {
            throw new InvalidOperationException("EmailDelivery provider 'smtp' requires Smtp:Username and Smtp:Password when authentication is enabled.");
        }

        return;
    }

    if (provider == "mail_service")
    {
        var mailService = ServiceProviderServiceExtensions.GetRequiredService<IOptions<MailServiceOptions>>(serviceProvider).Value;

        if (string.IsNullOrWhiteSpace(mailService.BaseUrl))
        {
            throw new InvalidOperationException("EmailDelivery provider 'mail_service' requires MailService:BaseUrl.");
        }

        if (!Uri.TryCreate(mailService.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("MailService:BaseUrl must be a valid absolute http/https URL.");
        }

        if (string.IsNullOrWhiteSpace(mailService.SendPath))
        {
            throw new InvalidOperationException("EmailDelivery provider 'mail_service' requires MailService:SendPath.");
        }

        if (string.IsNullOrWhiteSpace(mailService.ApiKeyHeader))
        {
            throw new InvalidOperationException("EmailDelivery provider 'mail_service' requires MailService:ApiKeyHeader.");
        }

        if (string.IsNullOrWhiteSpace(mailService.ApiKey))
        {
            throw new InvalidOperationException("EmailDelivery provider 'mail_service' requires MailService:ApiKey.");
        }

        if (mailService.TimeoutSeconds is < 5 or > 300)
        {
            throw new InvalidOperationException("MailService:TimeoutSeconds must be between 5 and 300.");
        }

        if (mailService.RetryCount is < 0 or > 4)
        {
            throw new InvalidOperationException("MailService:RetryCount must be between 0 and 4.");
        }

        return;
    }

    throw new InvalidOperationException("EmailDelivery:Provider must be one of: smtp, mail_service.");
}

public sealed record CheckoutRequest(string? CouponCode);

public partial class Program;
