using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Linq;
using TenantInfrastructure.MasterDb.Internal;
using TenantInfrastructure.Wiring;

namespace TenantInfrastructure.MasterDb;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> tooling to instantiate
/// <see cref="MasterDbContext"/> outside the normal host wiring.
/// <para>
/// Connection string resolution order (Requirement 6.1, 6.2, 6.5):
/// <list type="number">
///   <item><c>--connection=&lt;value&gt;</c> command-line argument (case-insensitive). Highest priority.</item>
///   <item><c>ConnectionStrings__IdentityDbConnection</c> environment variable.</item>
/// </list>
/// The legacy <c>ConnectionStrings__MasterDb</c> environment variable is no longer
/// consulted: tenant registry data now lives inside the <c>IdentityServerAdmin</c>
/// database alongside <c>AdminIdentityDbContext</c>.
/// </para>
/// <para>
/// Provider selection mirrors the runtime wiring in
/// <see cref="ServiceCollectionExtensions.AddTenantInfrastructure"/>: the
/// <c>DatabaseProviderConfiguration__ProviderType</c> environment variable is parsed via
/// <see cref="TenantDatabaseProviderParser"/>. When the variable is missing or empty the
/// factory defaults to <see cref="TenantDatabaseProvider.MySql"/> to preserve the
/// behaviour of <c>dotnet ef migrations add</c> in the MySQL workspace (Requirement 6.4).
/// </para>
/// </summary>
public sealed class MasterDbContextFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    private const string ConnectionEnvVar = "ConnectionStrings__IdentityDbConnection";
    private const string ProviderEnvVar = "DatabaseProviderConfiguration__ProviderType";
    private const string ConnectionArgPrefix = "--connection=";
    private const string MigrationsHistoryTableName = "__EFMigrationsHistory_TenantRegistry";

    public MasterDbContext CreateDbContext(string[] args)
    {
        // Default to MySql when the env var is missing or whitespace so that running
        // `dotnet ef migrations add` in the existing MySQL workspace keeps working without
        // requiring operators to set an extra variable. Explicit unsupported values (e.g.
        // "oracle") still bubble up through TenantDatabaseProviderParser.Parse.
        var providerEnv = Environment.GetEnvironmentVariable(ProviderEnvVar);
        var provider = string.IsNullOrWhiteSpace(providerEnv)
            ? TenantDatabaseProvider.MySql
            : TenantDatabaseProviderParser.Parse(providerEnv);

        var connectionString = ResolveConnectionString(args);
        if (provider == TenantDatabaseProvider.MySql)
        {
            connectionString = NormalizeMySqlConnectionStringForDevelopment(connectionString);
        }

        var optionsBuilder = new DbContextOptionsBuilder<MasterDbContext>();
        var migrationsAssembly = typeof(MasterDbContext).Assembly.GetName().Name!;

        switch (provider)
        {
            case TenantDatabaseProvider.SqlServer:
                optionsBuilder.UseSqlServer(connectionString, sql =>
                {
                    sql.MigrationsAssembly(migrationsAssembly);
                    sql.MigrationsHistoryTable(MigrationsHistoryTableName);
                });
                break;

            case TenantDatabaseProvider.PostgreSQL:
                optionsBuilder.UseNpgsql(connectionString, npg =>
                {
                    npg.MigrationsAssembly(migrationsAssembly);
                    npg.MigrationsHistoryTable(MigrationsHistoryTableName);
                });
                break;

            case TenantDatabaseProvider.MySql:
                optionsBuilder.UseMySQL(connectionString, my =>
                    {
                        my.MigrationsAssembly(migrationsAssembly);
                        my.MigrationsHistoryTable(MigrationsHistoryTableName);
                    })
                    .UseLowerCaseNamingConvention();
                break;

            default:
                // Defence in depth: TenantDatabaseProviderParser already validates the value,
                // but keep an explicit branch so any future enum addition fails fast here too.
                throw new InvalidOperationException(
                    $"DatabaseProvider '{provider}' is not supported for TenantInfrastructure. " +
                    "Supported values: SqlServer, PostgreSQL, MySql.");
        }

        // Carry the provider into DbContextOptions so MasterDbContext.OnModelCreating
        // can branch its mapping (column types, naming convention) per provider — this
        // matches the registration done in ServiceCollectionExtensions.AddTenantInfrastructure.
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new TenantProviderOptionsExtension(provider));

        return new MasterDbContext(optionsBuilder.Options);
    }

    private static string ResolveConnectionString(string[] args)
    {
        var fromArgs = args
            .Select(ParseConnectionArgument)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (!string.IsNullOrWhiteSpace(fromArgs))
        {
            return fromArgs!;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        throw new InvalidOperationException(
            "Tenant registry connection string is missing. " +
            $"Set {ConnectionEnvVar} or pass --connection=<value> to dotnet ef.");
    }

    private static string? ParseConnectionArgument(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        return arg.StartsWith(ConnectionArgPrefix, StringComparison.OrdinalIgnoreCase)
            ? arg[ConnectionArgPrefix.Length..]
            : null;
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
