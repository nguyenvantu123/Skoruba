using Microsoft.EntityFrameworkCore;
using TenantInfrastructure.Abstractions;

namespace TenantInfrastructure.MasterDb;

public sealed class EfTenantStore : ITenantStore
{
    private readonly IDbContextFactory<MasterDbContext> _factory;

    public EfTenantStore(IDbContextFactory<MasterDbContext> factory)
        => _factory = factory;

    public async Task<TenantInfo?> FindAsync(string tenantKey, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        return await db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantKey == tenantKey, ct);
    }
}
