#nullable enable
using System;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skoruba.Duende.IdentityServer.TenantInfrastructure.UnitTests.Common;
using TenantInfrastructure.MasterDb;
using TenantInfrastructure.Wiring;
using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantInfrastructure.UnitTests.Wiring;

/// <summary>
/// Decision-branch tests for <see cref="ServiceCollectionExtensions.AddTenantInfrastructure"/>.
/// <para>
/// Each test exercises the provider switch path that <c>AddTenantInfrastructure</c> uses to
/// register <see cref="IDbContextFactory{MasterDbContext}"/> and the per-provider column-type
/// metadata applied in <see cref="MasterDbContext.OnModelCreating"/>. The tests build only EF
/// Core options and model metadata; they never open a real database connection.
/// </para>
/// <para>
/// The test class joins the <see cref="EnvironmentVariableCollection"/> because the MySql path
/// inspects <c>ASPNETCORE_ENVIRONMENT</c> via <c>NormalizeMySqlConnectionStringForDevelopment</c>
/// and we pin that variable to <c>Production</c> for determinism. Sharing the collection
/// serialises execution against sibling env-var-touching tests.
/// </para>
/// </summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class AddTenantInfrastructureProviderSwitchTests
{
    private const string AspNetCoreEnvironmentVar = "ASPNETCORE_ENVIRONMENT";

    // Dummy connection strings — provider switch + model metadata only, no real connection.
    private const string SqlServerConnectionString =
        "Server=tcp:localhost;Database=Tenants;Integrated Security=true";

    private const string PostgreSqlConnectionString =
        "Host=localhost;Database=tenants;Username=postgres;Password=secret";

    private const string MySqlConnectionString =
        "Server=localhost;Database=tenants;User Id=root;Password=secret;Port=3306";

    [Fact]
    public void AddTenantInfrastructure_RegistersSqlServerProvider_WhenDatabaseProviderIsSqlServer()
    {
        using var ctx = BuildContext("SqlServer", SqlServerConnectionString);

        ctx.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.SqlServer");
    }

    [Fact]
    public void AddTenantInfrastructure_RegistersNpgsqlProvider_WhenDatabaseProviderIsPostgreSQL()
    {
        using var ctx = BuildContext("PostgreSQL", PostgreSqlConnectionString);

        ctx.Database.ProviderName.Should().Be("Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    [Fact]
    public void AddTenantInfrastructure_RegistersMySqlProvider_WhenDatabaseProviderIsMySql()
    {
        // Pin environment to Production so NormalizeMySqlConnectionStringForDevelopment is a no-op.
        using var _ = new EnvironmentVariableScope(AspNetCoreEnvironmentVar, "Production");

        using var ctx = BuildContext("MySql", MySqlConnectionString);

        // MySql.EntityFrameworkCore (Oracle's official package) advertises this provider name.
        ctx.Database.ProviderName.Should().Be("MySql.EntityFrameworkCore");
    }

    [Fact]
    public void AddTenantInfrastructure_Throws_WhenDatabaseProviderIsUnsupported()
    {
        // The provider value is parsed at AddTenantInfrastructure call-time so an invalid
        // value fails fast at registration, before any IDbContextFactory<MasterDbContext>
        // resolution would happen. Either way the contract is the same: InvalidOperationException
        // whose message lists the supported providers.
        var services = new ServiceCollection();

        var act = () => services.AddTenantInfrastructure(opt =>
        {
            opt.DatabaseProvider = "oracle";
            opt.MasterConnectionString = MySqlConnectionString;
            opt.RedisConnectionString = string.Empty;
            opt.ApplyMasterDbMigrations = false;
            opt.AllowMasterDbAutoMigration = false;
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("SqlServer").And
                .Contain("PostgreSQL").And
                .Contain("MySql");
    }

    [Fact]
    public void MasterDbContext_MapsConnectionSecrets_AsNvarcharMax_ForSqlServer()
    {
        using var ctx = BuildContext("SqlServer", SqlServerConnectionString);

        GetConnectionSecretsColumnType(ctx).Should().Be("nvarchar(max)");
    }

    [Fact]
    public void MasterDbContext_MapsConnectionSecrets_AsJsonb_ForPostgreSQL()
    {
        using var ctx = BuildContext("PostgreSQL", PostgreSqlConnectionString);

        GetConnectionSecretsColumnType(ctx).Should().Be("jsonb");
    }

    [Fact]
    public void MasterDbContext_MapsConnectionSecrets_AsJson_ForMySql()
    {
        using var _ = new EnvironmentVariableScope(AspNetCoreEnvironmentVar, "Production");

        using var ctx = BuildContext("MySql", MySqlConnectionString);

        GetConnectionSecretsColumnType(ctx).Should().Be("json");
    }

    private static MasterDbContext BuildContext(string databaseProvider, string connectionString)
    {
        var services = new ServiceCollection();
        services.AddTenantInfrastructure(opt =>
        {
            opt.DatabaseProvider = databaseProvider;
            opt.MasterConnectionString = connectionString;
            opt.RedisConnectionString = string.Empty;
            opt.ApplyMasterDbMigrations = false;
            opt.AllowMasterDbAutoMigration = false;
        });

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MasterDbContext>>();
        return factory.CreateDbContext();
    }

    private static string? GetConnectionSecretsColumnType(MasterDbContext ctx)
    {
        var entityType = ctx.Model.FindEntityType(typeof(TenantInfo));
        entityType.Should().NotBeNull("MasterDbContext must map TenantInfo");
        return entityType!.FindProperty(nameof(TenantInfo.ConnectionSecretsJson))?.GetColumnType();
    }
}
