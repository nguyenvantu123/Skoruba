using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant;

namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces
{
    public interface ITenantService
    {
        Task<List<TenantDto>> GetTenantsAsync(string? search, CancellationToken ct);
        Task<List<PublicTenantDto>> GetPublicTenantsAsync(CancellationToken ct);
        Task<TenantDto?> GetTenantAsync(int id, CancellationToken ct);
        Task<TenantDto> CreateTenantAsync(TenantCreateDto model, CancellationToken ct);
        Task<TenantDto> CloneTenantAsync(TenantCloneDto model, CancellationToken ct);
        Task<TenantDto> UpdateTenantAsync(int id, TenantUpdateDto model, CancellationToken ct);
        Task<TenantRegistryLookupResultDto?> GetTenantRegistryLookupAsync(string identifier, string? serviceName, CancellationToken ct);

        Task<List<TenantAdminDto>> GetTenantAdminsAsync(int tenantId, CancellationToken ct);
        Task AssignTenantAdminAsync(int tenantId, string userId, CancellationToken ct);
        Task UnassignTenantAdminAsync(int tenantId, string userId, CancellationToken ct);
    }
}
