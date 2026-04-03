// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System.IdentityModel.Tokens.Jwt;
using Duende.IdentityServer.EntityFramework.Options;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSwag.AspNetCore;
using Skoruba.AuditLogging.EntityFramework.Entities;
using Skoruba.Duende.IdentityServer.Admin.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.Api.Helpers;
using Skoruba.Duende.IdentityServer.Admin.Api.Services;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.Configuration;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.Constants;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers;
using Skoruba.Duende.IdentityServer.Shared.Configuration.Helpers;
using Skoruba.Duende.IdentityServer.Shared.Dtos;
using Skoruba.Duende.IdentityServer.Shared.Dtos.Identity;
using System.Linq;
using System.Threading.RateLimiting;
using StartupHelpers = Skoruba.Duende.IdentityServer.Shared.Configuration.Helpers.StartupHelpers;
using TenantInfrastructure.Resolution;
using TenantInfrastructure.Wiring;

namespace Skoruba.Duende.IdentityServer.Admin.Api
{
    public class Startup
    {
        public Startup(IWebHostEnvironment env, IConfiguration configuration)
        {
            JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
            HostingEnvironment = env;
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public IWebHostEnvironment HostingEnvironment { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            var adminApiConfiguration = Configuration.GetSection(nameof(AdminApiConfiguration)).Get<AdminApiConfiguration>();
            services.AddSingleton(adminApiConfiguration);
            ConfigureThemePreferenceCache(services);
            ConfigurePublicTenantDirectory(services);
            ConfigureTenantAdminProjectionSync(services);

            var databaseProviderConfiguration = Configuration.GetSection(nameof(DatabaseProviderConfiguration)).Get<DatabaseProviderConfiguration>();
            var databaseMigration = StartupHelpers.GetDatabaseMigrationsConfiguration(Configuration, MigrationAssemblyConfiguration.GetMigrationAssemblyByProvider(databaseProviderConfiguration));

            services.AddTenantInfrastructure(opt =>
            {
                opt.MasterConnectionString = NormalizeMySqlConnectionStringForDevelopment(Configuration.GetConnectionString("MasterDb"));
                opt.RedisConnectionString = Configuration.GetConnectionString("Redis");
                opt.RedisInstanceName = Configuration.GetValue<string>("TenantInfrastructure:RedisInstanceName") ?? "tenant-registry:";
                opt.ApplyMasterDbMigrations = Configuration.GetValue<bool>("TenantInfrastructure:ApplyMasterDbMigrations");
                opt.AllowMasterDbAutoMigration = Configuration.GetValue<bool>("TenantInfrastructure:AllowMasterDbAutoMigration");
                opt.Resolution.MinHostParts = 3;
                opt.Resolution.ReservedSubdomains.Add("sso");
                opt.Resolution.AllowMissingTenant = true;
            });
            services.AddHttpContextAccessor();
            services.AddSingleton(new ConfigurationStoreOptions());
            services.AddSingleton(new OperationalStoreOptions());

            RegisterDbContexts(services, databaseMigration);

            services.AddEmailSenders(Configuration);
            RegisterAuthentication(services);
            RegisterAuthorization(services);

            services.AddIdentityServerAdminApi<AdminIdentityDbContext, IdentityServerConfigurationDbContext, IdentityServerPersistedGrantDbContext, IdentityServerDataProtectionDbContext, AdminLogDbContext, AdminAuditLogDbContext, AdminConfigurationDbContext, AuditLog,
                IdentityUserDto, IdentityRoleDto, UserIdentity, UserIdentityRole, string, UserIdentityUserClaim, UserIdentityUserRole,
                UserIdentityUserLogin, UserIdentityRoleClaim, UserIdentityUserToken,
                IdentityUsersDto, IdentityRolesDto, IdentityUserRolesDto,
                IdentityUserClaimsDto, IdentityUserProviderDto, IdentityUserProvidersDto, IdentityUserChangePasswordDto,
                IdentityRoleClaimsDto, IdentityUserClaimDto, IdentityRoleClaimDto>(Configuration, adminApiConfiguration);
            services.AddTransient<ITenantAdminProjectionSyncService, TenantAdminProjectionSyncService>();

            services.AddSwaggerServices(adminApiConfiguration);

            services.AddIdSHealthChecks<IdentityServerConfigurationDbContext, IdentityServerPersistedGrantDbContext, AdminIdentityDbContext, AdminLogDbContext, AdminAuditLogDbContext, IdentityServerDataProtectionDbContext>(Configuration, adminApiConfiguration);
        }

        private void ConfigureThemePreferenceCache(IServiceCollection services)
        {
            var themeCacheConfiguration = Configuration
                .GetSection(ThemePreferenceCacheConfiguration.SectionName)
                .Get<ThemePreferenceCacheConfiguration>() ?? new ThemePreferenceCacheConfiguration();

            services.Configure<ThemePreferenceCacheConfiguration>(
                Configuration.GetSection(ThemePreferenceCacheConfiguration.SectionName));

            if (string.IsNullOrWhiteSpace(themeCacheConfiguration.RedisConnectionString))
            {
                services.AddDistributedMemoryCache();
                return;
            }

            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = themeCacheConfiguration.RedisConnectionString;
                options.InstanceName = themeCacheConfiguration.InstanceName;
            });
        }

