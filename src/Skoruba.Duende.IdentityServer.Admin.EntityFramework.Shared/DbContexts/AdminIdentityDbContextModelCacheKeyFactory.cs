using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;

public sealed class AdminIdentityDbContextModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        return context is AdminIdentityDbContext adminIdentityContext
            ? (context.GetType(), adminIdentityContext.SchemaCacheKey, designTime)
            : (context.GetType(), designTime);
    }
}
