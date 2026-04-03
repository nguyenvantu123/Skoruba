using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using System.Linq;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.Identity;
using TenantInfrastructure.MasterDb;
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

        // master db factory
        services.AddDbContextFactory<MasterDbContext>(db =>
        {
            db.UseMySQL(NormalizeMySqlConnectionStringForDevelopment(opt.MasterConnectionString));
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
