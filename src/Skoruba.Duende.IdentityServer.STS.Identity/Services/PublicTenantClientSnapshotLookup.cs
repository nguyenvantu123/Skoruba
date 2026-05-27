// Feature: tenant-client-cache-public-read, Task 11/12 — STS.Identity consumer wrapper
//
// Result envelope returned from <see cref="IPublicTenantClientSnapshotProvider"/>.
// Adapts the eight-way SDK Sdk_Cache_Outcome into a simpler, host-side contract
// that downstream services and controllers can switch on. Snapshot values are
// pass-through references to the SDK Public_Safe_Fields DTO — no deep copy.

#nullable enable

using System;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

/// <summary>
/// Terminal outcomes a caller of <see cref="IPublicTenantClientSnapshotProvider"/>
/// can observe. Distinct from the SDK <see cref="SdkCacheOutcome"/> because the
/// host wrapper folds in two pre-SDK conditions —
/// <see cref="Disabled"/> (consumer not configured) and
/// <see cref="NoTenantContext"/> (no tenant resolved on the current request) —
/// plus a guard against caller bugs (<see cref="InvalidClientId"/>).
/// </summary>
public enum PublicClientSnapshotOutcome
{
    /// <summary>The consumer wrapper is not enabled in configuration. SDK was never called.</summary>
    Disabled,

    /// <summary>
    /// <c>ITenantContextAccessor.Current</c> was <see langword="null"/>. The caller
    /// must resolve a tenant (subdomain or <c>X-Tenant-Id</c> header) before invoking
    /// the provider.
    /// </summary>
    NoTenantContext,

    /// <summary>
    /// The caller passed a <see langword="null"/>, empty, or whitespace-only
    /// <c>clientId</c>. Indicates a caller bug; the SDK was never called.
    /// </summary>
    InvalidClientId,

    /// <summary>
    /// SDK returned a snapshot. Maps from
    /// <see cref="SdkCacheOutcome.Hit"/>, <see cref="SdkCacheOutcome.Miss"/>, or
    /// <see cref="SdkCacheOutcome.NotModified"/>.
    /// </summary>
    Snapshot,

    /// <summary>SDK returned 404 Not Found.</summary>
    NotFound,

    /// <summary>SDK returned 401 Unauthorized.</summary>
    Unauthorized,

    /// <summary>SDK returned 429 Too Many Requests.</summary>
    RateLimited,

    /// <summary>SDK returned 503 Service Unavailable or exhausted transient retries.</summary>
    Unavailable,
}

/// <summary>
/// Result envelope returned from
/// <see cref="IPublicTenantClientSnapshotProvider.GetSnapshotAsync(string, System.Threading.CancellationToken)"/>.
/// </summary>
/// <param name="Snapshot">
/// The Public_Safe_Fields snapshot when <paramref name="Outcome"/> is
/// <see cref="PublicClientSnapshotOutcome.Snapshot"/>; <see langword="null"/>
/// otherwise.
/// </param>
/// <param name="Outcome">The terminal outcome (see <see cref="PublicClientSnapshotOutcome"/>).</param>
/// <param name="RetryAfter">
/// Hint surfaced from a server <c>Retry-After</c> header for
/// <see cref="PublicClientSnapshotOutcome.RateLimited"/> /
/// <see cref="PublicClientSnapshotOutcome.Unavailable"/>. The wrapper does NOT
/// auto-wait — callers schedule the retry on their own terms.
/// </param>
public sealed record PublicClientSnapshotLookup(
    PublicClientSnapshot? Snapshot,
    PublicClientSnapshotOutcome Outcome,
    TimeSpan? RetryAfter);
