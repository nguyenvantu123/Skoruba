using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.IdentityModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Collections;
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
                options.Cookie.SameSite = SameSiteMode.Lax;
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
                    OnRedirectToIdentityProvider = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenIdConnectTrace");

                        logger.LogInformation(
                            "OIDC challenge redirect. Path={Path}, ReturnUrl={ReturnUrl}, IssuerAddress={IssuerAddress}, RedirectUri={RedirectUri}, Scope={Scope}, ResponseType={ResponseType}, ClientId={ClientId}, Prompt={Prompt}, ACR={Acr}, ExtraParams={ExtraParams}",
                            context.Request.Path,
                            context.Properties?.RedirectUri ?? "<none>",
                            context.ProtocolMessage.IssuerAddress ?? "<none>",
                            context.ProtocolMessage.RedirectUri ?? "<none>",
                            context.ProtocolMessage.Scope ?? "<none>",
                            context.ProtocolMessage.ResponseType ?? "<none>",
                            context.ProtocolMessage.ClientId ?? "<none>",
                            context.ProtocolMessage.Prompt ?? "<none>",
                            context.ProtocolMessage.AcrValues ?? "<none>",
                            DumpProperties(context.ProtocolMessage.Parameters));

                        return Task.CompletedTask;
                    },
                    OnMessageReceived = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenIdConnectTrace");

                        logger.LogInformation(
                            "OIDC callback message received. Method={Method}, Path={Path}, Query={Query}, Form={Form}, Headers={Headers}",
                            context.Request.Method,
                            context.Request.Path,
                            Truncate(context.Request.QueryString.Value, 3000),
                            DumpRequestForm(context.Request),
                            DumpHeaders(context.Request.Headers));

                        return Task.CompletedTask;
                    },
                    OnAuthorizationCodeReceived = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenIdConnectTrace");

                        logger.LogInformation(
                            "OIDC authorization code received. Path={Path}, CodeLength={CodeLength}, State={State}, SessionState={SessionState}, IssuerAddress={IssuerAddress}",
                            context.Request.Path,
                            context.ProtocolMessage.Code?.Length ?? 0,
                            Truncate(context.ProtocolMessage.State, 256),
                            Truncate(context.ProtocolMessage.SessionState, 256),
                            context.ProtocolMessage.IssuerAddress ?? "<none>");

                        return Task.CompletedTask;
                    },
                    OnTokenResponseReceived = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenIdConnectTrace");

                        logger.LogInformation(
                            "OIDC token response received. AccessTokenLength={AccessTokenLength}, IdTokenLength={IdTokenLength}, RefreshTokenLength={RefreshTokenLength}, TokenType={TokenType}, ExpiresIn={ExpiresIn}, Error={Error}, ErrorDescription={ErrorDescription}",
                            context.TokenEndpointResponse?.AccessToken?.Length ?? 0,
                            context.TokenEndpointResponse?.IdToken?.Length ?? 0,
                            context.TokenEndpointResponse?.RefreshToken?.Length ?? 0,
                            context.TokenEndpointResponse?.TokenType ?? "<none>",
                            context.TokenEndpointResponse?.ExpiresIn ?? "<none>",
                            context.TokenEndpointResponse?.Error ?? "<none>",
                            Truncate(context.TokenEndpointResponse?.ErrorDescription, 512));

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenIdConnectTrace");

                        logger.LogInformation(
                            "OIDC token validated. Subject={Subject}, Name={Name}, Idp={Idp}, TenantKey={TenantKey}, Claims={Claims}",
                            context.Principal?.FindFirst("sub")?.Value ?? "<none>",
                            context.Principal?.Identity?.Name ?? "<none>",
                            context.Principal?.FindFirst("idp")?.Value ?? "<none>",
                            context.Principal?.FindFirst("tenant_key")?.Value ?? "<none>",
                            DumpClaims(context.Principal?.Claims));

                        return Task.CompletedTask;
                    },
                    OnUserInformationReceived = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenIdConnectTrace");

                        logger.LogInformation(
                            "OIDC userinfo received. UserInfo={UserInfo}",
                            Truncate(context.User.ToString(), 3000));

                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenIdConnectTrace");

                        logger.LogError(
                            context.Exception,
                            "OIDC authentication failed. Path={Path}, QueryString={QueryString}, Message={Message}",
                            context.Request.Path,
                            context.Request.QueryString.Value ?? "<none>",
                            context.Exception.Message);

                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenIdConnectTrace");

                        logger.LogError(
                            context.Failure,
                            "OpenIdConnect remote login failed. Path={Path}, QueryString={QueryString}, RedirectUri={RedirectUri}, FailureMessage={FailureMessage}, Properties={Properties}",
                            context.Request.Path,
                            context.Request.QueryString.Value ?? "<none>",
                            context.Properties?.RedirectUri ?? "<none>",
                            context.Failure?.Message ?? "<none>",
                            DumpAuthenticationProperties(context.Properties));

                        return Task.CompletedTask;
                    }
                };
            });
    }

    private static string DumpRequestForm(HttpRequest request)
    {
        try
        {
            if (!request.HasFormContentType)
            {
                return "<none>";
            }

            return string.Join("; ", request.Form.Select(x => $"{x.Key}={Truncate(x.Value.ToString(), 256)}"));
        }
        catch (Exception ex)
        {
            return $"<unavailable:{ex.GetType().Name}>";
        }
    }

    private static string DumpHeaders(IHeaderDictionary headers)
    {
        return string.Join("; ", headers.Select(h => $"{h.Key}={Truncate(h.Value.ToString(), 256)}"));
    }

    private static string DumpClaims(IEnumerable<System.Security.Claims.Claim>? claims)
    {
        if (claims == null)
        {
            return "<none>";
        }

        return string.Join("; ", claims.Select(c => $"{c.Type}={Truncate(c.Value, 256)}"));
    }

    private static string DumpAuthenticationProperties(AuthenticationProperties? properties)
    {
        if (properties == null)
        {
            return "<none>";
        }

        var items = properties.Items?.Select(x => $"{x.Key}={Truncate(x.Value, 256)}") ?? Enumerable.Empty<string>();
        var parameters = properties.Parameters?.Select(x => $"{x.Key}={Truncate(x.Value?.ToString(), 256)}") ?? Enumerable.Empty<string>();
        return string.Join("; ", items.Concat(parameters));
    }

    private static string DumpProperties(IDictionary<string, string?>? values)
    {
        if (values == null)
        {
            return "<none>";
        }

        return string.Join("; ", values.Select(x => $"{x.Key}={Truncate(x.Value, 256)}"));
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "<none>";
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "...";
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
