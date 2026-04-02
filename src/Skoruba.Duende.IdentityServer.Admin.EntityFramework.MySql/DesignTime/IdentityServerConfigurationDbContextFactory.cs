using System;
using System.IO;
using System.Text.Json;
using Duende.IdentityServer.EntityFramework.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql.Helpers;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql.DesignTime;

public sealed class IdentityServerConfigurationDbContextFactory : IDesignTimeDbContextFactory<IdentityServerConfigurationDbContext>
{
    private const string DefaultDesignTimeConnectionString =
        "Server=localhost;Port=3306;Database=skoruba_design_time;Uid=root;Pwd=design_time;SslMode=Disabled";

    public IdentityServerConfigurationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<IdentityServerConfigurationDbContext>();
        var services = new ServiceCollection();

        services.AddSingleton(new ConfigurationStoreOptions());

        var serviceProvider = services.BuildServiceProvider();

        optionsBuilder.UseMySQL(
            GetConnectionString(),
            mySql => mySql.MigrationsAssembly(typeof(MigrationAssembly).Assembly.GetName().Name));
        optionsBuilder.UseApplicationServiceProvider(serviceProvider);

        return new IdentityServerConfigurationDbContext(optionsBuilder.Options);
    }

    private static string GetConnectionString()
    {
        var environmentConnection = Environment.GetEnvironmentVariable("ConnectionStrings__ConfigurationDbConnection");
        if (!string.IsNullOrWhiteSpace(environmentConnection))
            return environmentConnection;

        var designTimeConnection = Environment.GetEnvironmentVariable("EFTOOLS__ConfigurationDbConnection");
        if (!string.IsNullOrWhiteSpace(designTimeConnection))
            return designTimeConnection;

        var appSettingsConnection = TryReadConnectionStringFromAdminApiAppSettings();
        if (!string.IsNullOrWhiteSpace(appSettingsConnection))
            return appSettingsConnection;

        return DefaultDesignTimeConnectionString;
    }

    private static string TryReadConnectionStringFromAdminApiAppSettings()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var appSettingsPath = Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Skoruba.Duende.IdentityServer.Admin.Api",
            "appsettings.json"));

        if (!File.Exists(appSettingsPath))
            return null;

        using var stream = File.OpenRead(appSettingsPath);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings))
            return null;

        return connectionStrings.TryGetProperty("ConfigurationDbConnection", out var connectionString)
            ? connectionString.GetString()
            : null;
    }
}
