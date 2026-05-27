// Feature: tenant-client-cache-public-read — Mobile BFF host.
//
// Minimal-API host that bridges Flutter mobile clients and the public-read
// endpoint of the tenant client cache. The BFF holds the per-tenant API key
// server-side, validates the END USER via Bearer JWT issued by Skoruba STS,
// and proxies a slim, mobile-friendly response.
//
// Key invariants:
//   * No DbContext / no Admin.UI.Api / no BusinessLogic dependency. The BFF
//     is stateless (R2.7-style isolation).
//   * `tenantKey` is derived ONLY from the validated `tenant_key` claim.
//   * Configuration is fail-fast: bad config aborts host start.
//   * Options are resolved from DI (IOptions<T>) rather than captured at
//     host-builder time, so test fixtures that swap configuration at
//     ConfigureAppConfiguration time observe the merged result.
//   * No CORS — mobile-only host.

using System.Net.Http;
using System.Globalization;
using System.Threading.RateLimiting;

using IdentityModel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;

using Serilog;

using Skoruba.Duende.IdentityServer.Mobile.Bff.Configuration;
using Skoruba.Duende.IdentityServer.Mobile.Bff.Endpoints;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client;

var builder = WebApplication.CreateBuilder(args);

// ─── Logging ─────────────────────────────────────────────────────────────
// Serilog with the same pattern the rest of the solution uses: read config
// from `serilog.json` (or `appsettings*.json`), fall back to console.
// Skipped in the `Test` environment so integration tests can install their
// own ILogger capturing provider without Serilog short-circuiting it.
builder.Configuration.AddJsonFile("serilog.json", optional: true, reloadOnChange: true);
if (!string.Equals(builder.Environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase))
{
    builder.Host.UseSerilog((context, _, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .WriteTo.Console();
    });
}

// ─── User secrets (development only) ─────────────────────────────────────
builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: true);

// ─── Strongly-typed options + fail-fast validation ───────────────────────
builder.Services.AddOptions<MobileBffConfiguration>()
    .Bind(builder.Configuration.GetSection(MobileBffConfiguration.SectionName))
    .Validate(static cfg =>
    {
        if (cfg is null) return false;
        if (string.IsNullOrWhiteSpace(cfg.Authentication.Authority)) return false;
        if (!Uri.TryCreate(cfg.TenantClientCache.BaseAddress, UriKind.Absolute, out _)) return false;
        if (string.IsNullOrWhiteSpace(cfg.TenantClientCache.ApiKey)) return false;
        if (cfg.TenantClientCache.HttpTimeoutSeconds is < 1 or > 60) return false;
        if (cfg.TenantClientCache.MaxRetryAttempts is < 0 or > 5) return false;
        if (cfg.TenantClientCache.MaxClientCacheTtlSeconds is < 0 or > 3600) return false;
        if (cfg.RateLimiting.BootstrapPermitLimit is < 1 or > 1000) return false;
        if (cfg.RateLimiting.BootstrapWindowSeconds is < 1 or > 3600) return false;
        if (cfg.RateLimiting.BootstrapQueueLimit is < 0 or > 100) return false;
        return true;
    },
    "MobileBff configuration failed validation. Required: Authentication:Authority (non-empty), "
    + "TenantClientCache:BaseAddress (absolute URI), TenantClientCache:ApiKey (non-empty), "
    + "HttpTimeoutSeconds in [1,60], MaxRetryAttempts in [0,5], MaxClientCacheTtlSeconds in [0,3600], "
    + "RateLimiting:BootstrapPermitLimit in [1,1000], BootstrapWindowSeconds in [1,3600], "
    + "BootstrapQueueLimit in [0,100].")
    .ValidateOnStart();

// ─── Authentication (Bearer JWT — mirrors STS.Identity StartupHelpers) ───
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { });

builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<MobileBffConfiguration>>((options, bffOptionsMonitor) =>
    {
        var bffOptions = bffOptionsMonitor.Value;
        var authority = (bffOptions.Authentication.Authority ?? string.Empty).Trim().TrimEnd('/');
        var audience = bffOptions.Authentication.Audience;
        var validateAudience = !string.IsNullOrWhiteSpace(audience);

        options.Authority = authority;
        options.RequireHttpsMetadata = bffOptions.Authentication.RequireHttpsMetadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = validateAudience,
            ValidAudience = validateAudience ? audience : null,
            ValidateIssuer = true,
            ValidIssuer = authority,
            NameClaimType = JwtClaimTypes.Name,
            RoleClaimType = JwtClaimTypes.Role
        };

        // Dev-cert handler (mirrors the STS.Identity pattern). Only enabled
        // in Development AND when the authority is a *.localhost https URL.
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environment, Environments.Development, StringComparison.OrdinalIgnoreCase);
        if (isDevelopment && IsLocalDevelopmentHttpsUri(authority))
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }
    });

builder.Services.AddAuthorization();

// ─── SDK wiring ──────────────────────────────────────────────────────────
// Use a placeholder URI so AddTenantClientCacheClient's named HttpClient
// can be set up; the real values are projected from IOptions<MobileBffConfiguration>
// below via a post-configure step. This indirection lets tests swap config
// in ConfigureAppConfiguration and have it take effect.
builder.Services.AddTenantClientCacheClient(o =>
{
    o.BaseAddress = new Uri("https://placeholder.invalid", UriKind.Absolute);
    o.ApiKey = "placeholder";
});

