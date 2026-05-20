using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantInfrastructure.MasterDb;

namespace TenantInfrastructure.Wiring;

public static class ApplicationBuilderExtensions
{
    public static async Task InitializeTenantMasterDbAsync(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MasterDbContext>>();
        var options = scope.ServiceProvider.GetRequiredService<TenantInfrastructureOptions>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("TenantInfrastructure.Init");

        await using var db = await factory.CreateDbContextAsync();

        if (options.ApplyMasterDbMigrations)
        {
            if (!options.AllowMasterDbAutoMigration)
            {
                logger.LogInformation(
                    "Tenant registry migrations are configured but auto-migration is disabled. " +
                    "Skipping Database.Migrate(). Operator must apply migrations manually against IdentityServerAdmin.");
                return;
            }

            logger.LogInformation("Applying tenant registry migrations against IdentityServerAdmin database.");
            await db.Database.MigrateAsync();
            return;
        }

        logger.LogInformation("Tenant registry migrations disabled; calling EnsureCreatedAsync on IdentityServerAdmin database.");
        await db.Database.EnsureCreatedAsync();
    }
}
