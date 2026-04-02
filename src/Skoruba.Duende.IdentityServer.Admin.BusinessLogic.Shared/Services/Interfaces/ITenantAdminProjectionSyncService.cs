using System.Threading;
using System.Threading.Tasks;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Dtos.TenantAdminProjection;

namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Services.Interfaces
{
    public interface ITenantAdminProjectionSyncService
    {
        Task UpsertIfTenantAdminAsync(string userId, CancellationToken cancellationToken = default);
        Task<TenantAdminProjectionDeleteSnapshot> CaptureDeleteSnapshotAsync(string userId, CancellationToken cancellationToken = default);
        Task DeactivateAsync(TenantAdminProjectionDeleteSnapshot snapshot, CancellationToken cancellationToken = default);
        Task DeleteAsync(TenantAdminProjectionDeleteSnapshot snapshot, CancellationToken cancellationToken = default);
    }
}
