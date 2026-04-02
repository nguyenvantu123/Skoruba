using TenantInfrastructure.MasterDb;
using static TenantInfrastructure.MasterDb.DistributedTenantRegistryCache;

namespace TenantInfrastructure.Abstractions;

public interface ITenantRegistryCache
{
    TenantInfo? TryGetTenant(string tenantKey);
    string? TryGetSecretName(string tenantKey, string serviceName);
    IReadOnlyList<PublicTenantCacheData>? TryGetPublicTenantNames();
    void SetTenant(TenantInfo tenant);
    void SetPublicTenantNames(IEnumerable<PublicTenantCacheData> tenantNames);
}
