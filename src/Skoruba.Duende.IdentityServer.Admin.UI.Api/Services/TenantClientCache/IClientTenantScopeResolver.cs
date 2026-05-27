// Feature: tenant-client-cache-expansion, Task 4
//
// Resolves the set of tenant keys a Duende `Client` row belongs to. Mirrors
// the priority chain enforced by `ClientTenantRedirectResolver` in the STS:
//   1. ClientTenantRedirectUris rows for the client (DB)            ← truth
//   2. JSON in `Properties[skoruba_tenant_redirect_pairs]` (legacy) ← fallback
//   3. Empty list                                                   ← shared/global
//
// Lifetime: Scoped (R11.7). Implementations consume `IClientService` /
// scoped DbContext-backed services.
//
// Validates: Requirements 11.1, 11.2, 11.3, 11.4, 11.5, 11.6, 11.7

#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

/// <summary>
/// Strategy that maps a Duende `Client` row to the set of tenant keys it is
/// scoped to. Used by the controller hot-path (after a CRUD write, given a
/// freshly loaded <see cref="ClientDto"/>) and by the background refresh
/// sweep (given only the integer primary key).
///
/// Contract:
/// <list type="bullet">
///   <item>Returns a normalized (trim + lowercase invariant), case-insensitively
///         distinct, lexicographic-ascending, immutable list (R11.3, R11.4).</item>
///   <item>Never throws on empty input — null/zero/missing client returns the
///         empty list (R11.5).</item>
///   <item>Does NOT consume Duende's <c>IClientStore</c> (R11.6) — only the
///         BusinessLogic <see cref="Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces.IClientService"/>.</item>
/// </list>
/// </summary>
public interface IClientTenantScopeResolver
{
    /// <summary>
    /// Resolve tenant keys from an already-loaded <see cref="ClientDto"/>.
    /// Used by the controller hot-path where the DTO was just loaded by
    /// <c>IClientService.GetClientAsync</c>.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveTenantKeysAsync(
        ClientDto client,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolve tenant keys from a client primary key. Used by the background
    /// refresh sweep where only the integer id is in scope.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveTenantKeysAsync(
        int clientPrimaryKey,
        CancellationToken cancellationToken);
}
