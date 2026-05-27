// Feature: tenant-client-cache-public-read
// Public client surface for the tenant client cache SDK (R10.2, R10.3, R11.8).
// Concrete implementation lands in Task 9. This Task 7 contributes only the
// interface stub so consumer-facing types compile in isolation.

using System.Threading;
using System.Threading.Tasks;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client;

/// <summary>
/// SDK entry point for reading public-safe Duende client snapshots from the
/// public-read endpoint <c>GET /api/public/tenants/{tenantKey}/clients/{clientId}</c>.
/// </summary>
/// <remarks>
/// Implementations MUST be safe to register as a <c>Singleton</c> and resolve
/// <see cref="System.Net.Http.IHttpClientFactory"/> internally (R10.6, R10.11).
/// </remarks>
public interface ITenantClientCacheClient
{
    /// <summary>
    /// Get the snapshot for <paramref name="tenantKey"/> / <paramref name="clientId"/>.
    /// The SDK MAY return an in-memory cache hit (R11.6, R11.7) without issuing
    /// HTTP traffic.
    /// </summary>
    /// <param name="tenantKey">The tenant identifier (case-insensitive; trimmed and lowercased internally).</param>
    /// <param name="clientId">The client identifier (trimmed internally).</param>
    /// <param name="cancellationToken">Cancellation token propagated to the HTTP call (R11.5).</param>
    Task<TenantClientSnapshotResult> GetClientAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Force-bypass the in-memory cache and revalidate against the server with
    /// the supplied <c>If-None-Match</c> header (R11.8).
    /// </summary>
    /// <param name="tenantKey">The tenant identifier (case-insensitive; trimmed and lowercased internally).</param>
    /// <param name="clientId">The client identifier (trimmed internally).</param>
    /// <param name="ifNoneMatch">
    /// The ETag value to send in the <c>If-None-Match</c> header. Pass <see langword="null"/> to
    /// fall back to the SDK's automatic revalidation behavior (R11.9).
    /// </param>
    /// <param name="cancellationToken">Cancellation token propagated to the HTTP call (R11.5).</param>
    Task<TenantClientSnapshotResult> GetClientAsync(
        string tenantKey,
        string clientId,
        string? ifNoneMatch,
        CancellationToken cancellationToken = default);
}
