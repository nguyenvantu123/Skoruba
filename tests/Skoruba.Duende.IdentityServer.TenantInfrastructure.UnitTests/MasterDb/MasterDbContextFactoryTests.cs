using System;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Skoruba.Duende.IdentityServer.TenantInfrastructure.UnitTests.Common;
using TenantInfrastructure.MasterDb;
using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantInfrastructure.UnitTests.MasterDb;

/// <summary>
/// Decision-branch tests for <see cref="MasterDbContextFactory"/>. The factory mutates
/// process-wide state (environment variables) when invoked by <c>dotnet ef</c>, so all
/// tests in this class share a single xUnit collection to serialise execution and avoid
/// env-var races with sibling test classes.
/// </summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class MasterDbContextFactoryTests
{
    private const string ConnectionEnvVar = "ConnectionStrings__IdentityDbConnection";
    private const string ProviderEnvVar = "DatabaseProviderConfiguration__ProviderType";
    private const string LegacyConnectionEnvVar = "ConnectionStrings__MasterDb";
    private const string AspNetCoreEnvironmentVar = "ASPNETCORE_ENVIRONMENT";

    // Dummy connection strings — the factory only configures EF options, it never opens
    // a real connection, so syntactically valid placeholders are enough.
    private const string SqlServerConnectionString =
        "Server=tcp:localhost;Database=Tenants;Integrated Security=true";

    private const string PostgreSqlConnectionString =
        "Host=localhost;Database=tenants;Username=postgres;Password=secret";

    private const string MySqlConnectionString =
        "Server=localhost;Database=tenants;User Id=root;Password=secret;Port=3306";

    [Fact]
    public void CreateDbContext_PrefersConnectionArgument_OverEnvironmentVariable()
    {
        const string envConnection =
            "Server=env-host;Database=tenants;User Id=root;Password=env;Port=3306";
        const string argConnection =
            "Server=arg-host;Database=tenants;User Id=root;Password=arg;Port=3306";

        // Force a non-Development environment so the MySql normalisation helper does not
        // rewrite the connection string and we can assert exact equality.
        using var _ = new EnvironmentVariableScope(AspNetCoreEnvironmentVar, "Production");
        using var __ = new EnvironmentVariableScope(ProviderEnvVar, null);
        using var ___ = new EnvironmentVariableScope(ConnectionEnvVar, envConnection);

        var factory = new MasterDbContextFactory();

        using var ctx = factory.CreateDbContext(new[] { $"--connection={argConnection}" });

        ctx.Database.GetConnectionString().Should().Be(argConnection);
    }

    [Fact]
    public void CreateDbContext_FallsBackToEnvironmentVariable_WhenArgumentMissing()
    {
        using var _ = new EnvironmentVariableScope(AspNetCoreEnvironmentVar, "Production");
        using var __ = new EnvironmentVariableScope(ProviderEnvVar, null);
        using var ___ = new EnvironmentVariableScope(ConnectionEnvVar, MySqlConnectionString);

        var factory = new MasterDbContextFactory();

        using var ctx = factory.CreateDbContext(Array.Empty<string>());

        ctx.Should().NotBeNull();
        // Default provider is MySql when DatabaseProviderConfiguration__ProviderType is unset.
        ctx.Database.ProviderName.Should().Be("MySql.EntityFrameworkCore");
        ctx.Database.GetConnectionString().Should().Be(MySqlConnectionString);
    }

    [Fact]
    public void CreateDbContext_Throws_WhenBothArgumentAndEnvironmentVariableMissing()
    {
        using var _ = new EnvironmentVariableScope(ProviderEnvVar, null);
        using var __ = new EnvironmentVariableScope(ConnectionEnvVar, null);
        using var ___ = new EnvironmentVariableScope(LegacyConnectionEnvVar, null);

        var factory = new MasterDbContextFactory();

        var act = () => factory.CreateDbContext(Array.Empty<string>());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain(ConnectionEnvVar).And
                .Contain("--connection=<value>");
    }

    [Fact]
    public void CreateDbContext_DoesNotConsider_LegacyMasterDbEnvironmentVariable()
    {
        // ConnectionStrings__MasterDb must not be honoured by the new factory: setting it
        // alone (with IdentityDbConnection unset) must still fail.
        using var _ = new EnvironmentVariableScope(ProviderEnvVar, null);
        using var __ = new EnvironmentVariableScope(ConnectionEnvVar, null);
        using var ___ = new EnvironmentVariableScope(LegacyConnectionEnvVar, MySqlConnectionString);

        var factory = new MasterDbContextFactory();

        var act = () => factory.CreateDbContext(Array.Empty<string>());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(ConnectionEnvVar);
    }

    [Fact]
    public void CreateDbContext_UsesSqlServerProvider_WhenProviderEnvVarIsSqlServer()
    {
        using var _ = new EnvironmentVariableScope(ProviderEnvVar, "SqlServer");
        using var __ = new EnvironmentVariableScope(ConnectionEnvVar, SqlServerConnectionString);

        var factory = new MasterDbContextFactory();

        using var ctx = factory.CreateDbContext(Array.Empty<string>());

        ctx.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.SqlServer");
    }

    [Fact]
    public void CreateDbContext_UsesNpgsqlProvider_WhenProviderEnvVarIsPostgreSQL()
    {
        using var _ = new EnvironmentVariableScope(ProviderEnvVar, "PostgreSQL");
        using var __ = new EnvironmentVariableScope(ConnectionEnvVar, PostgreSqlConnectionString);

        var factory = new MasterDbContextFactory();

        using var ctx = factory.CreateDbContext(Array.Empty<string>());

        ctx.Database.ProviderName.Should().Be("Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    [Fact]
    public void CreateDbContext_UsesMySqlProvider_WhenProviderEnvVarIsMySql()
    {
        using var _ = new EnvironmentVariableScope(AspNetCoreEnvironmentVar, "Production");
        using var __ = new EnvironmentVariableScope(ProviderEnvVar, "MySql");
        using var ___ = new EnvironmentVariableScope(ConnectionEnvVar, MySqlConnectionString);

        var factory = new MasterDbContextFactory();

        using var ctx = factory.CreateDbContext(Array.Empty<string>());

        // MySql.EntityFrameworkCore (Oracle's official package) advertises this provider
        // name. Pomelo would expose "Pomelo.EntityFrameworkCore.MySql" instead.
        ctx.Database.ProviderName.Should().Be("MySql.EntityFrameworkCore");
    }

    [Fact]
    public void CreateDbContext_Throws_WhenProviderEnvVarIsUnsupported()
    {
        using var _ = new EnvironmentVariableScope(ProviderEnvVar, "oracle");
        using var __ = new EnvironmentVariableScope(ConnectionEnvVar, MySqlConnectionString);

        var factory = new MasterDbContextFactory();

        var act = () => factory.CreateDbContext(Array.Empty<string>());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("SqlServer").And
                .Contain("PostgreSQL").And
                .Contain("MySql");
    }
}
