// Feature: tenant-client-cache-public-read, Task 11/12 — STS.Identity consumer wrapper
//
// Host-side facade over the public-read SDK. Resolves tenantKey automatically
// from <c>ITenantContextAccessor.Current</c> so consumers never hard-code a
// tenantKey. Caller passes <c>clientId</c> from the active request (typically
// Duende <c>Client.ClientId</c>).

#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

/// <summary>
/// Reads <c>Public_Safe_Fields</c> client snapshots from the central
/// public-read endpoint without forcing each caller to know about the
/// SDK, the API key, or the tenant resolution rules. The provider:
/// <list type="bullet">
///   <item><description>Resolves <c>tenantKey</c> from <c>ITenantContextAccessor.Current</c> on every call.</description></item>
///   <item><description>Is fail-soft: never throws on missing tenant context, missing config, or SDK errors. Callers switch on <see cref="PublicClientSnapshotLookup.Outcome"/>.</description></item>
///   <item><description>Logs structured warnings on failure paths. Never logs the API key or hash digest.</description></item>
/// </list>
/// </summary>
public interface IPublicTenantClientSnapshotProvider
{
    /// <summary>
    /// Look up the public-safe snapshot for the supplied <paramref name="clientId"/>
    /// in the current tenant context.
    /// </summary>
    /// <param name="clientId">
    /// The client identifier (typically <c>Duende.IdentityServer.Models.Client.ClientId</c>
    /// from the active request). Must be non-empty; <see langword="null"/>, empty,
    /// or whitespace returns <see cref="PublicClientSnapshotOutcome.InvalidClientId"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token propagated to the SDK call.</param>
    Task<PublicClientSnapshotLookup> GetSnapshotAsync(
        string clientId,
        CancellationToken cancellationToken = default);
}
