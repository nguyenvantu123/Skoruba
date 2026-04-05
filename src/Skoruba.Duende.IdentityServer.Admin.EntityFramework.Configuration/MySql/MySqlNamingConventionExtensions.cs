using Microsoft.EntityFrameworkCore;

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.MySql;

public static class MySqlNamingConventionExtensions
{
    public static DbContextOptionsBuilder UseSkorubaMySqlNamingConvention(this DbContextOptionsBuilder optionsBuilder)
    {
        return optionsBuilder.UseLowerCaseNamingConvention();
    }
}