builder.Services
    .AddOptions<TenantClientCacheClientOptions>()
    .Configure<IOptions<MobileBffConfiguration>>((o, bffOptionsMonitor) =>
    {
        var section = bffOptionsMonitor.Value.TenantClientCache;
        if (Uri.TryCreate(section.BaseAddress, UriKind.Absolute, out var baseAddress))
        {
            o.BaseAddress = baseAddress;
        }
        if (!string.IsNullOrWhiteSpace(section.ApiKey))
        {
            o.ApiKey = section.ApiKey;
        }
        if (section.HttpTimeoutSeconds >= 1 && section.HttpTimeoutSeconds <= 60)
        {
            o.HttpTimeout = TimeSpan.FromSeconds(section.HttpTimeoutSeconds);
        }
        if (section.MaxRetryAttempts >= 0 && section.MaxRetryAttempts <= 5)
        {
            o.MaxRetryAttempts = section.MaxRetryAttempts;
        }
        if (section.MaxClientCacheTtlSeconds >= 0 && section.MaxClientCacheTtlSeconds <= 3600)
        {
            o.MaxClientCacheTtl = TimeSpan.FromSeconds(section.MaxClientCacheTtlSeconds);
        }
    });

// ─── Rate limiter (anonymous bootstrap endpoint only) ────────────────────
// IP-partitioned fixed-window limiter applied via the policy
// `MobileBff_Bootstrap`. Resolves config from IOptionsSnapshot per request
// so test fixtures that overlay configuration via ConfigureAppConfiguration
// observe updated permit / window values.
builder.Services.AddRateLimiter(rateLimiterOptions =>
{
    rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiterOptions.AddPolicy(MobileBootstrapEndpoints.RateLimitPolicyName, httpContext =>
    {
        var snapshot = httpContext.RequestServices
            .GetRequiredService<IOptionsSnapshot<MobileBffConfiguration>>()
            .Value
            .RateLimiting;

        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var permitLimit = Math.Max(1, snapshot.BootstrapPermitLimit);
        var windowSeconds = Math.Max(1, snapshot.BootstrapWindowSeconds);
        var queueLimit = Math.Max(0, snapshot.BootstrapQueueLimit);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = queueLimit,
                AutoReplenishment = true
            });
    });

    rateLimiterOptions.OnRejected = OnBootstrapRateLimitRejectedAsync;
});

// ─── Endpoints ───────────────────────────────────────────────────────────
var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (!string.Equals(app.Environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase))
{
    // Serilog request logging only when Serilog is wired (skipped in the
    // Test environment so the test logger pipeline isn't disturbed).
    app.UseSerilogRequestLogging();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("Mobile_Health")
    .AllowAnonymous();

app.MapMobileClientEndpoints();
app.MapMobileBootstrapEndpoints();

app.Run();

static bool IsLocalDevelopmentHttpsUri(string? uri)
{
    if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri)
        || parsedUri.Scheme != Uri.UriSchemeHttps)
    {
        return false;
    }

    return parsedUri.IsLoopback
        || string.Equals(parsedUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || parsedUri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}

// 429 rejection handler for the `MobileBff_Bootstrap` policy. Writes the
// canonical body `{"error":"rate_limit_exceeded"}`, sets `Retry-After`
// from the lease metadata (fallback = configured window seconds), and
// emits a Warning log with the partition's IP for operator dashboards.
// Never logs API keys or snapshot bodies (R12.7).
static async ValueTask OnBootstrapRateLimitRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
{
    var services = context.HttpContext.RequestServices;
    var bffOptions = services.GetRequiredService<IOptionsSnapshot<MobileBffConfiguration>>().Value;

    var retryAfterSeconds = Math.Max(1, bffOptions.RateLimiting.BootstrapWindowSeconds);
    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ts))
    {
        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(ts.TotalSeconds));
    }

    var response = context.HttpContext.Response;
    response.StatusCode = StatusCodes.Status429TooManyRequests;
    response.Headers[HeaderNames.RetryAfter] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
    response.ContentType = "application/json; charset=utf-8";

    var loggerFactory = services.GetService<ILoggerFactory>();
    var logger = loggerFactory?.CreateLogger("Skoruba.Duende.IdentityServer.Mobile.Bff.MobileBootstrapEndpoints");
    if (logger is not null)
    {
        var remoteIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        logger.LogWarning(
            "Mobile BFF bootstrap rate-limit exceeded. RemoteIp={RemoteIp} RetryAfterSeconds={RetryAfterSeconds}",
            remoteIp,
            retryAfterSeconds);
    }

    await response
        .WriteAsync("{\"error\":\"rate_limit_exceeded\"}", cancellationToken)
        .ConfigureAwait(false);
}

/// <summary>
/// Public partial declaration so <c>WebApplicationFactory&lt;Program&gt;</c>
/// in the integration tests can locate the entry point assembly.
/// </summary>
public partial class Program
{
}
