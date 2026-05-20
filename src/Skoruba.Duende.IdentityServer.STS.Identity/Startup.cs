using Duende.IdentityServer.Configuration;
using Duende.IdentityServer.Stores;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySql.EntityFrameworkCore.Infrastructure;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.Configuration;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.MySql;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.PostgreSQL;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Configuration.Schema;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using Skoruba.Duende.IdentityServer.Shared.Configuration.Helpers;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration.Constants;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration.Interfaces;
using Skoruba.Duende.IdentityServer.STS.Identity.Helpers;
using Skoruba.Duende.IdentityServer.STS.Identity.Stores;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.Resolution;
using TenantInfrastructure.Wiring;
using Microsoft.Extensions.Logging;
using Serilog;
using TenantInfrastructure.Identity;
using Skoruba.Duende.IdentityServer.STS.Identity.Services;

namespace Skoruba.Duende.IdentityServer.STS.Identity
{
    public class Startup
    {
        public IConfiguration Configuration { get; }
        public IWebHostEnvironment Environment { get; }

        public Startup(IWebHostEnvironment environment, IConfiguration configuration)
        {
            Configuration = configuration;
            Environment = environment;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            Configuration.ValidateStartupConfiguration();
            services.AddHttpContextAccessor();
            services.Configure<TenantIdentityDbResolutionConfiguration>(
                Configuration.GetSection(TenantIdentityDbResolutionConfiguration.SectionName));

            var tenantIdentityDbResolution = Configuration
                .GetSection(TenantIdentityDbResolutionConfiguration.SectionName)
                .Get<TenantIdentityDbResolutionConfiguration>() ?? new TenantIdentityDbResolutionConfiguration();

            var databaseProviderConfiguration = Configuration
                .GetSection(nameof(DatabaseProviderConfiguration))
                .Get<DatabaseProviderConfiguration>()
                ?? new DatabaseProviderConfiguration { ProviderType = DatabaseProviderType.MySql };

            var identityDbConnectionString = Configuration.GetConnectionString(ConfigurationConsts.IdentityDbConnectionStringKey);

            if (string.IsNullOrWhiteSpace(identityDbConnectionString))
            {
                throw new InvalidOperationException(
                    "Connection string 'IdentityDbConnection' is required for TenantInfrastructure. " +
                    "Set ConnectionStrings:IdentityDbConnection in configuration.");
            }

            services.AddTenantInfrastructure(opt =>
            {
                opt.DatabaseProvider = databaseProviderConfiguration.ProviderType.ToString();
                opt.MasterConnectionString = databaseProviderConfiguration.ProviderType == DatabaseProviderType.MySql
                    ? NormalizeMySqlConnectionStringForDevelopment(identityDbConnectionString, Environment.IsDevelopment())
                    : identityDbConnectionString;
                opt.RedisConnectionString = Configuration.GetConnectionString("Redis") ?? string.Empty;
                opt.RedisInstanceName = Configuration.GetValue<string>("TenantInfrastructure:RedisInstanceName") ?? "tenant-registry:";
                opt.ApplyMasterDbMigrations = Configuration.GetValue<bool>("TenantInfrastructure:ApplyMasterDbMigrations");
                opt.AllowMasterDbAutoMigration = Configuration.GetValue<bool>("TenantInfrastructure:AllowMasterDbAutoMigration");

                opt.Resolution.MinHostParts = 3;
                opt.Resolution.ReservedSubdomains.Add("sts");
                opt.Resolution.ReservedSubdomains.Add("identity");
                opt.Resolution.ReservedSubdomains.Add("sso");

                // Mobile clients call central STS and send tenant via header.
                // Use one canonical header to avoid ambiguity across clients/gateways.
                opt.Resolution.TenantHeaderNames.Clear();
                opt.Resolution.TenantHeaderNames.Add("X-Tenant-Id");

                if (Uri.TryCreate(tenantIdentityDbResolution.CentralBaseUrl, UriKind.Absolute, out var centralUri))
                {
                    opt.Resolution.SkipHosts.Add(centralUri.Host);
                }
            });

            var rootConfiguration = CreateRootConfiguration();
            var developmentOidcClientSyncConfiguration = Configuration
                .GetSection(DevelopmentOidcClientSyncConfiguration.SectionName)
                .Get<DevelopmentOidcClientSyncConfiguration>() ?? new DevelopmentOidcClientSyncConfiguration();
            services.AddSingleton(rootConfiguration);
            services.AddSingleton(rootConfiguration.AdminConfiguration);
            services.AddSingleton(developmentOidcClientSyncConfiguration);
            services.AddSingleton<IStsIdentityDbConnectionStringResolver, StsIdentityDbConnectionStringResolver>();
            services.AddScoped<IClientTenantRedirectResolver, ClientTenantRedirectResolver>();
            services.AddScoped(ResolveIdentityTableConfiguration);
            services.AddScoped<DevelopmentAdminUiClientSyncService>();
            services.AddScoped<DevelopmentOidcClientSyncService>();

            services.Configure<ServerSideSessionsConfiguration>(Configuration.GetSection(ServerSideSessionsConfiguration.SectionName));

            RegisterDbContexts(services);

            services.AddDataProtection<IdentityServerDataProtectionDbContext>(Configuration);
            services.AddEmailSenders(Configuration);
            RegisterAuthentication(services);
            services.AddScoped<ITenantAdminAccountService, TenantAdminAccountService>();
            services.AddScoped<ITenantRegistryLookupService, TenantRegistryLookupService>();

            services.Decorate<IResourceStore, MySqlSafeResourceStore>();

            services.PostConfigure<IdentityServerOptions>(o =>
            {
                o.PushedAuthorization.Required = false;
                o.Endpoints.EnablePushedAuthorizationEndpoint = false;
            });

            RegisterHstsOptions(services);

            services.AddMvcWithLocalization<UserIdentity, string>(Configuration);
            services.AddRazorPages();

            RegisterAuthorization(services);

            services.AddIdSHealthChecks<IdentityServerConfigurationDbContext, IdentityServerPersistedGrantDbContext, AdminIdentityDbContext, IdentityServerDataProtectionDbContext>(Configuration);
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.InitializeTenantMasterDbAsync().GetAwaiter().GetResult();

            if (env.IsDevelopment())
            {
                SyncDevelopmentAdminUiClient(app);
                SyncDevelopmentOidcClients(app);
            }

            app.UseTenantInfrastructure();
            app.UseCookiePolicy();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();
            }

