// Feature: tenant-client-cache-expansion, Task 5
//
// Public surface for the tenant-scoped Duende Client snapshot cache.
//
// Lifetime: Singleton (no per-tenant state held in implementation).
//
// Contract (verbatim from design.md "ITenantClientCacheService"):
//   - All methods accept a CancellationToken.
//   - Validation of tenantKey/clientId happens BEFORE any I/O.
//   - The implementation is fail-soft: any IDistributedCache exception is
//     caught + logged + counted, but never propagated.
//
// Validates: Requirements 3.1, 3.2, 3.3, 3.4, 5.4, 5.5, 6.7, 9.1, 9.2,
//            9.3, 9.4, 9.5, 9.7, 10.1, 10.2

#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

/// <summary>
/// Tenant-scoped public-safe snapshot cache for Duende Client config.
/// Coexists with the legacy <see cref="IClientScopeCacheService"/>; neither
/// service shares state with the other.
/// </summary>
public interface ITenantClientCacheService
{
    /// <summary>
    /// Read the current snapshot for <c>(tenantKey, clientId)</c>. Returns
    /// <c>null</c> when the entry is missing, corrupt, or carries a future
    /// schema version (Cache_Outcome.Stale per R2.8).
    /// </summary>
    Task<ClientCacheSnapshotEnvelope?> ReadSnapshotAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Write a fresh snapshot for <c>(tenantKey, client.ClientId)</c>.
    /// No-op when <c>TenantClientCacheOptions.Enabled == false</c>. Fails
    /// soft on any underlying cache error.
    /// </summary>
    Task WriteSnapshotAsync(
        string tenantKey,
        ClientDto client,
        CancellationToken cancellationToken);

    /// <summary>
    /// Write a fresh snapshot for each <paramref name="tenantKeys"/> entry,
    /// sequentially. Each tenant write is independent and fail-soft.
    /// </summary>
    Task WriteSnapshotsAsync(
        IReadOnlyCollection<string> tenantKeys,
        ClientDto client,
        CancellationToken cancellationToken);

    /// <summary>
    /// Remove the snapshot for <c>(tenantKey, clientId)</c>. Idempotent —
    /// removing a missing key is a success (R6.7).
    /// </summary>
    Task InvalidateSnapshotAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Remove the snapshot for each <paramref name="tenantKeys"/> entry,
    /// sequentially. Each tenant invalidate is independent and fail-soft.
    /// </summary>
    Task InvalidateSnapshotsAsync(
        IReadOnlyCollection<string> tenantKeys,
        string clientId,
        CancellationToken cancellationToken);
}
