// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using NetIPNetwork = System.Net.IPNetwork;
using Duende.IdentityServer.EntityFramework.Options;
using IdentityModel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Skoruba.AuditLogging.EntityFramework.DbContexts;
using Skoruba.AuditLogging.EntityFramework.Entities;
using Skoruba.AuditLogging.EntityFramework.Extensions;
using Skoruba.AuditLogging.EntityFramework.Repositories;
using Skoruba.AuditLogging.EntityFramework.Services;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Extensions;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Identity.Dtos.Identity;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Identity.Extensions;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Admin.Storage.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.Configuration;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.PostgreSQL;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.SqlServer;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Helpers;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Repositories;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Repositories.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Extensions;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.ApplicationParts;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.AuditLogging;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.Constants;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.ExceptionHandling;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers.Localization;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Mappers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Resources;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Shared.Configuration.Helpers;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.MySql;
using Microsoft.Extensions.Hosting;
using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers
{
    public static class StartupHelpers
    {
        public static IServiceCollection AddAuditEventLogging<TAuditLoggingDbContext, TAuditLog>(
            this IServiceCollection services, IConfiguration configuration)
            where TAuditLog : AuditLog, new()
            where TAuditLoggingDbContext : IAuditLoggingDbContext<TAuditLog>
        {
            services.AddHttpContextAccessor();

            var auditLoggingConfiguration = configuration.GetSection(nameof(AuditLoggingConfiguration))
                .Get<AuditLoggingConfiguration>();
            services.AddSingleton(auditLoggingConfiguration);

            services.AddAuditLogging(options => { options.Source = auditLoggingConfiguration.Source; })
                .AddEventData<ApiAuditSubject, ApiAuditAction>()
                .AddAuditSinks<DatabaseAuditEventLoggerSink<TAuditLog>>();

            services
                .AddTransient<IAuditLoggingRepository<TAuditLog>,
                    AuditLoggingRepository<TAuditLoggingDbContext, TAuditLog>>();

            services.AddTransient<IAuditLogRepository<TAuditLog>, AuditLogRepository<TAuditLoggingDbContext, TAuditLog>>();
            services.AddTransient<IAuditLogService, AuditLogService<TAuditLog>>();

            return services;
        }

        public static IServiceCollection AddAdminApiCors(this IServiceCollection services, AdminApiConfiguration adminApiConfiguration)
        {
            services.AddCors(options =>
            {
                options.AddDefaultPolicy(
                    builder =>
                    {
                        if (adminApiConfiguration.CorsAllowAnyOrigin)
                        {
                            builder.AllowAnyOrigin();
                        }
                        else
                        {
                            builder.WithOrigins(adminApiConfiguration.CorsAllowOrigins);
                        }

                        builder.AllowAnyHeader();
                        builder.AllowAnyMethod();
                    });
            });

            return services;
        }

        /// <summary>
        /// Register services for MVC
        /// </summary>
        /// <param name="services"></param>
        public static void AddMvcServices<TUserDto, TRoleDto,
            TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
            TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
            TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto>(
            this IServiceCollection services)
            where TUserDto : UserDto<TKey>, new()
            where TRoleDto : RoleDto<TKey>, new()
            where TUser : IdentityUser<TKey>
            where TRole : IdentityRole<TKey>
            where TKey : IEquatable<TKey>
            where TUserClaim : IdentityUserClaim<TKey>
            where TUserRole : IdentityUserRole<TKey>
            where TUserLogin : IdentityUserLogin<TKey>
            where TRoleClaim : IdentityRoleClaim<TKey>
            where TUserToken : IdentityUserToken<TKey>
            where TUsersDto : UsersDto<TUserDto, TKey>
            where TRolesDto : RolesDto<TRoleDto, TKey>
            where TUserRolesDto : UserRolesDto<TRoleDto, TKey>
            where TUserClaimsDto : UserClaimsDto<TUserClaimDto, TKey>
            where TUserProviderDto : UserProviderDto<TKey>
            where TUserProvidersDto : UserProvidersDto<TUserProviderDto, TKey>
            where TUserChangePasswordDto : UserChangePasswordDto<TKey>
            where TRoleClaimsDto : RoleClaimsDto<TRoleClaimDto, TKey>
            where TUserClaimDto : UserClaimDto<TKey>
            where TRoleClaimDto : RoleClaimDto<TKey>
        {
            services.AddLocalization(opts => { opts.ResourcesPath = ConfigurationConsts.ResourcesPath; });

            services.TryAddTransient(typeof(IGenericControllerLocalizer<>), typeof(GenericControllerLocalizer<>));

            services.AddControllersWithViews(o => { o.Conventions.Add(new GenericControllerRouteConvention()); })
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                })
                .AddDataAnnotationsLocalization()
                .ConfigureApplicationPartManager(m =>
                {
                    m.FeatureProviders.Add(
                        new GenericTypeControllerFeatureProvider<TUserDto, TRoleDto,
                            TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
                            TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
                            TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto>());
                });
        }

        /// <summary>
        /// Register DbContexts for IdentityServer ConfigurationStore and PersistedGrants, Identity and Logging
        /// Configure the connection strings in AppSettings.json
        /// </summary>
        /// <typeparam name="TConfigurationDbContext"></typeparam>
        /// <typeparam name="TPersistedGrantDbContext"></typeparam>
        /// <typeparam name="TLogDbContext"></typeparam>
        /// <typeparam name="TIdentityDbContext"></typeparam>
        /// <typeparam name="TAuditLoggingDbContext"></typeparam>
        /// <typeparam name="TDataProtectionDbContext"></typeparam>
        /// <typeparam name="TAuditLog"></typeparam>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <param name="databaseMigrationsConfiguration"></param>
        public static void AddDbContexts<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext,
            TLogDbContext, TAuditLoggingDbContext, TDataProtectionDbContext, TAdminConfigurationDbContext, TAuditLog>(this IServiceCollection services, IConfiguration configuration, DatabaseMigrationsConfiguration databaseMigrationsConfiguration)
            where TIdentityDbContext : DbContext
            where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
            where TConfigurationDbContext : DbContext, IAdminConfigurationDbContext
            where TLogDbContext : DbContext, IAdminLogDbContext
            where TAuditLoggingDbContext : DbContext, IAuditLoggingDbContext<TAuditLog>
            where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
            where TAdminConfigurationDbContext : DbContext, IAdminConfigurationStoreDbContext
            where TAuditLog : AuditLog
        {
            var databaseProvider = configuration.GetSection(nameof(DatabaseProviderConfiguration)).Get<DatabaseProviderConfiguration>();
            var connectionStrings = configuration.GetSection("ConnectionStrings").Get<ConnectionStringsConfiguration>();

            switch (databaseProvider.ProviderType)
            {
                case DatabaseProviderType.SqlServer:
                    services.RegisterSqlServerDbContexts<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TLogDbContext, TAuditLoggingDbContext, TDataProtectionDbContext, TAdminConfigurationDbContext, TAuditLog>(connectionStrings, databaseMigrationsConfiguration);
                    break;
                case DatabaseProviderType.PostgreSQL:
                    services.RegisterNpgSqlDbContexts<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TLogDbContext, TAuditLoggingDbContext, TDataProtectionDbContext, TAdminConfigurationDbContext, TAuditLog>(connectionStrings, databaseMigrationsConfiguration);
                    break;
                case DatabaseProviderType.MySql:
                    services.RegisterMySqlDbContexts<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TLogDbContext, TAuditLoggingDbContext, TDataProtectionDbContext, TAdminConfigurationDbContext, TAuditLog>(connectionStrings, databaseMigrationsConfiguration);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(databaseProvider.ProviderType), $@"The value needs to be one of {string.Join(", ", Enum.GetNames(typeof(DatabaseProviderType)))}.");
            }

        }

        /// <summary>
        /// Add authentication middleware for an API
        /// </summary>
        /// <typeparam name="TIdentityDbContext">DbContext for an access to Identity</typeparam>
        /// <typeparam name="TUser">Entity with User</typeparam>
        /// <typeparam name="TRole">Entity with Role</typeparam>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        public static void AddApiAuthentication<TIdentityDbContext, TUser, TRole>(this IServiceCollection services,
            IConfiguration configuration)
            where TIdentityDbContext : DbContext
            where TRole : class
            where TUser : class
        {
            var adminApiConfiguration = configuration.GetSection(nameof(AdminApiConfiguration)).Get<AdminApiConfiguration>();

            services.AddIdentityCore<TUser>(options => configuration.GetSection(nameof(IdentityOptions)).Bind(options))
                .AddRoles<TRole>()
                .AddEntityFrameworkStores<TIdentityDbContext>()
                .AddDefaultTokenProviders();

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = adminApiConfiguration.IdentityServerBaseUrl;
                    options.RequireHttpsMetadata = adminApiConfiguration.RequireHttpsMetadata;
                    options.Audience = adminApiConfiguration.OidcApiName;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        NameClaimType = JwtClaimTypes.Name,
                        RoleClaimType = JwtClaimTypes.Role
                    };

                    var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                    var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
                    if (isDevelopment && IsLocalDevelopmentHttpsUri(options.Authority))
                    {
                        options.BackchannelHttpHandler = new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                        };
                    }
                });
        }

        /// <summary>
        /// Register in memory DbContexts for IdentityServer ConfigurationStore and PersistedGrants, Identity and Logging
        /// For testing purpose only
        /// </summary>
        /// <typeparam name="TConfigurationDbContext"></typeparam>
        /// <typeparam name="TPersistedGrantDbContext"></typeparam>
        /// <typeparam name="TLogDbContext"></typeparam>
        /// <typeparam name="TIdentityDbContext"></typeparam>
        /// <typeparam name="TAuditLoggingDbContext"></typeparam>
        /// <typeparam name="TDataProtectionDbContext"></typeparam>
        /// <param name="services"></param>
        public static void RegisterDbContextsStaging<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TLogDbContext, TAuditLoggingDbContext, TDataProtectionDbContext>(this IServiceCollection services)
            where TIdentityDbContext : DbContext
            where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
            where TConfigurationDbContext : DbContext, IAdminConfigurationDbContext
            where TLogDbContext : DbContext, IAdminLogDbContext
            where TAuditLoggingDbContext : DbContext, IAuditLoggingDbContext<AuditLog>
            where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
        {
            var persistedGrantsDatabaseName = Guid.NewGuid().ToString();
            var configurationDatabaseName = Guid.NewGuid().ToString();
            var logDatabaseName = Guid.NewGuid().ToString();
            var identityDatabaseName = Guid.NewGuid().ToString();
            var auditLoggingDatabaseName = Guid.NewGuid().ToString();
            var dataProtectionDatabaseName = Guid.NewGuid().ToString();

            var operationalStoreOptions = new OperationalStoreOptions();
            services.AddSingleton(operationalStoreOptions);

            var storeOptions = new ConfigurationStoreOptions();
            services.AddSingleton(storeOptions);

            services.AddDbContext<TIdentityDbContext>(optionsBuilder => optionsBuilder.UseInMemoryDatabase(identityDatabaseName));
            services.AddDbContext<TPersistedGrantDbContext>(optionsBuilder => optionsBuilder.UseInMemoryDatabase(persistedGrantsDatabaseName));
            services.AddDbContext<TConfigurationDbContext>(optionsBuilder => optionsBuilder.UseInMemoryDatabase(configurationDatabaseName));
            services.AddDbContext<TLogDbContext>(optionsBuilder => optionsBuilder.UseInMemoryDatabase(logDatabaseName));
            services.AddDbContext<TAuditLoggingDbContext>(optionsBuilder => optionsBuilder.UseInMemoryDatabase(auditLoggingDatabaseName));
            services.AddDbContext<TDataProtectionDbContext>(optionsBuilder => optionsBuilder.UseInMemoryDatabase(dataProtectionDatabaseName));
        }

        public static void AddAuthorizationPolicies(this IServiceCollection services)
        {
            var adminApiConfiguration = services.BuildServiceProvider().GetService<AdminApiConfiguration>();

            services.AddAuthorization(options =>
            {
                options.AddPolicy(AuthorizationConsts.AdministrationPolicy,
                    policy =>
                        policy.RequireAssertion(context =>
                        {
                            var hasScope = context.User.HasClaim(c =>
                                c.Type == JwtClaimTypes.Scope && c.Value == adminApiConfiguration.OidcApiName);

                            if (!hasScope) return false;

                            var hasSuperAdminRole = context.User.HasClaim(c =>
                                ((c.Type == JwtClaimTypes.Role || c.Type == ClaimTypes.Role) && c.Value == adminApiConfiguration.AdministrationRole) ||
                                (c.Type == $"client_{JwtClaimTypes.Role}" && c.Value == adminApiConfiguration.AdministrationRole));

                            var hasTenantAdminRole = !string.IsNullOrWhiteSpace(adminApiConfiguration.TenantAdminRole) &&
                                context.User.HasClaim(c =>
                                    ((c.Type == JwtClaimTypes.Role || c.Type == ClaimTypes.Role) && c.Value == adminApiConfiguration.TenantAdminRole) ||
                                    (c.Type == $"client_{JwtClaimTypes.Role}" && c.Value == adminApiConfiguration.TenantAdminRole));

                            var httpContext = context.Resource switch
                            {
                                Microsoft.AspNetCore.Mvc.Filters.AuthorizationFilterContext mvcContext => mvcContext.HttpContext,
                                Microsoft.AspNetCore.Http.HttpContext directContext => directContext,
                                _ => null
                            };

                            var tenantAccessor = httpContext?.RequestServices.GetService<TenantInfrastructure.Abstractions.ITenantContextAccessor>();
                            var hasTenant = tenantAccessor?.Current != null;

                            if (!hasTenant) return hasSuperAdminRole;

                            if (hasSuperAdminRole) return true;

                            if (!hasTenantAdminRole) return false;

                            var tenantKeyClaim = context.User.FindFirst(TenantClaimTypes.TenantKey)?.Value;
                            if (string.IsNullOrWhiteSpace(tenantKeyClaim)) return false;

                            return string.Equals(tenantKeyClaim, tenantAccessor!.Current!.TenantKey, StringComparison.OrdinalIgnoreCase);
                        }));

                options.AddPolicy(AuthorizationConsts.SuperAdminPolicy,
                    policy =>
                        policy.RequireAssertion(context =>
                            context.User.HasClaim(c =>
                                (((c.Type == JwtClaimTypes.Role || c.Type == ClaimTypes.Role) && c.Value == adminApiConfiguration.AdministrationRole) ||
                                 (c.Type == $"client_{JwtClaimTypes.Role}" && c.Value == adminApiConfiguration.AdministrationRole))
                            ) && context.User.HasClaim(c => c.Type == JwtClaimTypes.Scope && c.Value == adminApiConfiguration.OidcApiName)
                        ));
            });
        }

        public static void AddIdSHealthChecks<TConfigurationDbContext, TPersistedGrantDbContext, TIdentityDbContext, TLogDbContext, TAuditLoggingDbContext, TDataProtectionDbContext>(this IServiceCollection services, IConfiguration configuration, AdminApiConfiguration adminApiConfiguration)
            where TConfigurationDbContext : DbContext, IAdminConfigurationDbContext
            where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
            where TIdentityDbContext : DbContext
            where TLogDbContext : DbContext, IAdminLogDbContext
            where TAuditLoggingDbContext : DbContext, IAuditLoggingDbContext<AuditLog>
            where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
        {
            var configurationDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.ConfigurationDbConnectionStringKey);
            var persistedGrantsDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.PersistedGrantDbConnectionStringKey);
            var identityDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.IdentityDbConnectionStringKey);
            var logDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.AdminLogDbConnectionStringKey);
            var auditLogDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.AdminAuditLogDbConnectionStringKey);
            var dataProtectionDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.DataProtectionDbConnectionStringKey);

            var identityServerUri = adminApiConfiguration.IdentityServerBaseUrl;
            var healthChecksBuilder = services.AddHealthChecks()
                .AddDbContextCheck<TConfigurationDbContext>("ConfigurationDbContext")
                .AddDbContextCheck<TPersistedGrantDbContext>("PersistedGrantsDbContext")
                .AddDbContextCheck<TIdentityDbContext>("IdentityDbContext")
                .AddDbContextCheck<TLogDbContext>("LogDbContext")
                .AddDbContextCheck<TAuditLoggingDbContext>("AuditLogDbContext")
                .AddDbContextCheck<TDataProtectionDbContext>("DataProtectionDbContext");

            if (adminApiConfiguration.EnableIdentityServerHealthCheck &&
                !string.IsNullOrWhiteSpace(identityServerUri))
            {
                healthChecksBuilder.AddOpenIdConnectServer(oidcSvrUri: new Uri(identityServerUri), name: "Identity Server");
            }

            var serviceProvider = services.BuildServiceProvider();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            using (var scope = scopeFactory.CreateScope())
            {
                var configurationTableName = DbContextHelpers.GetEntityTable<TConfigurationDbContext>(scope.ServiceProvider);
                var persistedGrantTableName = DbContextHelpers.GetEntityTable<TPersistedGrantDbContext>(scope.ServiceProvider);
                var identityTableName = DbContextHelpers.GetEntityTable<TIdentityDbContext>(scope.ServiceProvider);
                var logTableName = DbContextHelpers.GetEntityTable<TLogDbContext>(scope.ServiceProvider);
                var auditLogTableName = DbContextHelpers.GetEntityTable<TAuditLoggingDbContext>(scope.ServiceProvider);
                var dataProtectionTableName = DbContextHelpers.GetEntityTable<TDataProtectionDbContext>(scope.ServiceProvider);

                var databaseProvider = configuration.GetSection(nameof(DatabaseProviderConfiguration)).Get<DatabaseProviderConfiguration>();
                switch (databaseProvider.ProviderType)
                {
                    case DatabaseProviderType.SqlServer:
                        healthChecksBuilder
                            .AddSqlServer(configurationDbConnectionString, name: "ConfigurationDb",
                                healthQuery: $"SELECT TOP 1 * FROM dbo.[{configurationTableName}]")
                            .AddSqlServer(persistedGrantsDbConnectionString, name: "PersistentGrantsDb",
                                healthQuery: $"SELECT TOP 1 * FROM dbo.[{persistedGrantTableName}]")
                            .AddSqlServer(identityDbConnectionString, name: "IdentityDb",
                                healthQuery: $"SELECT TOP 1 * FROM dbo.[{identityTableName}]")
                            .AddSqlServer(logDbConnectionString, name: "LogDb",
                                healthQuery: $"SELECT TOP 1 * FROM dbo.[{logTableName}]")
                            .AddSqlServer(auditLogDbConnectionString, name: "AuditLogDb",
                                healthQuery: $"SELECT TOP 1 * FROM dbo.[{auditLogTableName}]")
                            .AddSqlServer(dataProtectionDbConnectionString, name: "DataProtectionDb",
                            healthQuery: $"SELECT TOP 1 * FROM dbo.[{dataProtectionTableName}]");
                        break;
                    case DatabaseProviderType.PostgreSQL:
                        healthChecksBuilder
                            .AddNpgSql(configurationDbConnectionString, name: "ConfigurationDb",
                                healthQuery: $"SELECT * FROM \"{configurationTableName}\" LIMIT 1")
                            .AddNpgSql(persistedGrantsDbConnectionString, name: "PersistentGrantsDb",
                                healthQuery: $"SELECT * FROM \"{persistedGrantTableName}\" LIMIT 1")
                            .AddNpgSql(identityDbConnectionString, name: "IdentityDb",
                                healthQuery: $"SELECT * FROM \"{identityTableName}\" LIMIT 1")
                            .AddNpgSql(logDbConnectionString, name: "LogDb",
                                healthQuery: $"SELECT * FROM \"{logTableName}\" LIMIT 1")
                            .AddNpgSql(auditLogDbConnectionString, name: "AuditLogDb",
                                healthQuery: $"SELECT * FROM \"{auditLogTableName}\"  LIMIT 1")
                            .AddNpgSql(dataProtectionDbConnectionString, name: "DataProtectionDb",
                                healthQuery: $"SELECT * FROM \"{dataProtectionTableName}\"  LIMIT 1");
                        break;
                    case DatabaseProviderType.MySql:
                        configurationDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(configurationDbConnectionString);
                        persistedGrantsDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(persistedGrantsDbConnectionString);
                        identityDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(identityDbConnectionString);
                        logDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(logDbConnectionString);
                        auditLogDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(auditLogDbConnectionString);
                        dataProtectionDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(dataProtectionDbConnectionString);

                        healthChecksBuilder
                            .AddMySql(configurationDbConnectionString, name: "ConfigurationDb",
                                healthQuery: $"SELECT 1 FROM \"{configurationTableName}\" ")
                            .AddMySql(persistedGrantsDbConnectionString, name: "PersistentGrantsDb",
                                healthQuery: $"SELECT 1 FROM \"{persistedGrantTableName}\" ")
                            .AddMySql(identityDbConnectionString, name: "IdentityDb",
                                healthQuery: $"SELECT 1 FROM \"{identityTableName}\" ")
                            .AddMySql(logDbConnectionString, name: "LogDb",
                                healthQuery: $"SELECT 1 FROM \"{logTableName}\" ")
                            .AddMySql(auditLogDbConnectionString, name: "AuditLogDb",
                                healthQuery: $"SELECT 1 FROM \"{auditLogTableName}\" ")
                            .AddMySql(dataProtectionDbConnectionString, name: "DataProtectionDb",
                                healthQuery: $"SELECT 1 FROM \"{dataProtectionTableName}\" ");
                        break;
                    default:
                        throw new NotImplementedException($"Health checks not defined for database provider {databaseProvider.ProviderType}");
                }
            }
        }

        private static string NormalizeMySqlConnectionStringForDevelopment(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return connectionString;

            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
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

        public static void AddForwardHeaders(this IApplicationBuilder app, IConfiguration configuration)
        {
            var forwardedHeadersConfig = configuration.GetSection("ForwardedHeadersConfiguration")
                .Get<Skoruba.Duende.IdentityServer.Shared.Configuration.Configuration.ForwardedHeadersConfiguration>()
                ?? new Skoruba.Duende.IdentityServer.Shared.Configuration.Configuration.ForwardedHeadersConfiguration();

            if (forwardedHeadersConfig.Enabled)
            {
                var forwardingOptions = new ForwardedHeadersOptions()
                {
                    ForwardedHeaders = ForwardedHeaders.All,
                    ForwardLimit = forwardedHeadersConfig.ForwardLimit
                };

                if (forwardedHeadersConfig.AllowAll)
                {
                    // Development mode: allow all proxies and networks (insecure)
                    forwardingOptions.KnownIPNetworks.Clear();
                    forwardingOptions.KnownProxies.Clear();
                }
                else
                {
                    // Production mode: only trust configured proxies and networks
                    if (forwardedHeadersConfig.KnownProxies != null && forwardedHeadersConfig.KnownProxies.Count > 0)
                    {
                        forwardingOptions.KnownProxies.Clear();
                        foreach (var proxy in forwardedHeadersConfig.KnownProxies)
                        {
                            if (System.Net.IPAddress.TryParse(proxy, out var ipAddress))
                            {
                                forwardingOptions.KnownProxies.Add(ipAddress);
                            }
                        }
                    }

                    if (forwardedHeadersConfig.KnownNetworks != null && forwardedHeadersConfig.KnownNetworks.Count > 0)
                    {
                        forwardingOptions.KnownIPNetworks.Clear();
                        foreach (var network in forwardedHeadersConfig.KnownNetworks)
                        {
                            var parts = network.Split('/');
                            if (parts.Length == 2 &&
                                IPAddress.TryParse(parts[0], out var prefix) &&
                                int.TryParse(parts[1], out var prefixLength))
                            {
                                forwardingOptions.KnownIPNetworks.Add(new NetIPNetwork(prefix, prefixLength));
                            }
                        }
                    }

                    // If no proxies or networks configured, don't clear defaults (more secure)
                    // This means it will only trust the loopback by default
                }

                app.UseForwardedHeaders(forwardingOptions);
            }
        }

        public static void AddIdentityServerAdminApi<TIdentityDbContext, TIdentityServerConfigurationDbContext, TPersistedGrantDbContext, TIdentityServerDataProtectionDbContext, TAdminLogDbContext, TAdminAuditLogDbContext, TAdminConfigurationDbContext, TAuditLog, TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
            TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
            TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto>(this IServiceCollection services, IConfiguration configuration, AdminApiConfiguration adminApiConfiguration)
            where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
            where TIdentityDbContext : IdentityDbContext<TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken>
            where TUserDto : UserDto<TKey>, new()
            where TRoleDto : RoleDto<TKey>, new()
            where TUser : IdentityUser<TKey>
            where TRole : IdentityRole<TKey>
            where TKey : IEquatable<TKey>
            where TUserClaim : IdentityUserClaim<TKey>
            where TUserRole : IdentityUserRole<TKey>
            where TUserLogin : IdentityUserLogin<TKey>
            where TRoleClaim : IdentityRoleClaim<TKey>
            where TUserToken : IdentityUserToken<TKey>
            where TUsersDto : UsersDto<TUserDto, TKey>
            where TRolesDto : RolesDto<TRoleDto, TKey>
            where TUserRolesDto : UserRolesDto<TRoleDto, TKey>
            where TUserClaimsDto : UserClaimsDto<TUserClaimDto, TKey>
            where TUserProviderDto : UserProviderDto<TKey>
            where TUserProvidersDto : UserProvidersDto<TUserProviderDto, TKey>
            where TUserChangePasswordDto : UserChangePasswordDto<TKey>
            where TRoleClaimsDto : RoleClaimsDto<TRoleClaimDto, TKey>
            where TUserClaimDto : UserClaimDto<TKey>
            where TRoleClaimDto : RoleClaimDto<TKey>
            where TIdentityServerDataProtectionDbContext : DbContext, IDataProtectionKeyContext
            where TIdentityServerConfigurationDbContext : DbContext, IAdminConfigurationDbContext
            where TAdminLogDbContext : DbContext, IAdminLogDbContext
            where TAdminConfigurationDbContext : DbContext, IAdminConfigurationStoreDbContext
            where TAdminAuditLogDbContext : IAuditLoggingDbContext<AuditLog>, IAuditLoggingDbContext<TAuditLog>
            where TAuditLog : AuditLog, new()
        {
            services.AddSingleton(configuration.GetSection(nameof(IdentityServerData))
                .Get<IdentityServerData>());

            services.AddSingleton(configuration.GetSection(nameof(IdentityData))
                .Get<IdentityData>());

            services.AddDataProtection<TIdentityServerDataProtectionDbContext>(configuration);

            services.AddScoped<ControllerExceptionFilterAttribute>();
            services.AddScoped<IApiErrorResources, ApiErrorResources>();
            services.AddSingleton<ITenantRoleProvider, TenantRoleProvider>();
            services.AddScoped<IClientScopeCacheService, ClientScopeCacheService>();
            services.AddScoped<IUserThemePreferenceService, UserThemePreferenceService>();

            var profileTypes = new HashSet<Type>
            {
                typeof(IdentityMapperProfile<TRoleDto, TUserRolesDto, TKey, TUserClaimsDto, TUserClaimDto, TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimDto, TRoleClaimsDto>)
            };

            services.AddConfigureAdminAspNetIdentitySchema(configuration);

            services.AddAdminAspNetIdentityServices<TIdentityDbContext, TPersistedGrantDbContext,
                TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
                TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
                TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto>(profileTypes);

            services.AddAdminServices<TIdentityServerConfigurationDbContext, TPersistedGrantDbContext, TAdminLogDbContext, TAdminConfigurationDbContext>();

            services.AddAdminApiCors(adminApiConfiguration);

            services.AddMvcServices<TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
                TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
                TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto>();

            services.AddAuditEventLogging<TAdminAuditLogDbContext, TAuditLog>(configuration);
        }

        public static string GetInformationalVersion(this Type typeInAssembly)
        {
            ArgumentNullException.ThrowIfNull(typeInAssembly);

            return typeInAssembly.Assembly.GetName().Version?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Binds the <c>TenantClientCache</c> configuration section to
        /// <see cref="TenantClientCacheOptions"/>, registers the fail-fast
        /// <see cref="TenantClientCacheOptionsValidator"/>, registers the
        /// per-tenant Duende Client snapshot cache services, and conditionally
        /// registers the periodic refresh hosted service when
        /// <c>TenantClientCache:Enabled</c> is <c>true</c> (R1.7, R1.8, R8.1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The hosted-service registration is gated on the bound options value
        /// read directly from <paramref name="configuration"/>; no
        /// intermediate <see cref="ServiceCollection.BuildServiceProvider()"/>
        /// is constructed to avoid the well-known anti-pattern of materializing
        /// a throwaway provider during DI registration.
        /// </para>
        /// <para>
        /// Caller contract: invoke this AFTER <c>AddTenantInfrastructure</c> so
        /// that <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
        /// is already registered. Existing legacy registrations
        /// (<c>IClientScopeCacheService</c> etc.) are NOT touched here.
        /// </para>
        /// </remarks>
        public static IServiceCollection RegisterTenantClientCache(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services
                .AddOptions<TenantClientCacheOptions>()
                .Bind(configuration.GetSection(TenantClientCacheOptions.SectionName))
                .ValidateOnStart();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<TenantClientCacheOptions>, TenantClientCacheOptionsValidator>());

            // Cache infrastructure services.
            //   * Metrics meter is a process-wide singleton.
            //   * The cache service holds no per-request state and depends only
            //     on singleton infrastructure (IDistributedCache, IOptionsMonitor,
            //     ILogger, Meter), so Singleton is correct.
            //   * The scope resolver depends on IClientService (Scoped) so MUST
            //     itself be Scoped (R11.7).
            services.TryAddSingleton<TenantClientCacheMetrics>();
            services.TryAddSingleton<ITenantClientCacheService, TenantClientCacheService>();
            services.TryAddScoped<IClientTenantScopeResolver, ClientTenantScopeResolver>();

            // Conditional hosted service registration. We deliberately read the
            // bound options DIRECTLY from IConfiguration here rather than
            // calling services.BuildServiceProvider() — that would materialize
            // a throwaway provider mid-registration (anti-pattern: validators
            // run twice, singletons get duplicated). Reading the section is
            // safe because the same binding logic above feeds the runtime
            // IOptions<T> chain.
            //
            // R1.8 / R8.1: when Enabled == false, the BackgroundService is NOT
            // registered. Production operators flip Enabled via env var
            // (TenantClientCache__Enabled=false) without redeploying. The
            // refresh service itself is also a no-op when Enabled flips false
            // at runtime (defence in depth), but skipping registration entirely
            // means no hosted-service overhead.
            var bound = configuration
                .GetSection(TenantClientCacheOptions.SectionName)
                .Get<TenantClientCacheOptions>() ?? new TenantClientCacheOptions();

            if (bound.Enabled)
            {
                services.AddHostedService<TenantClientCacheRefreshService>();
            }

            return services;
        }

        /// <summary>
        /// Full DI registration for the public-read endpoint feature
        /// (<c>tenant-client-cache-public-read</c>, Task 6).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Binds the <c>TenantClientCachePublicRead</c> configuration section
        /// to <see cref="TenantClientCachePublicReadOptions"/>, registers the
        /// fail-fast <see cref="TenantClientCachePublicReadOptionsValidator"/>,
        /// and arms <c>ValidateOnStart()</c> so misconfiguration crashes the
        /// host on launch (R1.1, R1.4, R1.5, R4.3, R4.4, R5.6, R5.7, R6.2,
        /// R9.6, R17.1).
        /// </para>
        /// <para>
        /// Adds the public-read pipeline collaborators as singletons:
        /// <see cref="ITenantApiKeyValidator"/> →
        /// <see cref="TenantApiKeyValidator"/>,
        /// <see cref="HttpsRequiredFilter"/>,
        /// <see cref="TenantApiKeyAuthorizationFilter"/>,
        /// <see cref="PublicReadExceptionFilter"/>,
        /// <see cref="IpHashHelper"/>. Filter lifetimes are
        /// <c>Singleton</c> because the filters hold no per-request state and
        /// resolve only singleton collaborators (the
        /// <c>ILogger&lt;T&gt;</c> framework instances are themselves
        /// singletons; <see cref="IOptionsMonitor{TOptions}"/> is by
        /// definition singleton).
        /// </para>
        /// <para>
        /// Registers the CORS policy <c>"TenantClientCachePublicRead"</c>
        /// with the strict allowlist semantics described in the design
        /// document (R5.1 – R5.8): zero default origins (R5.4), only
        /// <c>GET / HEAD / OPTIONS</c> methods (R5.2), only
        /// <c>X-Tenant-Api-Key, If-None-Match, Accept</c> request headers
        /// (R5.2), <c>ETag, Cache-Control</c> exposed (R5.8), credentials
        /// disallowed (R5.3), preflight cache from
        /// <c>Cors.PreflightMaxAgeSeconds</c> (R5.7).
        /// </para>
        /// <para>
        /// Registers the rate limiter policy
        /// <c>"TenantClientCachePublicRead"</c> with a token-bucket
        /// partition keyed by the URL-bound <c>tenantKey</c> (normalized
        /// via <c>Trim().ToLowerInvariant()</c> per R4.6). Bucket
        /// parameters come from <c>RateLimit:*</c> (R4.2). On rejection
        /// the handler writes the canonical 429 body
        /// <c>{"error":"rate_limit_exceeded"}</c> with a <c>Retry-After</c>
        /// header read from <see cref="MetadataName.RetryAfter"/> and
        /// fallback <c>1</c> (R4.5), increments
        /// <see cref="TenantClientCacheMetrics.PublicReadRateLimited(string, double)"/>
        /// tagged with <paramref name="tenantKey"/> (R4.8), and emits a
        /// Warning audit via
        /// <see cref="AuditEventPublicRead.EmitRateLimited(ILogger, AuditFields)"/>.
        /// </para>
        /// <para>
        /// Service registration for <c>ITenantClientCacheService</c> is
        /// OWNED by the <c>tenant-client-cache-expansion</c> spec (its
        /// Task 11). This extension assumes
        /// <see cref="RegisterTenantClientCache"/> has already been called
        /// before <c>AddTenantClientCachePublicRead</c> resolves the cache
        /// service via DI (caller responsibility — see Task 11 of this
        /// spec).
        /// </para>
        /// <para>
        /// Idempotent: every collaborator is registered via the
        /// <c>TryAdd*</c> family so that two calls to this extension never
        /// produce duplicate descriptors. The CORS and rate-limiter
        /// policies are registered on <see cref="CorsOptions"/> and
        /// <see cref="RateLimiterOptions"/> respectively which by design
        /// re-add the policy on duplicate calls — see the test
        /// <c>StartupHelpersAddTenantClientCachePublicReadTests</c> for the
        /// observed contract.
        /// </para>
        /// </remarks>
        public static IServiceCollection AddTenantClientCachePublicRead(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            // ----- Options binding (Task 1 foundation, retained verbatim) ----
            services
                .AddOptions<TenantClientCachePublicReadOptions>()
                .Bind(configuration.GetSection(TenantClientCachePublicReadOptions.SectionName))
                .ValidateOnStart();

            // R1.9: register validator via TryAddEnumerable so callers
            // invoking this extension twice produce a single registration
            // (idempotent).
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IValidateOptions<TenantClientCachePublicReadOptions>,
                    TenantClientCachePublicReadOptionsValidator>());

            // ----- Pipeline collaborators (Task 6) ---------------------------
            // Filters and the API-key validator are stateless singletons —
            // they resolve only singleton dependencies (IOptionsMonitor,
            // framework ILogger<T>, TenantClientCacheMetrics). Singleton
            // lifetime keeps the per-request allocation budget at zero.
            services.TryAddSingleton<ITenantApiKeyValidator, TenantApiKeyValidator>();
            services.TryAddSingleton<HttpsRequiredFilter>();
            services.TryAddSingleton<TenantApiKeyAuthorizationFilter>();
            services.TryAddSingleton<PublicReadExceptionFilter>();
            services.TryAddSingleton<IpHashHelper>();

            // ----- R1.8 single-shot startup logger ---------------------------
            // Registered via TryAddEnumerable so a second invocation of this
            // extension never duplicates the startup log entry. The hosted
            // service emits a single Information event on host start with
            // tenant count + bound RateLimit / Cors / ResponseCache values
            // (no API key plaintext or hash).
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, PublicReadStartupLogger>());

            // ----- CORS policy "TenantClientCachePublicRead" (R5) ------------
            // We intentionally compose the policy via a callback that resolves
            // IOptions<TenantClientCachePublicReadOptions> at policy-build
            // time. Origins come from the bound options snapshot (config
            // reload mid-process is rare for CORS; the policy provider caches
            // policies for the lifetime of the host).
            services.AddCors(options =>
            {
                options.AddPolicy(
                    PublicReadCorsPolicyName,
                    BuildPublicReadCorsPolicy(configuration));
            });

            // ----- Rate limiter policy "TenantClientCachePublicRead" (R4) ----
            services.AddRateLimiter(rateLimiterOptions =>
            {
                rateLimiterOptions.AddPolicy(
                    PublicReadRateLimiterPolicyName,
                    httpContext => BuildRateLimitPartition(httpContext));

                // R4.5: 429 response shape.
                rateLimiterOptions.OnRejected = OnRateLimitRejected;
            });

            return services;
        }

        /// <summary>
        /// Canonical name of the CORS policy registered by
        /// <see cref="AddTenantClientCachePublicRead"/>. Pinned because
        /// <c>PublicTenantClientsController</c> references it via
        /// <c>[EnableCors("TenantClientCachePublicRead")]</c>.
        /// </summary>
        public const string PublicReadCorsPolicyName = "TenantClientCachePublicRead";

        /// <summary>
        /// Canonical name of the rate-limiter policy registered by
        /// <see cref="AddTenantClientCachePublicRead"/>. Pinned because
        /// <c>PublicTenantClientsController</c> references it via
        /// <c>[EnableRateLimiting("TenantClientCachePublicRead")]</c>.
        /// </summary>
        public const string PublicReadRateLimiterPolicyName = "TenantClientCachePublicRead";

        /// <summary>
        /// Compose the CORS policy for the public-read endpoint per R5.1–R5.8.
        /// Origins are loaded from the configuration snapshot at policy-build
        /// time; an empty allowlist means zero origins (R5.4 fail-closed).
        /// </summary>
        private static Action<CorsPolicyBuilder> BuildPublicReadCorsPolicy(IConfiguration configuration)
        {
            return policy =>
            {
                var corsConfig = configuration
                    .GetSection(TenantClientCachePublicReadOptions.SectionName)
                    .Get<TenantClientCachePublicReadOptions>()?.Cors
                    ?? new TenantClientCachePublicReadOptions.CorsOptions();

                // R5.1 + R5.4: bind origins. Calling .WithOrigins() with zero
                // arguments yields a policy that allows zero origins, which
                // the browser CORS protocol rejects.
                if (corsConfig.AllowedOrigins.Count == 0)
                {
                    policy.WithOrigins(Array.Empty<string>());
                }
                else
                {
                    policy.WithOrigins(corsConfig.AllowedOrigins.ToArray());
                }

                policy
                    // R5.2: only safe verbs.
                    .WithMethods("GET", "HEAD", "OPTIONS")
                    // R5.2: explicit request-header allowlist. Cookie /
                    // Authorization deliberately omitted.
                    .WithHeaders("X-Tenant-Api-Key", "If-None-Match", "Accept")
                    // R5.8: expose ETag + Cache-Control so JS callers can
                    // read them for If-None-Match follow-ups.
                    .WithExposedHeaders("ETag", "Cache-Control")
                    // R5.3: no credentials. The API key is the only
                    // credential surface and travels in a header.
                    .DisallowCredentials()
                    // R5.7: preflight cache.
                    .SetPreflightMaxAge(TimeSpan.FromSeconds(corsConfig.PreflightMaxAgeSeconds));
            };
        }

        /// <summary>
        /// Build the rate-limit partition for the public-read endpoint per
        /// R4.1 / R4.6. Partition key is the path-bound <c>tenantKey</c>
        /// normalized via <c>Trim().ToLowerInvariant()</c>; an empty key
        /// (e.g. when route binding has not run) maps to a no-limit
        /// partition because path-validation will reject the request
        /// downstream with HTTP 400.
        /// </summary>
        /// <remarks>
        /// Exposed as <c>internal</c> (instead of <c>private</c>) solely
        /// so the property-based <c>RateLimitProperties</c> harness in
        /// the unit-test project can drive the EXACT same factory the
        /// host wires up. The framework wraps user delegates in an
        /// internal <c>DefaultKeyType</c> registry so reflection-based
        /// extraction of the registered policy is brittle across
        /// versions; calling this method directly keeps the production
        /// path and the test path byte-identical.
        /// </remarks>
        internal static RateLimitPartition<string> BuildRateLimitPartition(HttpContext httpContext)
        {
            var rawTenantKey = httpContext.Request.RouteValues.TryGetValue("tenantKey", out var routeValue)
                ? routeValue as string
                : null;

            var tenantKey = (rawTenantKey ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(tenantKey))
            {
                // No tenantKey on the route — defer rate limiting; the
                // controller's path validator returns 400. The limiter never
                // throws on an unknown partition.
                return RateLimitPartition.GetNoLimiter("__noop__");
            }

            var rateLimitConfig = httpContext.RequestServices
                .GetRequiredService<IOptionsMonitor<TenantClientCachePublicReadOptions>>()
                .CurrentValue.RateLimit;

            return RateLimitPartition.GetTokenBucketLimiter(
                tenantKey,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = rateLimitConfig.TokenLimit,
                    TokensPerPeriod = rateLimitConfig.TokensPerPeriod,
                    ReplenishmentPeriod = rateLimitConfig.ReplenishmentPeriod,
                    QueueLimit = rateLimitConfig.QueueLimit,
                    AutoReplenishment = rateLimitConfig.AutoReplenishment,
                });
        }

        /// <summary>
        /// 429 rejection handler. Writes the canonical body
        /// <c>{"error":"rate_limit_exceeded"}</c>, sets
        /// <c>Retry-After</c> from the lease metadata (fallback 1 per
        /// R4.5), increments the per-tenant
        /// <see cref="TenantClientCacheMetrics.PublicReadRateLimited(string, double)"/>
        /// counter (R4.8), and emits a Warning audit event so dashboards
        /// can correlate spikes with tenant-key partitions.
        /// </summary>
        private static async ValueTask OnRateLimitRejected(OnRejectedContext context, CancellationToken cancellationToken)
        {
            // R4.5: derive Retry-After from lease metadata where the
            // limiter exposes a TimeUntilNextReplenishment hint; fallback
            // to 1 second otherwise.
            var retryAfterSeconds = 1;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ts))
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(ts.TotalSeconds));
            }

            var response = context.HttpContext.Response;
            response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            response.ContentType = "application/json; charset=utf-8";

            // Pull the partition key off the route so metrics + audit can
            // tag with the same tenantKey the limiter used (R4.6 + R8.4).
            var rawTenantKey = context.HttpContext.Request.RouteValues.TryGetValue("tenantKey", out var routeValue)
                ? routeValue as string
                : null;
            var tenantKey = (rawTenantKey ?? string.Empty).Trim().ToLowerInvariant();

            // Resolve the metrics + logger from the request scope so we
            // never reach into a static singleton — keeps the test harness
            // able to swap them per WebApplicationFactory.
            var services = context.HttpContext.RequestServices;
            var metrics = services.GetService<TenantClientCacheMetrics>();
            metrics?.PublicReadRateLimited(tenantKey, durationMs: 0d);

            var loggerFactory = services.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients.RateLimiter");
            if (logger is not null)
            {
                AuditEventPublicRead.EmitRateLimited(
                    logger,
                    new AuditFields(
                        EventType: AuditEventPublicRead.EventTypePrefix + AuditOutcome.RateLimited,
                        TenantKey: tenantKey,
                        ClientId: null,
                        Outcome: AuditOutcome.RateLimited,
                        DurationMs: 0d,
                        CorrelationId: Activity.Current?.TraceId.ToString(),
                        RemoteIpHash: null,
                        HttpStatus: StatusCodes.Status429TooManyRequests,
                        ETagSent: null,
                        RetryAfterSeconds: retryAfterSeconds));
            }

            await response
                .WriteAsync("{\"error\":\"rate_limit_exceeded\"}", cancellationToken)
                .ConfigureAwait(false);
        }
    }
}




