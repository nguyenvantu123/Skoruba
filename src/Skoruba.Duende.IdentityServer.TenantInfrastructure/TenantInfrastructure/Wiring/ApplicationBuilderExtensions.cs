using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantInfrastructure.MasterDb;

namespace TenantInfrastructure.Wiring;

public static class ApplicationBuilderExtensions
{
    public static async Task InitializeTenantMasterDbAsync(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MasterDbContext>>();
        var options = scope.ServiceProvider.GetRequiredService<TenantInfrastructureOptions>();
        await using var db = await factory.CreateDbContextAsync();

        if (options.ApplyMasterDbMigrations)
        {
            if (!options.AllowMasterDbAutoMigration)
            {
                return;
            }

            await db.Database.MigrateAsync();
            return;
        }

        await db.Database.EnsureCreatedAsync();
    }
}
