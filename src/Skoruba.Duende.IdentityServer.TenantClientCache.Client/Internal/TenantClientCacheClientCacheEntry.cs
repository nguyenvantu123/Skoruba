// Feature: tenant-client-cache-public-read
// Internal in-memory cache entry held by IMemoryCache inside the SDK.
// Concrete behavior (TTL, set/get) is implemented in Task 9; this Task 7
// only introduces the type so models compile in isolation.

using System;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;

/// <summary>
/// Cache holder for the SDK's in-memory revalidation flow (R11.6, R11.9).
/// Stores the snapshot together with the ETag and metadata needed to issue
/// an <c>If-None-Match</c> request after the local TTL expires.
/// </summary>
internal sealed record TenantClientCacheClientCacheEntry(
    PublicClientSnapshot Snapshot,
    string? Etag,
    DateTimeOffset? LastWriteUtc,
    int? Version);
