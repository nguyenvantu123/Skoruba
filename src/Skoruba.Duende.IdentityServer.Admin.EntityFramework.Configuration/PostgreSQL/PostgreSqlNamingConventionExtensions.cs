using Microsoft.EntityFrameworkCore;

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.PostgreSQL;

public static class PostgreSqlNamingConventionExtensions
{
    public static DbContextOptionsBuilder UseSkorubaPostgreSqlNamingConvention(this DbContextOptionsBuilder optionsBuilder)
    {
        return optionsBuilder.UseLowerCaseNamingConvention();
    }
}
