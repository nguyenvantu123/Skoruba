// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// Feature: tenant-client-cache-public-read, Task 11
//
// Emits exactly one Information-level log entry on host startup that
// summarises the bound TenantClientCachePublicReadOptions snapshot per
// R1.8: tenant count + RateLimit / Cors / ResponseCache values, with
// an explicit redaction guard so neither plaintext nor SHA-256 hex
// API key values ever appear in the entry.
//
// Lifetime: registered as a hosted service (Singleton). Runs once on
// StartAsync and is then idle for the rest of the host lifetime.
//
// Validates: Requirements 1.7, 1.8, 1.10

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

/// <summary>
/// Single-shot hosted service that emits the canonical R1.8 startup audit
/// entry for the public-read endpoint. The entry contains:
/// <list type="bullet">
///   <item><description><c>TenantCount</c> — count of tenants with API
///   keys configured (count only — no key, no hash).</description></item>
///   <item><description><c>RateLimit*</c> — bound token-bucket parameters.</description></item>
///   <item><description><c>Cors*</c> — origin count + preflight TTL.</description></item>
///   <item><description><c>ResponseCacheMaxAgeSeconds</c> — bound max-age.</description></item>
///   <item><description><c>AuditLogIpHash</c> — whether IP hashing is enabled.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// This logger does NOT emit anything from the <c>ApiKeys</c> dictionary
/// values (R1.8 explicit redaction guard) — only the entry count. It does
/// not emit the <c>RemoteIpSalt</c> value either, only whether hashing is
/// enabled, since the salt is sensitive to operators per R9.6.
/// </para>
/// <para>
/// The hosted service is registered via <c>TryAddEnumerable</c> so two
/// calls to <c>AddTenantClientCachePublicRead</c> never produce duplicate
/// startup log entries.
/// </para>
/// </remarks>
internal sealed class PublicReadStartupLogger : IHostedService
{
    /// <summary>Canonical event type emitted by the startup logger.</summary>
    public const string EventType = "TenantClientCachePublicRead.StartupOptionsBound";

    private readonly IOptionsMonitor<TenantClientCachePublicReadOptions> _options;
    private readonly ILogger<PublicReadStartupLogger> _logger;

    public PublicReadStartupLogger(
        IOptionsMonitor<TenantClientCachePublicReadOptions> options,
        ILogger<PublicReadStartupLogger> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Materializing CurrentValue here triggers IValidateOptions evaluation.
        // The host already armed ValidateOnStart() in the registration extension
        // (StartupHelpers.AddTenantClientCachePublicRead) so misconfiguration
        // surfaces well before this hosted service runs. We still resolve via
        // CurrentValue (rather than IOptions<T>.Value) so a hot-reload that
        // happens between DI build and host start is reflected.
        var snapshot = _options.CurrentValue;

        // R1.8: ApiKeys count ONLY. Never log keys or hash values.
        var tenantCount = snapshot.ApiKeys?.Count ?? 0;
        var corsOriginCount = snapshot.Cors?.AllowedOrigins?.Count ?? 0;

        // The bound options expose non-nullable sub-records by default; the
        // null-coalesce protects against a configuration overlay that
        // explicitly sets a sub-section to null (theoretically possible via
        // raw IConfiguration manipulation in tests).
        var rateLimit = snapshot.RateLimit ?? new TenantClientCachePublicReadOptions.RateLimitOptions();
        var cors = snapshot.Cors ?? new TenantClientCachePublicReadOptions.CorsOptions();
        var responseCache = snapshot.ResponseCache ?? new TenantClientCachePublicReadOptions.ResponseCacheOptions();
        var audit = snapshot.Audit ?? new TenantClientCachePublicReadOptions.AuditOptions();

        _logger.LogInformation(
            "{EventType} TenantCount={TenantCount} "
            + "RateLimitTokenLimit={RateLimitTokenLimit} "
            + "RateLimitTokensPerPeriod={RateLimitTokensPerPeriod} "
            + "RateLimitReplenishmentPeriod={RateLimitReplenishmentPeriod} "
            + "RateLimitQueueLimit={RateLimitQueueLimit} "
            + "RateLimitAutoReplenishment={RateLimitAutoReplenishment} "
            + "CorsAllowedOriginCount={CorsAllowedOriginCount} "
            + "CorsPreflightMaxAgeSeconds={CorsPreflightMaxAgeSeconds} "
            + "ResponseCacheMaxAgeSeconds={ResponseCacheMaxAgeSeconds} "
            + "AuditLogIpHash={AuditLogIpHash}",
            EventType,
            tenantCount,
            rateLimit.TokenLimit,
            rateLimit.TokensPerPeriod,
            rateLimit.ReplenishmentPeriod,
            rateLimit.QueueLimit,
            rateLimit.AutoReplenishment,
            corsOriginCount,
            cors.PreflightMaxAgeSeconds,
            responseCache.MaxAgeSeconds,
            audit.LogIpHash);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
