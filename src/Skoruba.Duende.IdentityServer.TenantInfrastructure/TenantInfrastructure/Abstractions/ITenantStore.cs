namespace TenantInfrastructure.Abstractions;

public interface ITenantStore
{
    Task<MasterDb.TenantInfo?> FindAsync(string tenantKey, CancellationToken ct);
}