        private void ConfigurePublicTenantDirectory(IServiceCollection services)
        {
            var publicTenantDirectoryConfiguration = Configuration
                .GetSection(PublicTenantDirectoryConfiguration.SectionName)
                .Get<PublicTenantDirectoryConfiguration>() ?? new PublicTenantDirectoryConfiguration();

            services.Configure<PublicTenantDirectoryConfiguration>(
                Configuration.GetSection(PublicTenantDirectoryConfiguration.SectionName));
            services.AddResponseCaching();
            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy(PublicTenantApiConsts.RateLimitPolicy, httpContext =>
                {
                    var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var permitLimit = Math.Max(1, publicTenantDirectoryConfiguration.RateLimitPermitLimit);
                    var windowSeconds = Math.Max(1, publicTenantDirectoryConfiguration.RateLimitWindowSeconds);
                    var queueLimit = Math.Max(0, publicTenantDirectoryConfiguration.RateLimitQueueLimit);

                    return RateLimitPartition.GetFixedWindowLimiter(
                        remoteIp,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitLimit,
                            Window = TimeSpan.FromSeconds(windowSeconds),
                            QueueLimit = queueLimit,
                            AutoReplenishment = true
                        });
                });
            });
        }

        private void ConfigureTenantAdminProjectionSync(IServiceCollection services)
        {
            services.Configure<TenantAdminProjectionSyncConfiguration>(
                Configuration.GetSection(TenantAdminProjectionSyncConfiguration.SectionName));

            services.AddHttpClient("TenantAdminProjectionSync", client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, AdminApiConfiguration adminApiConfiguration)
        {
            app.InitializeTenantMasterDbAsync().GetAwaiter().GetResult();

            app.AddForwardHeaders(Configuration);

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            if (!env.IsDevelopment() && adminApiConfiguration.AllowedHosts?.Length > 0)
            {
                app.Use(async (context, next) =>
                {
                    var host = context.Request.Host.Host;
                    if (!HostMatcher.IsAllowed(host, adminApiConfiguration.AllowedHosts))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        await context.Response.WriteAsync("Not Found");
                        return;
                    }

                    await next();
                });
            }

            app.UseOpenApi();
            app.UseSwaggerUi(settings =>
            {
                settings.OAuth2Client = new OAuth2ClientSettings
                {
                    ClientId = adminApiConfiguration.OidcSwaggerUIClientId,
                    AppName = adminApiConfiguration.ApiName,
                    UsePkceWithAuthorizationCodeGrant = true,
                    ClientSecret = null
                };
            });

            app.UseWhen(
                context => !context.Request.Path.StartsWithSegments("/api/tenants/public", StringComparison.OrdinalIgnoreCase),
                branch => branch.UseTenantInfrastructure());

            app.UseRouting();
            app.UseResponseCaching();
            app.UseRateLimiter();
            UseAuthentication(app);
            app.UseCors();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();

                endpoints.MapHealthChecks("/health", new HealthCheckOptions
                {
                    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                });
            });
        }

        public virtual void RegisterDbContexts(IServiceCollection services,
            DatabaseMigrationsConfiguration databaseMigration)
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

            AddIdentityDbContext(services, databaseProviderConfiguration.ProviderType, connectionStrings.IdentityDbConnection, migrationsAssembly);

            AddConfiguredDbContext<IdentityServerConfigurationDbContext>(services, databaseProviderConfiguration.ProviderType, connectionStrings.ConfigurationDbConnection, migrationsAssembly);
            AddConfiguredDbContext<IdentityServerPersistedGrantDbContext>(services, databaseProviderConfiguration.ProviderType, connectionStrings.PersistedGrantDbConnection, migrationsAssembly);
            AddConfiguredDbContext<AdminLogDbContext>(services, databaseProviderConfiguration.ProviderType, connectionStrings.AdminLogDbConnection, migrationsAssembly);
            AddConfiguredDbContext<AdminAuditLogDbContext>(services, databaseProviderConfiguration.ProviderType, connectionStrings.AdminAuditLogDbConnection, migrationsAssembly);
            AddConfiguredDbContext<IdentityServerDataProtectionDbContext>(services, databaseProviderConfiguration.ProviderType, connectionStrings.DataProtectionDbConnection, migrationsAssembly);
            AddConfiguredDbContext<AdminConfigurationDbContext>(services, databaseProviderConfiguration.ProviderType, connectionStrings.AdminConfigurationDbConnection, migrationsAssembly);
        }

        private static void AddIdentityDbContext(IServiceCollection services, DatabaseProviderType providerType, string identityConnectionString, string migrationsAssembly)
        {
            services.AddDbContext<AdminIdentityDbContext>(options =>
            {
                switch (providerType)
                {
                    case DatabaseProviderType.SqlServer:
                        options.UseSqlServer(identityConnectionString, b => b.MigrationsAssembly(migrationsAssembly));
                        break;
                    case DatabaseProviderType.PostgreSQL:
                        options.UseNpgsql(identityConnectionString, b => b.MigrationsAssembly(migrationsAssembly));
                        break;
                    case DatabaseProviderType.MySql:
                        options.UseMySQL(NormalizeMySqlConnectionStringForDevelopment(identityConnectionString), b => b.MigrationsAssembly(migrationsAssembly));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(providerType),
                            $@"The value needs to be one of {string.Join(", ", Enum.GetNames(typeof(DatabaseProviderType)))}.");
                }
            });
        }

        private static void AddConfiguredDbContext<TContext>(IServiceCollection services, DatabaseProviderType providerType, string connectionString, string migrationsAssembly)
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
                        options.UseNpgsql(connectionString, b => b.MigrationsAssembly(migrationsAssembly));
                        break;
                    case DatabaseProviderType.MySql:
                        options.UseMySQL(NormalizeMySqlConnectionStringForDevelopment(connectionString), b => b.MigrationsAssembly(migrationsAssembly));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(providerType),
                            $@"The value needs to be one of {string.Join(", ", Enum.GetNames(typeof(DatabaseProviderType)))}.");
                }
            });
        }

        private static string NormalizeMySqlConnectionStringForDevelopment(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return connectionString;

            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var isDevelopment = string.Equals(environment, Environments.Development, StringComparison.OrdinalIgnoreCase);
            if (!isDevelopment)
                return connectionString;

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

        public virtual void RegisterAuthentication(IServiceCollection services)
        {
            services.AddApiAuthentication<AdminIdentityDbContext, UserIdentity, UserIdentityRole>(Configuration);
        }

        public virtual void RegisterAuthorization(IServiceCollection services)
        {
            services.AddAuthorizationPolicies();
        }

        public virtual void UseAuthentication(IApplicationBuilder app)
        {
            app.UseAuthentication();
        }
    }
}
