namespace TenantInfrastructure.MasterDb;

public interface ITenantRepository
{
    Task<IReadOnlyList<TenantInfo>> GetTenantsAsync(string? search, CancellationToken ct);
    Task<TenantInfo?> GetByIdAsync(int id, CancellationToken ct);
    Task<TenantInfo?> GetByKeyAsync(string tenantKey, CancellationToken ct);
    Task<TenantInfo> AddAsync(TenantInfo tenant, CancellationToken ct);
    Task UpdateAsync(TenantInfo tenant, CancellationToken ct);
}
