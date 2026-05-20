using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using System.Linq;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.Identity;
using TenantInfrastructure.MasterDb;
using TenantInfrastructure.MasterDb.Internal;
using TenantInfrastructure.Resolution;

namespace TenantInfrastructure.Wiring;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTenantInfrastructure(
        this IServiceCollection services,
        Action<TenantInfrastructureOptions> configure)
    {
        var opt = new TenantInfrastructureOptions();
        configure(opt);

        // Fail-fast: a missing IdentityDbConnection should produce a clear, actionable error
        // before any DI/EF registration is attempted (Requirements 1.1, 2.1, 2.5).
        if (string.IsNullOrWhiteSpace(opt.MasterConnectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'IdentityDbConnection' is required for TenantInfrastructure. " +
                "Set ConnectionStrings:IdentityDbConnection in configuration.");
        }

        // Parse once at registration time so an invalid provider value also fails fast,
        // and so we can capture the parsed enum into the lambda below without re-parsing
        // on every IDbContextFactory<MasterDbContext>.CreateDbContext() call.
        var provider = TenantDatabaseProviderParser.Parse(opt.DatabaseProvider);

        services.AddSingleton(opt);

        if (string.IsNullOrWhiteSpace(opt.RedisConnectionString))
        {
            services.AddDistributedMemoryCache();
        }
        else
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = opt.RedisConnectionString;
                options.InstanceName = opt.RedisInstanceName;
            });
        }

        // tenant context
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();

        // master db factory: provider-aware switch mirrors the wiring style used in
        // Skoruba.Duende.IdentityServer.Admin.EntityFramework.* helpers, but is duplicated
        // here on purpose to keep the layer boundary (TenantInfrastructure must not take a
        // reference on the Admin.EntityFramework configuration assembly).
        services.AddDbContextFactory<MasterDbContext>(db =>
        {
            var migrationsAssembly = typeof(MasterDbContext).Assembly.GetName().Name!;
            const string historyTable = "__EFMigrationsHistory_TenantRegistry";

            switch (provider)
            {
                case TenantDatabaseProvider.SqlServer:
                    db.UseSqlServer(opt.MasterConnectionString, sql =>
                    {
                        sql.MigrationsAssembly(migrationsAssembly);
                        sql.MigrationsHistoryTable(historyTable);
                    });
                    break;

                case TenantDatabaseProvider.PostgreSQL:
                    db.UseNpgsql(opt.MasterConnectionString, npg =>
                    {
                        npg.MigrationsAssembly(migrationsAssembly);
                        npg.MigrationsHistoryTable(historyTable);
                    });
                    break;

                case TenantDatabaseProvider.MySql:
                    db.UseMySQL(NormalizeMySqlConnectionStringForDevelopment(opt.MasterConnectionString), my =>
                        {
                            my.MigrationsAssembly(migrationsAssembly);
                            my.MigrationsHistoryTable(historyTable);
                        })
                        .UseLowerCaseNamingConvention();
                    break;

                default:
                    throw new InvalidOperationException(
                        $"DatabaseProvider '{provider}' is not supported for TenantInfrastructure. " +
                        "Supported values: SqlServer, PostgreSQL, MySql.");
            }

            // Carry the provider into DbContextOptions so MasterDbContext.OnModelCreating
            // can branch its mapping (column types, naming convention) per provider.
            ((IDbContextOptionsBuilderInfrastructure)db)
                .AddOrUpdateExtension(new TenantProviderOptionsExtension(provider));
        });

        // store + cache
        services.AddScoped<EfTenantStore>();
        services.AddScoped<ITenantRepository, EfTenantRepository>();
        services.AddSingleton<ITenantRegistryCache>(sp =>
            new DistributedTenantRegistryCache(
                sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DistributedTenantRegistryCache>>()));
        services.AddScoped<ITenantStore>(sp =>
        {
            var ef = sp.GetRequiredService<EfTenantStore>();
            var cache = sp.GetRequiredService<ITenantRegistryCache>();
            return new CachedTenantStore(ef, cache);
        });
        services.AddHostedService<TenantRegistryCacheRefreshService>();

        // resolution options
        services.AddSingleton(opt.Resolution);


        // validator
        services.AddScoped<ITenantUserValidator, TenantUserValidator>();

        return services;
    }

    private static string NormalizeMySqlConnectionStringForDevelopment(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
        if (!isDevelopment)
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
}