            app.UsePathBase(Configuration.GetValue<string>("BasePath"));

            app.UseStaticFiles();
            app.UseSerilogRequestLogging();
            app.Use(async (context, next) =>
            {
                if (!ShouldTraceAuthorizePath(context.Request.Path))
                {
                    await next();
                    return;
                }

                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AuthorizeTrace");

                logger.LogInformation(
                    "Authorize request received. Method={Method}, Scheme={Scheme}, Host={Host}, Path={Path}, Query={Query}, IsAuthenticated={IsAuthenticated}, UserName={UserName}, CookieNames={CookieNames}",
                    context.Request.Method,
                    context.Request.Scheme,
                    context.Request.Host.Value,
                    context.Request.Path.Value ?? "<none>",
                    TruncateForLog(context.Request.QueryString.Value, 3000),
                    context.User?.Identity?.IsAuthenticated ?? false,
                    context.User?.Identity?.Name ?? "<none>",
                    DumpCookieNames(context.Request.Cookies.Keys));

                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Authorize pipeline failed. Method={Method}, Scheme={Scheme}, Host={Host}, Path={Path}, Query={Query}, IsAuthenticated={IsAuthenticated}, UserName={UserName}",
                        context.Request.Method,
                        context.Request.Scheme,
                        context.Request.Host.Value,
                        context.Request.Path.Value ?? "<none>",
                        TruncateForLog(context.Request.QueryString.Value, 3000),
                        context.User?.Identity?.IsAuthenticated ?? false,
                        context.User?.Identity?.Name ?? "<none>");
                    throw;
                }

