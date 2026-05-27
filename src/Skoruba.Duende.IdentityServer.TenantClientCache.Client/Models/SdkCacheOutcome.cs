// Feature: tenant-client-cache-public-read
// Glossary: Sdk_Cache_Outcome (R10.4)
//
// Mirrors the eight terminal outcomes a consumer can observe from
// <see cref="ITenantClientCacheClient"/>. Distinct from the server-side
// Cache_Outcome enum (which lives in spec tenant-client-cache-expansion).

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

/// <summary>
/// Terminal outcomes a consumer can observe when calling
/// <see cref="ITenantClientCacheClient.GetClientAsync(string, string, System.Threading.CancellationToken)"/>.
/// </summary>
public enum SdkCacheOutcome
{
    /// <summary>Local in-memory cache hit (R11.7). No HTTP traffic was issued.</summary>
    Hit,

    /// <summary>Server returned 200 OK with a fresh body (cache was populated/updated).</summary>
    Miss,

    /// <summary>Server returned 304 Not Modified (R11.9).</summary>
    NotModified,

    /// <summary>Server returned 404 Not Found (R7.3).</summary>
    NotFound,

    /// <summary>Server returned 401 Unauthorized (R3.1, R3.2).</summary>
    Unauthorized,

    /// <summary>Server returned 429 Too Many Requests (R4.5).</summary>
    RateLimited,

    /// <summary>Server returned 503 Service Unavailable (R7.4, R7.5).</summary>
    ServiceUnavailable,

    /// <summary>5xx exhausted retries OR an unknown 4xx response was folded into a single fail-soft bucket.</summary>
    TransientFailure
}
