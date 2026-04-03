using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.IdentityModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Net.Http;
using Serilog;
using Skoruba.Duende.IdentityServer.Admin.Configuration;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.Configuration;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.MySql;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.PostgreSQL;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.SqlServer;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;
using Skoruba.Duende.IdentityServer.Admin.UI.Services.Configurations;
using Skoruba.Duende.IdentityServer.Shared.Configuration.Authentication;
using Skoruba.Duende.IdentityServer.Shared.Configuration.Helpers;
using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.Admin.Services;

public static class StartupService
{
    public static void AddDataProtectionDbContext(this IServiceCollection services, IConfiguration configuration)
    {
        var databaseProviderConfiguration = configuration.GetSection(nameof(DatabaseProviderConfiguration)).Get<DatabaseProviderConfiguration>();
        var databaseMigration = StartupHelpers.GetDatabaseMigrationsConfiguration(configuration, MigrationAssemblyConfiguration.GetMigrationAssemblyByProvider(databaseProviderConfiguration!));

        services.AddDataProtectionDbContext<IdentityServerDataProtectionDbContext>(configuration, databaseMigration);
        services.AddDataProtection<IdentityServerDataProtectionDbContext>(configuration);
    }

    private static void AddDataProtectionDbContext<TDataProtectionDbContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        DatabaseMigrationsConfiguration databaseMigrations)
        where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
    {
        var databaseProvider = configuration.GetSection(nameof(DatabaseProviderConfiguration))
            .Get<DatabaseProviderConfiguration>();

        var connectionStrings = configuration.GetSection("ConnectionStrings")
            .Get<ConnectionStringsConfiguration>();

        if (databaseProvider == null)
        {
            throw new ArgumentNullException(nameof(databaseProvider), "Database provider configuration is missing.");
        }

        if (connectionStrings == null)
        {
            throw new ArgumentNullException(nameof(connectionStrings), "Connection strings configuration is missing.");
        }

        var isDevelopment = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);

        switch (databaseProvider.ProviderType)
        {
            case DatabaseProviderType.SqlServer:
                services.AddDataProtectionDbContextSqlServer<TDataProtectionDbContext>(
                    connectionStrings.DataProtectionDbConnection,
                    databaseMigrations.DataProtectionDbMigrationsAssembly);
                break;
            case DatabaseProviderType.PostgreSQL:
                services.AddDataProtectionDbContextNpgSql<TDataProtectionDbContext>(
                    connectionStrings.DataProtectionDbConnection,
                    databaseMigrations.DataProtectionDbMigrationsAssembly);
                break;
            case DatabaseProviderType.MySql:
                services.AddDataProtectionDbContextMySql<TDataProtectionDbContext>(
                    NormalizeMySqlConnectionStringForDevelopment(connectionStrings.DataProtectionDbConnection, isDevelopment),
                    databaseMigrations.DataProtectionDbMigrationsAssembly);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(databaseProvider.ProviderType),
                    $@"The value needs to be one of {string.Join(", ", Enum.GetNames<DatabaseProviderType>())}.");
        }
    }

    public static void AddSerilog(this WebApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile("serilog.json", optional: true, reloadOnChange: true);

        builder.Host.UseSerilog((context, configuration) =>
            configuration.ReadFrom.Configuration(context.Configuration));
    }

    public static void AddAntiForgeryProtection(this IServiceCollection services)
    {
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "__Host-SkorubaBFF-CSRF";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
    }

    public static void AddAuthenticationConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        var adminConfiguration = configuration.GetSection(nameof(AdminConfiguration)).Get<AdminConfiguration>();
        ArgumentNullException.ThrowIfNull(adminConfiguration);
        var persistentLoginDuration = TimeSpan.FromDays(
            Math.Max(1, adminConfiguration.AuthenticationConfiguration.PersistentLoginDays));

        services.Configure<CookiePolicyOptions>(options =>
        {
            options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
            options.Secure = CookieSecurePolicy.SameAsRequest;
            options.OnAppendCookie = cookieContext =>
                AuthenticationHelpers.CheckSameSite(cookieContext.Context, cookieContext.CookieOptions);
            options.OnDeleteCookie = cookieContext =>
                AuthenticationHelpers.CheckSameSite(cookieContext.Context, cookieContext.CookieOptions);
        });

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                if (adminConfiguration.AuthenticationConfiguration.PersistLogin)
                {
                    options.SlidingExpiration = true;
                    options.ExpireTimeSpan = persistentLoginDuration;
                }
                options.Events = new CookieAuthenticationEvents
                {
                    OnSigningIn = context =>
                    {
                        if (!adminConfiguration.AuthenticationConfiguration.PersistLogin)
                        {
                            return Task.CompletedTask;
                        }

                        context.Properties.IsPersistent = true;
                        context.Properties.AllowRefresh = true;

                        if (!context.Properties.ExpiresUtc.HasValue)
                        {
                            context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.Add(persistentLoginDuration);
                        }

                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;

                        return context.Response.CompleteAsync();
                    },
                    OnSigningOut = async e =>
                    {
                        await e.HttpContext.RevokeRefreshTokenAsync();
                    }
                };
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.Authority = adminConfiguration.AuthenticationConfiguration.Authority;
                options.RequireHttpsMetadata = adminConfiguration.AuthenticationConfiguration.RequireHttpsMetadata;
                options.ClientId = adminConfiguration.AuthenticationConfiguration.ClientId;
                options.ResponseType = "code";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = JwtClaimTypes.Name,
                    RoleClaimType = JwtClaimTypes.Role
                };
                // The STS posts the authorization response back to /signin-oidc, so the
                // transient OIDC cookies must survive a cross-site HTTPS POST callback.
                options.CorrelationCookie.SameSite = SameSiteMode.None;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.NonceCookie.SameSite = SameSiteMode.None;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.ClaimActions.MapUniqueJsonKey(JwtClaimTypes.Role, JwtClaimTypes.Role);
                options.ClaimActions.MapUniqueJsonKey("tenant_key", "tenant_key");
                options.ClaimActions.MapUniqueJsonKey(TenantClaimTypes.FirstTimeLogin, TenantClaimTypes.FirstTimeLogin);

                options.UsePkce = true;

                adminConfiguration.AuthenticationConfiguration.AdminScopes.ForEach(scope =>
                {
                    options.Scope.Add(scope);
                });

                options.SaveTokens = true;
                options.ClientSecret = adminConfiguration.AuthenticationConfiguration.ClientSecret;
                options.GetClaimsFromUserInfoEndpoint = true;

                var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
                if (isDevelopment && IsLocalDevelopmentHttpsUri(options.Authority))
                {
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    };
                }

                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.UseIfAvailable;
                options.Events = new OpenIdConnectEvents
                {
                    OnRemoteFailure = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenIdConnectTrace");

                        logger.LogError(
                            context.Failure,
                            "OpenIdConnect remote login failed. Path={Path}, QueryString={QueryString}, RedirectUri={RedirectUri}",
                            context.Request.Path,
                            context.Request.QueryString.Value ?? "<none>",
                            context.Properties?.RedirectUri ?? "<none>");

                        return Task.CompletedTask;
                    }
                };
            });
    }

    private static string NormalizeMySqlConnectionStringForDevelopment(string connectionString, bool isDevelopment)
    {
        if (!isDevelopment || string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var parts = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
            {
                var trimmedPart = part.TrimStart();
                return !trimmedPart.StartsWith("SslMode=", StringComparison.OrdinalIgnoreCase) &&
                       !trimmedPart.StartsWith("Ssl Mode=", StringComparison.OrdinalIgnoreCase) &&
                       !trimmedPart.StartsWith("AllowPublicKeyRetrieval=", StringComparison.OrdinalIgnoreCase);
            });

        return $"{string.Join(";", parts)};AllowPublicKeyRetrieval=True;SslMode=Disabled";
    }

    private static bool IsLocalDevelopmentHttpsUri(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) ||
            parsedUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return parsedUri.IsLoopback ||
               string.Equals(parsedUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               parsedUri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }
}
