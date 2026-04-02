using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Linq;

namespace TenantInfrastructure.MasterDb;

public sealed class MasterDbContextFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args)
    {
        var connectionString = NormalizeMySqlConnectionStringForDevelopment(ResolveConnectionString(args));
        var optionsBuilder = new DbContextOptionsBuilder<MasterDbContext>();

        optionsBuilder.UseMySQL(connectionString);

        return new MasterDbContext(optionsBuilder.Options);
    }

    private static string ResolveConnectionString(string[] args)
    {
        var fromArgs = args
            .Select(ParseConnectionArgument)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (!string.IsNullOrWhiteSpace(fromArgs))
        {
            return fromArgs;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("ConnectionStrings__MasterDb");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        throw new InvalidOperationException(
            "MasterDb connection string is missing. Set ConnectionStrings__MasterDb or pass --connection=<value> to dotnet ef.");
    }

    private static string? ParseConnectionArgument(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        const string prefix = "--connection=";
        return arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? arg[prefix.Length..]
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
                       !trimmedPart.StartsWith("Ssl Mode=", StringComparison.OrdinalIgnoreCase);
            });

        return $"{string.Join(";", parts)};SslMode=Disabled";
    }
}
