// Feature: tenant-client-cache-public-read
// Options POCO for the SDK consumer.
// Validation rules (R10.7, R10.8) are wired in Task 9 via
// AddTenantClientCacheClient; this Task 7 contributes only the
// strongly-typed shape.

using System;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client;

/// <summary>
/// Options POCO for <see cref="ITenantClientCacheClient"/>.
/// </summary>
/// <remarks>
/// Defaults match the design document section "TenantClientCacheClientOptions"
/// (R10.7, R11.1, R11.3, R11.6).
/// </remarks>
public sealed class TenantClientCacheClientOptions
{
    /// <summary>
    /// Absolute base URL of the public-read endpoint host (e.g. <c>https://identity.example.com</c>).
    /// MUST be an absolute https URL (or http://localhost for development). R10.7, R10.8.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Per-tenant API key sent in the <c>X-Tenant-Api-Key</c> header on every request (R10.7).
    /// MUST be non-empty.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// HTTP client timeout (R10.7, R11.12). Default 5 seconds. Range [1s, 60s].
    /// </summary>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of additional retry attempts after the initial call (R11.1).
    /// Default 2. Range [0, 5].
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>
    /// Base delay for exponential backoff between retries (R11.3). Default 200 ms.
    /// Range [10ms, 5s].
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Upper bound on the in-memory cache TTL when the server's
    /// <c>Cache-Control: max-age</c> directive is larger than this value (R11.6).
    /// Default 5 minutes. Range [0s, 1h]. Setting to <see cref="TimeSpan.Zero"/>
    /// disables the in-memory cache (consult <see cref="EnableInMemoryCaching"/>
    /// for the explicit on/off toggle).
    /// </summary>
    public TimeSpan MaxClientCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Master switch for the SDK's in-memory cache (R11.6). Default <see langword="true"/>.
    /// When <see langword="false"/>, every call issues an HTTP request and the
    /// <c>If-None-Match</c> revalidation flow is bypassed.
    /// </summary>
    public bool EnableInMemoryCaching { get; set; } = true;
}
