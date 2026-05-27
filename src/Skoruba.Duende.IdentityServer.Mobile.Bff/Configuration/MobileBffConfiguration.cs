// Feature: tenant-client-cache-public-read — Mobile BFF host.
//
// Strongly-typed configuration root for the BFF. The BFF derives `tenantKey`
// from the user's `tenant_key` JWT claim and never trusts the request URL /
// body / headers for the tenant identifier. The per-tenant API key for the
// public-read endpoint is held server-side here; it must never reach a Flutter
// binary.
//
// Validation rules are enforced in Program.cs via
// `services.AddOptions<MobileBffConfiguration>().ValidateOnStart()` so the
// host fails fast on misconfiguration instead of degrading at request time.

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.Configuration;

/// <summary>
/// Root configuration POCO bound from the <c>MobileBff</c> section of
/// <c>appsettings.json</c> (and overrides).
/// </summary>
public sealed class MobileBffConfiguration
{
    /// <summary>
    /// Configuration section name used by <see cref="Microsoft.Extensions.Options"/>.
    /// </summary>
    public const string SectionName = "MobileBff";

    /// <summary>JWT bearer authentication settings (Skoruba STS issuer).</summary>
    public AuthenticationConfig Authentication { get; set; } = new();

    /// <summary>Settings for the upstream public-read SDK.</summary>
    public TenantClientCacheConfig TenantClientCache { get; set; } = new();

    /// <summary>Rate-limiter settings (anonymous bootstrap endpoint).</summary>
    public RateLimitingConfig RateLimiting { get; set; } = new();

    /// <summary>JWT bearer authentication settings.</summary>
    public sealed class AuthenticationConfig
    {
        /// <summary>OIDC authority (issuer) URL — e.g. <c>https://sts.example.com</c>.</summary>
        public string Authority { get; set; } = string.Empty;

        /// <summary>
        /// When <see langword="true"/> (default), the JWT handler requires
        /// <c>https</c> for metadata discovery. Only set <see langword="false"/>
        /// in local development against <c>https://sts.dev.localhost:5001</c>.
        /// </summary>
        public bool RequireHttpsMetadata { get; set; } = true;

        /// <summary>
        /// Optional audience (<c>aud</c>) to validate. When <see langword="null"/>
        /// or empty, audience validation is disabled — matching the STS.Identity
        /// pattern in <c>StartupHelpers.cs</c>.
        /// </summary>
        public string? Audience { get; set; }
    }

    /// <summary>Settings for the SDK that talks to the public-read endpoint.</summary>
    public sealed class TenantClientCacheConfig
    {
        /// <summary>
        /// Absolute base URL of the Admin host that exposes the public-read
        /// endpoint (e.g. <c>https://identity.example.com</c>).
        /// </summary>
        public string BaseAddress { get; set; } = string.Empty;

        /// <summary>
        /// Per-tenant API key sent in the <c>X-Tenant-Api-Key</c> header.
        /// Server-side secret — never expose to the mobile client.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>HTTP timeout in seconds. Range [1, 60]. Default 5.</summary>
        public int HttpTimeoutSeconds { get; set; } = 5;

        /// <summary>Maximum SDK retry attempts. Range [0, 5]. Default 2.</summary>
        public int MaxRetryAttempts { get; set; } = 2;

        /// <summary>
        /// Upper bound on the SDK's in-memory cache TTL, in seconds.
        /// Range [0, 3600]. Default 300.
        /// </summary>
        public int MaxClientCacheTtlSeconds { get; set; } = 300;
    }

    /// <summary>
    /// Rate-limiter settings for the anonymous bootstrap endpoint
    /// (<c>GET /mobile/bootstrap/{tenantKey}/{clientId}</c>). Partition key
    /// is the caller's IP; the existing post-auth endpoint
    /// (<c>GET /mobile/clients/{clientId}</c>) is NOT rate-limited here
    /// because the upstream Admin host already enforces a per-tenant token
    /// bucket on its public-read endpoint.
    /// </summary>
    public sealed class RateLimitingConfig
    {
        /// <summary>
        /// Maximum permits per window per IP for the bootstrap endpoint.
        /// Range [1, 1000]. Default 10.
        /// </summary>
        public int BootstrapPermitLimit { get; set; } = 10;

        /// <summary>
        /// Window length in seconds for the bootstrap rate limiter.
        /// Range [1, 3600]. Default 60.
        /// </summary>
        public int BootstrapWindowSeconds { get; set; } = 60;

        /// <summary>
        /// Queue depth for the bootstrap rate limiter. Range [0, 100].
        /// Default 0 (no queueing — rejected callers receive 429 immediately).
        /// </summary>
        public int BootstrapQueueLimit { get; set; } = 0;
    }
}
