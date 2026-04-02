using System.Threading;
using System.Threading.Tasks;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Dtos.TenantAdminProjection;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Services.Interfaces;

namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Services
{
    public sealed class NoOpTenantAdminProjectionSyncService : ITenantAdminProjectionSyncService
    {
        public Task UpsertIfTenantAdminAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<TenantAdminProjectionDeleteSnapshot> CaptureDeleteSnapshotAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TenantAdminProjectionDeleteSnapshot>(null);
        }

        public Task DeactivateAsync(TenantAdminProjectionDeleteSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(TenantAdminProjectionDeleteSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
