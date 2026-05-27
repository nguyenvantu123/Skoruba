// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

#nullable enable

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

/// <summary>
/// Authorization filter that gates the public-read endpoint
/// <c>GET /api/public/tenants/{tenantKey}/clients/{clientId}</c> behind the
/// per-tenant API key carried by header <c>X-Tenant-Api-Key</c>
/// (R3.1, R3.2, R3.3, R3.4, R3.7).
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: <c>Singleton</c>. The filter is stateless and resolves no
/// scoped collaborators directly — <see cref="ITenantApiKeyValidator"/> is a
/// singleton, the logger is the framework's hosted singleton instance, and
/// <see cref="IOptionsMonitor{TOptions}"/> is by definition singleton.
/// </para>
/// <para>
/// The filter runs AFTER <see cref="HttpsRequiredFilter"/> and BEFORE the
/// rate limiter (R3.8 + R4.7) so unauthenticated requests do not consume
/// per-tenant rate-limit tokens. On 401 the filter:
/// </para>
/// <list type="number">
///   <item><description>Increments the
///     <c>tenant_client_cache.public_read.unauthorized</c> counter via
///     <see cref="TenantClientCacheMetrics.PublicReadUnauthorized"/> with
///     no <c>tenantKey</c> tag (R8.4 anti-enumeration).</description></item>
///   <item><description>Emits a structured Warning log with
///     <c>EventType, Outcome, CorrelationId, RemoteIpHash</c> (R3.4, R8.7).
///     The raw header value, the SHA-256 hash, and the raw <c>tenantKey</c>
///     route value are deliberately NOT included in any log field.</description></item>
///   <item><description>Returns HTTP 401 with body
///     <c>{"error":"missing_api_key"}</c> when the header is absent /
///     whitespace, or <c>{"error":"invalid_api_key"}</c> when the
///     credential does not match (R3.1, R3.2, R3.3 — the body is identical
///     for "wrong key" and "tenant not registered" to defeat enumeration).</description></item>
/// </list>
/// </remarks>
internal sealed class TenantApiKeyAuthorizationFilter : IAsyncAuthorizationFilter
{
    /// <summary>
    /// Header name carrying the tenant API key. The filter consults
    /// ONLY this header (R3.7) — query string, cookies, and request body
    /// are intentionally ignored.
    /// </summary>
    public const string HeaderName = "X-Tenant-Api-Key";

    /// <summary>Route token containing the path-bound tenant key.</summary>
    private const string TenantKeyRouteKey = "tenantKey";

    /// <summary>EventType prefix for unauthorized audit log entries (R8.1).</summary>
    private const string EventType = "TenantClientCachePublicRead.Unauthorized";

    private readonly ITenantApiKeyValidator _validator;
    private readonly ILogger<TenantApiKeyAuthorizationFilter> _logger;
    private readonly IOptionsMonitor<TenantClientCachePublicReadOptions> _options;
    private readonly TenantClientCacheMetrics _metrics;
    private readonly IpHashHelper _ipHash;

    public TenantApiKeyAuthorizationFilter(
        ITenantApiKeyValidator validator,
        ILogger<TenantApiKeyAuthorizationFilter> logger,
        IOptionsMonitor<TenantClientCachePublicReadOptions> options,
        TenantClientCacheMetrics metrics,
        IpHashHelper ipHash)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _ipHash = ipHash ?? throw new ArgumentNullException(nameof(ipHash));
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Defence-in-depth: silence the "unused field" warning for _options.
        // The reference is retained for future use (e.g. additional filter
        // toggles) and to keep the dependency surface stable for Task 6.
        _ = _options;

        var headers = context.HttpContext.Request.Headers;

        // R3.7 — only the X-Tenant-Api-Key header is consulted; the filter
        // never inspects query string, cookies, or request body.
        if (!headers.TryGetValue(HeaderName, out var raw)
            || raw.Count == 0
            || string.IsNullOrWhiteSpace(raw.ToString()))
        {
            ShortCircuit(context, "missing_api_key");
            return Task.CompletedTask;
        }

        // R2.3 — caller-side normalization for the validator lookup. The
        // path-level shape regex (^[a-z0-9_-]+$) runs in the route layer;
        // normalizing here ensures we're robust if the controller is wired
        // without that constraint in tests.
        var tenantKey = ((string?)context.RouteData.Values[TenantKeyRouteKey] ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        // raw.ToString() is stable across StringValues sizes; we pass a
        // span over it into the validator so the plaintext never leaves the
        // stack as a stored field.
        var headerValue = raw.ToString();

        if (!_validator.TryValidate(tenantKey, headerValue.AsSpan()))
        {
            // R3.3 — DO NOT differentiate "tenant not registered" vs "wrong
            // key" in either status code or response body.
            ShortCircuit(context, "invalid_api_key");
            return Task.CompletedTask;
        }

        // valid → fall through; rate limiter (R4) runs next.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Emit unauthorized telemetry + the JSON 401 response. Centralising
    /// the response shape here ensures byte-equality between
    /// <c>missing_api_key</c> and <c>invalid_api_key</c> outcomes
    /// (anti-enumeration, R3.3).
    /// </summary>
    private void ShortCircuit(AuthorizationFilterContext context, string error)
    {
        // R8.4 — the unauthorized counter has NO tenantKey tag so dashboards
        // cannot be used to enumerate registered tenants.
        _metrics.PublicReadUnauthorized();

        // R3.4 / R8.7 — log NO raw header, NO SHA-256 hash, NO raw tenantKey.
        // Only request-scoped fields are emitted. RemoteIpHash is null when
        // Audit:LogIpHash is false (R3.6).
        _logger.LogWarning(
            "{EventType} outcome={Outcome} corr={CorrelationId} remoteIpHash={RemoteIpHash}",
            EventType,
            "Unauthorized",
            Activity.Current?.TraceId.ToString(),
            _ipHash.Hash(context.HttpContext.Connection.RemoteIpAddress));

        context.Result = new ObjectResult(new { error })
        {
            StatusCode = StatusCodes.Status401Unauthorized,
            ContentTypes = { "application/json; charset=utf-8" },
        };
    }
}
