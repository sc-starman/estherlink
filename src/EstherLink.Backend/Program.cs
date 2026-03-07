using System.Security.Claims;
using System.Globalization;
using System.Threading.RateLimiting;
using EstherLink.Backend.Configuration;
using EstherLink.Backend.Contracts.App;
using EstherLink.Backend.Contracts.Licensing;
using EstherLink.Backend.Contracts.Whitelist;
using EstherLink.Backend.Data;
using EstherLink.Backend.Data.Entities;
using EstherLink.Backend.Data.Enums;
using EstherLink.Backend.Health;
using EstherLink.Backend.Models;
using EstherLink.Backend.Security;
using EstherLink.Backend.Services;
using EstherLink.Backend.Services.Commerce;
using EstherLink.Backend.Services.Installers;
using EstherLink.Backend.Swagger;
using EstherLink.Backend.Utilities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using NuGet.Versioning;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.Configure<AdminSecurityOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.Configure<LicensingOptions>(builder.Configuration.GetSection("Licensing"));
builder.Services.Configure<PayKryptOptions>(builder.Configuration.GetSection("PayKrypt"));
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
    Environment.GetEnvironmentVariable("ESTHERLINK_DB_CONNECTION") ??
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
    options.AccessDeniedPath = "/account/login";
});

builder.Services.AddHttpClient(nameof(PayKryptClient));
builder.Services.AddHttpClient<IRecaptchaVerifier, RecaptchaVerifier>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/App");
    options.Conventions.AllowAnonymousToFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Docs");
    options.Conventions.AllowAnonymousToPage("/Contact");
    options.Conventions.AllowAnonymousToPage("/Download");
});

builder.Services.AddScoped<LicenseService>();
builder.Services.AddScoped<WhitelistService>();
builder.Services.AddScoped<AppReleaseService>();
builder.Services.AddScoped<SampleDataSeeder>();
builder.Services.AddScoped<SigningKeyService>();
builder.Services.AddScoped<SecurityBootstrapper>();
builder.Services.AddScoped<LicenseResponseSigner>();
builder.Services.AddScoped<IPayKryptClient, PayKryptClient>();
builder.Services.AddScoped<ICommerceService, CommerceService>();
builder.Services.AddScoped<ILicenseIssuanceService, LicenseIssuanceService>();
builder.Services.AddScoped<ITrialPolicyService, TrialPolicyService>();
builder.Services.AddScoped<IDownloadCatalogService, DownloadCatalogService>();
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
        Title = "EstherLink Backend API",
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
        "# TYPE estherlink_licenses_total gauge",
        $"estherlink_licenses_total {licenses}",
        "# TYPE estherlink_license_activations_total gauge",
        $"estherlink_license_activations_total {activations}",
        "# TYPE estherlink_whitelist_sets_total gauge",
        $"estherlink_whitelist_sets_total {whitelistSets}",
        "# TYPE estherlink_app_releases_total gauge",
        $"estherlink_app_releases_total {releases}"
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
        var latest = await appReleaseService.GetLatestAsync("stable", null, cancellationToken);
        if (latest is null)
        {
            return Results.NotFound(new { message = "No stable Windows installer release is available yet." });
        }

        var installerPath = installerStorageService.GetWindowsInstallerPath("stable", latest.LatestVersion);
        if (!File.Exists(installerPath))
        {
            return Results.NotFound(new
            {
                message = "Installer artifact for latest stable release was not found in storage.",
                version = latest.LatestVersion
            });
        }

        var downloadFileName = installerStorageService.GetWindowsDownloadFileName(latest.LatestVersion);
        return Results.File(installerPath, "application/x-msi", downloadFileName);
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
        var result = await commerceService.CreateCheckoutIntentAsync(userId.Value, email, cancellationToken);
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
                downloadUrl: "/download/windows",
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

static bool IsValidSemVer(string value)
{
    return !string.IsNullOrWhiteSpace(value) && NuGetVersion.TryParse(value.Trim(), out _);
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

public partial class Program;
