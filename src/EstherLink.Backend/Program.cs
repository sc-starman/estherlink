using System.Threading.RateLimiting;
using EstherLink.Backend.Configuration;
using EstherLink.Backend.Contracts.App;
using EstherLink.Backend.Contracts.Licensing;
using EstherLink.Backend.Contracts.Whitelist;
using EstherLink.Backend.Data;
using EstherLink.Backend.Data.Entities;
using EstherLink.Backend.Data.Enums;
using EstherLink.Backend.Health;
using EstherLink.Backend.Security;
using EstherLink.Backend.Services;
using EstherLink.Backend.Swagger;
using EstherLink.Backend.Utilities;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.Configure<AdminSecurityOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.Configure<LicensingOptions>(builder.Configuration.GetSection("Licensing"));

var postgresConnection =
    builder.Configuration.GetConnectionString("Postgres") ??
    Environment.GetEnvironmentVariable("ESTHERLINK_DB_CONNECTION") ??
    throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(postgresConnection);
});

var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redisConnection);
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

builder.Services.AddScoped<LicenseService>();
builder.Services.AddScoped<WhitelistService>();
builder.Services.AddScoped<AppReleaseService>();
builder.Services.AddSingleton<LicenseResponseSigner>();

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

app.UseSwagger();
app.UseSwaggerUI();
app.UseRateLimiter();

if (builder.Configuration.GetValue("Database:ApplyMigrationsOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.MapGet("/", () => Results.Ok(new
{
    service = "EstherLink.Backend",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "live",
    utc = DateTimeOffset.UtcNow
}));

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

var api = app.MapGroup("/api");

api.MapPost("/license/verify", async (
        LicenseVerifyRequest request,
        LicenseService licenseService,
        CancellationToken cancellationToken) =>
    {
        var errors = ValidationHelpers.ValidateLicenseVerify(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var response = await licenseService.VerifyAsync(request, cancellationToken);
        return Results.Ok(response);
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
    .AddEndpointFilter<AdminApiKeyEndpointFilter>();

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

