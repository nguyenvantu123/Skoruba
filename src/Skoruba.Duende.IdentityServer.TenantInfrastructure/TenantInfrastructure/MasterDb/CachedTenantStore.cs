using Microsoft.Extensions.Caching.Memory;
using TenantInfrastructure.Abstractions;

namespace TenantInfrastructure.MasterDb;

public sealed class CachedTenantStore : ITenantStore
{
    private readonly ITenantStore _inner;
    private readonly ITenantRegistryCache _cache;

    public CachedTenantStore(
        ITenantStore inner,
        ITenantRegistryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public async Task<TenantInfo?> FindAsync(string tenantKey, CancellationToken ct)
    {
        var cachedTenant = _cache.TryGetTenant(tenantKey);
        if (cachedTenant != null)
        {
            return cachedTenant;
        }

        var tenant = await _inner.FindAsync(tenantKey, ct);
        if (tenant != null)
        {
            _cache.SetTenant(tenant);
        }

        return tenant;
    }
}