                logger.LogInformation(
                    "Authorize response completed. Method={Method}, Scheme={Scheme}, Host={Host}, Path={Path}, StatusCode={StatusCode}, Location={Location}, IsAuthenticated={IsAuthenticated}, UserName={UserName}",
                    context.Request.Method,
                    context.Request.Scheme,
                    context.Request.Host.Value,
                    context.Request.Path.Value ?? "<none>",
                    context.Response.StatusCode,
                    TruncateForLog(context.Response.Headers.Location.ToString(), 3000),
                    context.User?.Identity?.IsAuthenticated ?? false,
                    context.User?.Identity?.Name ?? "<none>");
            });
            UseAuthentication(app);

            app.UseSecurityHeaders(Configuration);
            app.UseMvcLocalizationServices();

            app.UseRouting();
            app.UseAuthorization();
            app.UseEndpoints(endpoint =>
            {
                endpoint.MapDefaultControllerRoute();
                endpoint.MapHealthChecks("/health", new HealthCheckOptions
                {
                    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                });
            });
        }

        public virtual void RegisterDbContexts(IServiceCollection services)
        {
            var databaseProviderConfiguration = Configuration.GetSection(nameof(DatabaseProviderConfiguration)).Get<DatabaseProviderConfiguration>();
            var connectionStrings = Configuration.GetSection("ConnectionStrings").Get<ConnectionStringsConfiguration>() ?? new ConnectionStringsConfiguration();

            var migrationsAssembly = databaseProviderConfiguration.ProviderType switch
            {
                DatabaseProviderType.SqlServer => "Skoruba.Duende.IdentityServer.Admin.EntityFramework.SqlServer",
                DatabaseProviderType.PostgreSQL => "Skoruba.Duende.IdentityServer.Admin.EntityFramework.PostgreSQL",
                DatabaseProviderType.MySql => "Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql",
                _ => throw new ArgumentOutOfRangeException(nameof(databaseProviderConfiguration.ProviderType),
                    $@"The value needs to be one of {string.Join(", ", Enum.GetNames(typeof(DatabaseProviderType)))}.")
            };

            var isDevelopment = Environment.IsDevelopment();

            AddIdentityDbContext(services, databaseProviderConfiguration.ProviderType, migrationsAssembly, isDevelopment);

            AddConfiguredDbContext<IdentityServerConfigurationDbContext>(services, databaseProviderConfiguration.ProviderType, connectionStrings.ConfigurationDbConnection, migrationsAssembly, isDevelopment);
            AddConfiguredDbContext<IdentityServerPersistedGrantDbContext>(services, databaseProviderConfiguration.ProviderType, connectionStrings.PersistedGrantDbConnection, migrationsAssembly, isDevelopment);
            AddConfiguredDbContext<IdentityServerDataProtectionDbContext>(services, databaseProviderConfiguration.ProviderType, connectionStrings.DataProtectionDbConnection, migrationsAssembly, isDevelopment);
        }

        private static void AddIdentityDbContext(IServiceCollection services, DatabaseProviderType providerType, string migrationsAssembly, bool isDevelopment)
        {
            services.AddDbContext<AdminIdentityDbContext>((serviceProvider, options) =>
            {
                var identityConnectionString = serviceProvider
                    .GetRequiredService<IStsIdentityDbConnectionStringResolver>()
                    .ResolveConnectionString();

                options.ReplaceService<IModelCacheKeyFactory, AdminIdentityDbContextModelCacheKeyFactory>();

                switch (providerType)
                {
                    case DatabaseProviderType.SqlServer:
                        options.UseSqlServer(identityConnectionString, b => b.MigrationsAssembly(migrationsAssembly));
                        break;
                    case DatabaseProviderType.PostgreSQL:
                        options.UseNpgsql(identityConnectionString, b => b.MigrationsAssembly(migrationsAssembly))
                            .UseSkorubaPostgreSqlNamingConvention();
                        break;
                    case DatabaseProviderType.MySql:
                        options.UseMySQL(
                            NormalizeMySqlConnectionStringForDevelopment(identityConnectionString, isDevelopment),
                            b =>
                            {
                                b.MigrationsAssembly(migrationsAssembly);
                                ConfigureMySqlRetry(b);
                            })
                            .UseSkorubaMySqlNamingConvention();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(providerType),
                            $@"The value needs to be one of {string.Join(", ", Enum.GetNames(typeof(DatabaseProviderType)))}.");
                }
            });
        }

        private static IdentityTableConfiguration ResolveIdentityTableConfiguration(IServiceProvider serviceProvider)
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var tenantAccessor = serviceProvider.GetRequiredService<ITenantContextAccessor>();

            var tableConfiguration = new IdentityTableConfiguration();
            configuration.GetSection(nameof(IdentityTableConfiguration)).Bind(tableConfiguration);

            if (tenantAccessor.Current != null)
            {
                configuration.GetSection("TenantIdentityTableConfiguration").Bind(tableConfiguration);
            }

            return tableConfiguration;
        }

        private static void AddConfiguredDbContext<TContext>(IServiceCollection services, DatabaseProviderType providerType, string connectionString, string migrationsAssembly, bool isDevelopment)
            where TContext : DbContext
        {
            services.AddDbContext<TContext>(options =>
            {
                switch (providerType)
                {
                    case DatabaseProviderType.SqlServer:
                        options.UseSqlServer(connectionString, b => b.MigrationsAssembly(migrationsAssembly));
                        break;
                    case DatabaseProviderType.PostgreSQL:
                        options.UseNpgsql(connectionString, b => b.MigrationsAssembly(migrationsAssembly))
                            .UseSkorubaPostgreSqlNamingConvention();
                        break;
                    case DatabaseProviderType.MySql:
                        options.UseMySQL(
                            NormalizeMySqlConnectionStringForDevelopment(connectionString, isDevelopment),
                            b =>
                            {
                                b.MigrationsAssembly(migrationsAssembly);
                                ConfigureMySqlRetry(b);
                            })
                            .UseSkorubaMySqlNamingConvention();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(providerType),
                            $@"The value needs to be one of {string.Join(", ", Enum.GetNames(typeof(DatabaseProviderType)))}.");
                }
            });
        }

        private static string NormalizeMySqlConnectionStringForDevelopment(string connectionString, bool isDevelopment)
        {
            if (!isDevelopment || string.IsNullOrWhiteSpace(connectionString))
            {
                return connectionString;
            }

            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            RemoveConnectionStringKey(builder, "SslMode");
            RemoveConnectionStringKey(builder, "Ssl Mode");

            builder["SslMode"] = "Disabled";
            builder["Pooling"] = "true";
            builder["Connection Timeout"] = "30";
            builder["Default Command Timeout"] = "60";
            builder["Minimum Pool Size"] = "0";
            builder["Maximum Pool Size"] = "200";

            return builder.ConnectionString;
        }

        private static void RemoveConnectionStringKey(DbConnectionStringBuilder builder, string key)
        {
            if (builder.ContainsKey(key))
            {
                builder.Remove(key);
            }
        }

        private static void ConfigureMySqlRetry(MySQLDbContextOptionsBuilder mySqlOptions)
        {
            mySqlOptions.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
        }

        public virtual void RegisterAuthentication(IServiceCollection services)
        {
            services.AddAuthenticationServices<AdminIdentityDbContext, UserIdentity, UserIdentityRole>(Configuration);
            services.AddIdentityServer<IdentityServerConfigurationDbContext, IdentityServerPersistedGrantDbContext, UserIdentity>(Configuration);
        }

        public virtual void RegisterAuthorization(IServiceCollection services)
        {
            var rootConfiguration = CreateRootConfiguration();
            services.AddAuthorizationPolicies(rootConfiguration);
        }

        public virtual void UseAuthentication(IApplicationBuilder app)
        {
            app.UseAuthentication();
            app.UseIdentityServer();
        }

        public virtual void RegisterHstsOptions(IServiceCollection services)
        {
            services.AddHsts(options =>
            {
                options.Preload = true;
                options.IncludeSubDomains = true;
                options.MaxAge = TimeSpan.FromDays(365);
            });
        }

        protected IRootConfiguration CreateRootConfiguration()
        {
            var rootConfiguration = new RootConfiguration();
            Configuration.GetSection(ConfigurationConsts.AdminConfigurationKey).Bind(rootConfiguration.AdminConfiguration);
            Configuration.GetSection(ConfigurationConsts.RegisterConfigurationKey).Bind(rootConfiguration.RegisterConfiguration);
            return rootConfiguration;
        }

        private static void SyncDevelopmentAdminUiClient(IApplicationBuilder app)
        {
            using var scope = app.ApplicationServices.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Startup>>();

            try
            {
                scope.ServiceProvider
                    .GetRequiredService<DevelopmentAdminUiClientSyncService>()
                    .SyncAsync()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to synchronize the admin UI client at STS startup.");
            }
        }

        private static void SyncDevelopmentOidcClients(IApplicationBuilder app)
        {
            using var scope = app.ApplicationServices.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Startup>>();

            try
            {
                scope.ServiceProvider
                    .GetRequiredService<DevelopmentOidcClientSyncService>()
                    .SyncAsync()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to synchronize development OIDC clients at STS startup.");
            }
        }

        private static bool ShouldTraceAuthorizePath(PathString path)
        {
            return path.StartsWithSegments("/connect/authorize", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWithSegments("/connect/authorize/callback", StringComparison.OrdinalIgnoreCase);
        }

        private static string DumpCookieNames(IEnumerable<string> cookieNames)
        {
            return string.Join(", ", cookieNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name)
                .Select(name => TruncateForLog(name, 128)));
        }

        private static string TruncateForLog(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<none>";
            }

            return value.Length <= maxLength ? value : value[..maxLength] + "...";
        }
    }
}
