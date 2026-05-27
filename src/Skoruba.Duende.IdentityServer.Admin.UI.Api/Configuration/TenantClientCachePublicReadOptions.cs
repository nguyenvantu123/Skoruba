// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

/// <summary>
/// Strongly typed options bound from the <c>TenantClientCachePublicRead</c>
/// configuration section. Drives the public-read endpoint
/// <c>GET /api/public/tenants/{tenantKey}/clients/{clientId}</c> exposed by the
/// Admin_Api_Host (feature <c>tenant-client-cache-public-read</c>).
/// </summary>
/// <remarks>
/// Default values mirror the <c>TenantClientCachePublicReadOptions</c> entry
/// in the design glossary (R1.2, R1.3, R4.2, R5.7, R6.2). Verbatim defaults:
/// <list type="bullet">
///   <item><description><see cref="ApiKeys"/> = empty <see cref="Dictionary{TKey,TValue}"/> (R1.3 — fail-closed).</description></item>
///   <item><description><see cref="RateLimit"/>: TokenLimit = 30, TokensPerPeriod = 30, ReplenishmentPeriod = 1 minute, QueueLimit = 0, AutoReplenishment = true.</description></item>
///   <item><description><see cref="Cors"/>: AllowedOrigins = empty list (R5.4), PreflightMaxAgeSeconds = 600.</description></item>
///   <item><description><see cref="ResponseCache"/>: MaxAgeSeconds = 60.</description></item>
///   <item><description><see cref="Audit"/>: LogIpHash = true, RemoteIpSalt = empty (validator requires non-empty in Production per R9.6).</description></item>
/// </list>
/// </remarks>
public sealed class TenantClientCachePublicReadOptions
{
    /// <summary>
    /// Configuration section name (root key in <c>appsettings.json</c>).
    /// </summary>
    public const string SectionName = "TenantClientCachePublicRead";

    /// <summary>
    /// Map of <c>tenantKey</c> (lowercased, trimmed) → SHA-256 hex lowercase
    /// of the per-tenant API key. Source of truth for header
    /// <c>X-Tenant-Api-Key</c> validation (R1.2, R1.4, R1.5).
    /// </summary>
    /// <remarks>
    /// The dictionary uses an <see cref="StringComparer.Ordinal"/> comparer to
    /// keep validator failures deterministic (uppercase keys MUST fail-fast,
    /// not be silently coerced). Hot reload is observed via
    /// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>
    /// (R1.6, R1.9).
    /// </remarks>
    public IDictionary<string, string> ApiKeys { get; set; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Token-bucket rate limit applied per-tenant on the public-read endpoint
    /// (R4). Partition key is the URL-bound <c>tenantKey</c>.
    /// </summary>
    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary>
    /// Strict allowlist CORS policy for the public-read endpoint (R5).
    /// Default: zero origins (cross-origin browser requests rejected).
    /// </summary>
    public CorsOptions Cors { get; set; } = new();

    /// <summary>
    /// Cache directives surfaced via <c>Cache-Control: max-age</c> on the
    /// public-read response (R6.2).
    /// </summary>
    public ResponseCacheOptions ResponseCache { get; set; } = new();

    /// <summary>
    /// Audit-event configuration. <c>RemoteIpSalt</c> MUST be non-empty in
    /// the <c>Production</c> environment (R9.6).
    /// </summary>
    public AuditOptions Audit { get; set; } = new();

    /// <summary>
    /// Token-bucket rate limiter configuration (R4.2 – R4.4).
    /// </summary>
    public sealed class RateLimitOptions
    {
        /// <summary>R4.2/R4.3: max tokens in the bucket. Range [1, 10000].</summary>
        public int TokenLimit { get; set; } = 30;

        /// <summary>R4.2: tokens added per replenishment period.</summary>
        public int TokensPerPeriod { get; set; } = 30;

        /// <summary>R4.2/R4.4: bucket replenishment period. Range [00:00:01, 01:00:00].</summary>
        public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>R4.2: queue depth (default 0 — reject immediately on bucket empty).</summary>
        public int QueueLimit { get; set; } = 0;

        /// <summary>R4.2: auto-replenish bucket on a timer (default true).</summary>
        public bool AutoReplenishment { get; set; } = true;
    }

    /// <summary>
    /// CORS policy configuration (R5).
    /// </summary>
    public sealed class CorsOptions
    {
        /// <summary>
        /// Allowed origins (absolute URLs, https-only or http+localhost per R5.6).
        /// Default: empty list — zero origins (R5.4).
        /// </summary>
        public IList<string> AllowedOrigins { get; set; } = new List<string>();

        /// <summary>R5.7: preflight cache duration. Range [0, 86400].</summary>
        public int PreflightMaxAgeSeconds { get; set; } = 600;
    }

    /// <summary>
    /// HTTP response caching directives (R6.2).
    /// </summary>
    public sealed class ResponseCacheOptions
    {
        /// <summary>R6.2: <c>Cache-Control: max-age=N</c>. Range [0, 3600].</summary>
        public int MaxAgeSeconds { get; set; } = 60;
    }

    /// <summary>
    /// Audit event configuration (R3.6, R9.6).
    /// </summary>
    public sealed class AuditOptions
    {
        /// <summary>R3.6: emit hashed remote IP in audit log entries.</summary>
        public bool LogIpHash { get; set; } = true;

        /// <summary>
        /// R9.6: per-host random salt mixed with the remote IP before hashing.
        /// MUST be non-empty in the <c>Production</c> environment (validator enforces).
        /// </summary>
        public string RemoteIpSalt { get; set; } = string.Empty;
    }
}
