#nullable enable
using System;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Skoruba.Duende.IdentityServer.TenantInfrastructure.UnitTests.Common;
using TenantInfrastructure.Wiring;
using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantInfrastructure.UnitTests.Wiring;

/// <summary>
/// Fail-fast contract tests for <see cref="ServiceCollectionExtensions.AddTenantInfrastructure"/>.
/// <para>
/// The wiring extension is required (per Requirements 1.3, 1.4, 1.5, 2.5, 3.7, 8.5, 8.6) to
/// reject misconfiguration <em>at registration time</em> rather than letting the failure
/// surface from EF Core when the first <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>
/// is resolved. These tests pin that contract:
/// </para>
/// <list type="bullet">
///   <item>Missing or whitespace <c>MasterConnectionString</c> throws an <see cref="InvalidOperationException"/>
///   whose message guides the operator to <c>ConnectionStrings:IdentityDbConnection</c>.</item>
///   <item>Missing or whitespace <c>DatabaseProvider</c> throws an <see cref="InvalidOperationException"/>
///   that lists the supported provider names so the operator can self-correct.</item>
/// </list>
/// <para>
/// Tests build an <see cref="IServiceCollection"/> directly — they do not depend on
/// <c>IConfiguration</c>, <c>IHost</c>, or environment variables — so they exercise the
/// wiring contract in isolation. The class joins <see cref="EnvironmentVariableCollection"/>
/// purely for forward-compatibility: should the extension ever consult an env var, this
/// guarantees serialised execution against sibling env-var-touching tests.
/// </para>
/// </summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class AddTenantInfrastructureFailFastTests
{
    /// <summary>
    /// Verbatim message contract for the missing-connection-string fail-fast.
    /// Kept as a constant so a drift in wording in production code (or in the test
    /// expectation) is caught by exact-match assertions in every scenario.
    /// </summary>
    private const string MissingConnectionStringMessage =
        "Connection string 'IdentityDbConnection' is required for TenantInfrastructure. " +
        "Set ConnectionStrings:IdentityDbConnection in configuration.";

    // Valid dummy connection string used by the provider-validation scenarios so the
    // connection-string guard passes and execution reaches the provider parser. No real
    // database connection is opened.
    private const string ValidMySqlConnectionString =
        "Server=localhost;Database=tenants;User Id=root;Password=secret;Port=3306";

    [Fact]
    public void AddTenantInfrastructure_Throws_WhenMasterConnectionStringIsNull()
    {
        var services = new ServiceCollection();

        var act = () => services.AddTenantInfrastructure(opt =>
        {
            opt.DatabaseProvider = "MySql";
            opt.MasterConnectionString = null!;
            opt.RedisConnectionString = string.Empty;
            opt.ApplyMasterDbMigrations = false;
            opt.AllowMasterDbAutoMigration = false;
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Be(MissingConnectionStringMessage);
    }

    [Fact]
    public void AddTenantInfrastructure_Throws_WhenMasterConnectionStringIsEmpty()
    {
        var services = new ServiceCollection();

        var act = () => services.AddTenantInfrastructure(opt =>
        {
            opt.DatabaseProvider = "MySql";
            opt.MasterConnectionString = string.Empty;
            opt.RedisConnectionString = string.Empty;
            opt.ApplyMasterDbMigrations = false;
            opt.AllowMasterDbAutoMigration = false;
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Be(MissingConnectionStringMessage);
    }

    [Fact]
    public void AddTenantInfrastructure_Throws_WhenMasterConnectionStringIsWhitespace()
    {
        var services = new ServiceCollection();

        var act = () => services.AddTenantInfrastructure(opt =>
        {
            opt.DatabaseProvider = "MySql";
            opt.MasterConnectionString = "   ";
            opt.RedisConnectionString = string.Empty;
            opt.ApplyMasterDbMigrations = false;
            opt.AllowMasterDbAutoMigration = false;
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Be(MissingConnectionStringMessage);
    }

    [Fact]
    public void AddTenantInfrastructure_Throws_WhenDatabaseProviderIsNull()
    {
        var services = new ServiceCollection();

        var act = () => services.AddTenantInfrastructure(opt =>
        {
            opt.DatabaseProvider = null!;
            opt.MasterConnectionString = ValidMySqlConnectionString;
            opt.RedisConnectionString = string.Empty;
            opt.ApplyMasterDbMigrations = false;
            opt.AllowMasterDbAutoMigration = false;
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("DatabaseProvider").And
                .Contain("SqlServer").And
                .Contain("PostgreSQL").And
                .Contain("MySql");
    }

    [Fact]
    public void AddTenantInfrastructure_Throws_WhenDatabaseProviderIsEmpty()
    {
        var services = new ServiceCollection();

        var act = () => services.AddTenantInfrastructure(opt =>
        {
            opt.DatabaseProvider = string.Empty;
            opt.MasterConnectionString = ValidMySqlConnectionString;
            opt.RedisConnectionString = string.Empty;
            opt.ApplyMasterDbMigrations = false;
            opt.AllowMasterDbAutoMigration = false;
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("DatabaseProvider").And
                .Contain("SqlServer").And
                .Contain("PostgreSQL").And
                .Contain("MySql");
    }

    [Fact]
    public void AddTenantInfrastructure_Throws_WhenDatabaseProviderIsWhitespace()
    {
        var services = new ServiceCollection();

        var act = () => services.AddTenantInfrastructure(opt =>
        {
            opt.DatabaseProvider = "   ";
            opt.MasterConnectionString = ValidMySqlConnectionString;
            opt.RedisConnectionString = string.Empty;
            opt.ApplyMasterDbMigrations = false;
            opt.AllowMasterDbAutoMigration = false;
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("DatabaseProvider").And
                .Contain("SqlServer").And
                .Contain("PostgreSQL").And
                .Contain("MySql");
    }
}
