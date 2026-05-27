// Feature: tenant-client-cache-public-read
// Result envelope returned from <see cref="ITenantClientCacheClient"/>.
// See design.md, section "Models/TenantClientSnapshotResult.cs" (R10.4, R11.4).

using System;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

/// <summary>
/// Result envelope returned by
/// <see cref="ITenantClientCacheClient.GetClientAsync(string, string, System.Threading.CancellationToken)"/>.
/// </summary>
/// <param name="Snapshot">
/// The Public_Safe_Fields snapshot. <see langword="null"/> for non-success outcomes
/// (or for <see cref="SdkCacheOutcome.NotModified"/> when no prior cache entry exists).
/// </param>
/// <param name="Etag">The server-supplied weak ETag, when present.</param>
/// <param name="LastWriteUtc">
/// Timestamp from the <c>X-Snapshot-Last-Write-Utc</c> response header, when present.
/// </param>
/// <param name="Version">Snapshot version from the <c>X-Snapshot-Version</c> response header, when present.</param>
/// <param name="Outcome">Terminal outcome the SDK observed (R10.4).</param>
/// <param name="RetryAfter">
/// Hint from a <c>Retry-After</c> response header, surfaced as <see cref="TimeSpan"/> for
/// <see cref="SdkCacheOutcome.RateLimited"/> / <see cref="SdkCacheOutcome.ServiceUnavailable"/>
/// (R11.4). The SDK does NOT auto-wait — it surfaces the value so the caller can schedule
/// a retry on its own terms.
/// </param>
public sealed record TenantClientSnapshotResult(
    PublicClientSnapshot? Snapshot,
    string? Etag,
    DateTimeOffset? LastWriteUtc,
    int? Version,
    SdkCacheOutcome Outcome,
    TimeSpan? RetryAfter);
