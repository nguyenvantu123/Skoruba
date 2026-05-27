// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;

/// <summary>
/// Public-read controller exposing
/// <c>GET /api/public/tenants/{tenantKey}/clients/{clientId}</c> (HEAD on
/// the same route). Anonymous from the Duende auth perspective; gated by
/// the <c>X-Tenant-Api-Key</c> header validated by
/// <see cref="TenantApiKeyAuthorizationFilter"/>.
/// </summary>
/// <remarks>
/// <para>
/// The controller's only collaborator beyond <c>HttpContext</c>-level
/// helpers is <see cref="ITenantClientCacheService.ReadSnapshotAsync"/>
/// (R2.1, R2.7, R12.10). It deliberately does NOT inject
/// <c>IClientService</c>, <c>IClientRepository</c>,
/// <c>IAdminConfigurationDbContext</c>, or any other service that has
/// access to <c>Client.ClientSecrets</c> / <c>Claims</c> / <c>Properties</c> /
/// <c>IdentityProviderRestrictions</c>. The dependency surface is closed
/// here so the public-read path cannot accidentally read secret-bearing
/// fields.
/// </para>
/// <para>
/// Filter pipeline (R3.8 + R4.7 + R7.8):
/// </para>
/// <list type="number">
///   <item><description><see cref="HttpsRequiredFilter"/> — reject plain
///     HTTP for non-loopback hosts before any credential is read (R9.7).</description></item>
///   <item><description><see cref="TenantApiKeyAuthorizationFilter"/> —
///     gate on <c>X-Tenant-Api-Key</c> (R3.1, R3.2, R3.3).</description></item>
///   <item><description>Rate limiter policy
///     <c>"TenantClientCachePublicRead"</c> (R4) — runs after API-key
///     validation so unauthenticated requests do not consume tokens.</description></item>
///   <item><description>Action body — path validation (R7.1, R7.2),
///     <c>ReadSnapshotAsync</c> (R2.1), ETag negotiation (R6).</description></item>
///   <item><description><see cref="PublicReadExceptionFilter"/> —
///     converts any unhandled exception into 503 + Retry-After: 5
///     (R7.5, R7.8).</description></item>
/// </list>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/public/tenants")]
[EnableCors("TenantClientCachePublicRead")]
[EnableRateLimiting("TenantClientCachePublicRead")]
[ServiceFilter(typeof(HttpsRequiredFilter))]
[ServiceFilter(typeof(TenantApiKeyAuthorizationFilter))]
[ServiceFilter(typeof(PublicReadExceptionFilter))]
[Tags("PublicTenantClients")]
public sealed class PublicTenantClientsController : ControllerBase
{
    /// <summary>R7.1: max length for the URL-bound tenantKey.</summary>
    internal const int TenantKeyMaxLength = 128;

    /// <summary>R7.2: max length for the URL-bound clientId.</summary>
    internal const int ClientIdMaxLength = 200;

    /// <summary>R7.1: regex shape after <c>Trim().ToLowerInvariant()</c>.</summary>
    private static readonly Regex TenantKeyShape =
        new("^[a-z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>R6.1, R6.8: deterministic JSON serializer for the body.</summary>
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>R7.8: closed body shape for terminal failure responses.</summary>
    private const string ContentTypeJson = "application/json; charset=utf-8";

    private readonly ITenantClientCacheService _snapshots;
    private readonly IOptionsMonitor<TenantClientCachePublicReadOptions> _options;
    private readonly TenantClientCacheMetrics _metrics;
    private readonly ILogger<PublicTenantClientsController> _logger;
    private readonly IpHashHelper _ipHash;

    public PublicTenantClientsController(
        ITenantClientCacheService snapshots,
        IOptionsMonitor<TenantClientCachePublicReadOptions> options,
        TenantClientCacheMetrics metrics,
        ILogger<PublicTenantClientsController> logger,
        IpHashHelper ipHash)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ipHash = ipHash ?? throw new ArgumentNullException(nameof(ipHash));
    }

    /// <summary>
    /// Read the public-safe snapshot for <paramref name="tenantKey"/> /
    /// <paramref name="clientId"/>. See class-level remarks for the full
    /// pipeline order.
    /// </summary>
    [HttpGet("{tenantKey}/clients/{clientId}")]
    [HttpHead("{tenantKey}/clients/{clientId}")]
    [Produces("application/json")]
    public async Task<IActionResult> GetAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken)
    {
        // Caller-disconnect handling: HttpContext.RequestAborted is the
        // canonical token. The framework supplies `cancellationToken` as a
        // mirror — we still pass RequestAborted to ReadSnapshotAsync for
        // R2.8 explicitness and to retain the pattern used by the parent
        // spec.
        _ = cancellationToken;

        var sw = ValueStopwatch.StartNew();

        // R7.1 — normalize + validate tenantKey.
        var normalizedTenantKey = (tenantKey ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizedTenantKey)
            || normalizedTenantKey.Length > TenantKeyMaxLength
            || !TenantKeyShape.IsMatch(normalizedTenantKey))
        {
            return Bad("invalid_tenant_key", sw.GetElapsedMs());
        }

        // R7.2 — trim + validate clientId.
        var trimmedClientId = (clientId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmedClientId)
            || trimmedClientId.Length > ClientIdMaxLength)
        {
            return Bad("invalid_client_id", sw.GetElapsedMs());
        }

        // R2.1, R2.8 — only collaborator beyond HttpContext-level helpers.
        var envelope = await _snapshots.ReadSnapshotAsync(
            normalizedTenantKey, trimmedClientId, HttpContext.RequestAborted);

        if (envelope is null)
        {
            // R7.3 — Miss / corrupt / stale all surfaced as 404.
            return MissNotFound(normalizedTenantKey, trimmedClientId, sw.GetElapsedMs());
        }

        if (envelope.Version <= 0)
        {
            // R7.4 — sentinel envelope signals Snapshot_Pipeline_Disabled.
            // The PublicReadExceptionFilter handles the throwing variant
            // (it routes to 503 snapshot_unavailable instead of
            // snapshot_pipeline_disabled — operator runbook documents both).
            return PipelineDisabled(normalizedTenantKey, trimmedClientId, sw.GetElapsedMs());
        }

        // R6.1, R6.8 — deterministic serialize then SHA-256 hash.
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Data, Json);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bodyBytes, hash);
        var etag = "W/\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";

        // R6.4, R6.5 — If-None-Match negotiation (RFC 7232).
        var requestEtag = Request.Headers["If-None-Match"].ToString();
        if (Matches(requestEtag, etag))
        {
            WriteCommonHeaders(etag, envelope);
            EmitNotModified(normalizedTenantKey, trimmedClientId, etag, sw.GetElapsedMs());
            return StatusCode(StatusCodes.Status304NotModified);
        }

        WriteCommonHeaders(etag, envelope);
        Response.ContentType = ContentTypeJson; // R2.6

        if (HttpMethods.IsHead(Request.Method))
        {
            // R2.9 — HEAD returns identical headers + Content-Length, no body.
            Response.ContentLength = bodyBytes.Length;
            Response.StatusCode = StatusCodes.Status200OK;
            EmitHit(normalizedTenantKey, trimmedClientId, etag, sw.GetElapsedMs());
            return new EmptyResult();
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentLength = bodyBytes.Length;
        await Response.Body.WriteAsync(bodyBytes, HttpContext.RequestAborted);
        EmitHit(normalizedTenantKey, trimmedClientId, etag, sw.GetElapsedMs());
        return new EmptyResult();
    }

    /// <summary>
    /// Set the canonical response headers shared by 200 + 304 paths
    /// (R6.1, R6.2, R6.3, R6.6, R6.7, R9.8).
    /// </summary>
    private void WriteCommonHeaders(string etag, ClientCacheSnapshotEnvelope envelope)
    {
        var maxAge = _options.CurrentValue.ResponseCache.MaxAgeSeconds;

        Response.Headers["ETag"] = etag;                                              // R6.1
        Response.Headers["Cache-Control"] = "public, max-age="
            + maxAge.ToString(CultureInfo.InvariantCulture)
            + ", no-transform";                                                        // R6.2 + R9.8
        Response.Headers["Vary"] = "X-Tenant-Api-Key";                                // R6.3
        Response.Headers["X-Snapshot-Last-Write-Utc"]
            = envelope.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture);      // R6.6
        Response.Headers["X-Snapshot-Version"]
            = envelope.Version.ToString(CultureInfo.InvariantCulture);                // R6.7
        Response.Headers["X-Content-Type-Options"] = "nosniff";                       // R9.8
    }

    /// <summary>
    /// RFC 7232 <c>If-None-Match</c> matching. Accepts the wildcard
    /// <c>*</c> per R6.5, comma-separated lists, optional <c>W/</c>
    /// prefix per R6.4, and incidental whitespace around tags.
    /// </summary>
    /// <remarks>
    /// The opaque entity-tag inside the quoted string is compared
    /// case-sensitively per RFC 7232 §2.3.2. Matching strips the optional
    /// weak-validator <c>W/</c> prefix from BOTH sides before the
    /// comparison so a server-emitted weak ETag <c>W/"abc"</c> matches a
    /// client-supplied <c>"abc"</c> just as it matches <c>W/"abc"</c>.
    /// </remarks>
    internal static bool Matches(string? requestEtag, string serverEtag)
    {
        if (string.IsNullOrWhiteSpace(requestEtag))
        {
            return false;
        }

        var serverInner = StripWeakPrefix(serverEtag);

        // The header may contain a comma-separated list of entity-tags
        // (RFC 7232 §3.2: "1#entity-tag / *"). We split on commas but
        // remain tolerant of whitespace, since clients sometimes send
        // values like <c>W/"a", W/"b"</c>.
        var span = requestEtag.AsSpan();
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == ',')
            {
                var token = span.Slice(start, i - start).Trim();
                start = i + 1;
                if (token.Length == 0)
                {
                    continue;
                }

                // Wildcard — RFC 7232 §3.2 — applies regardless of position.
                if (token.Length == 1 && token[0] == '*')
                {
                    return true;
                }

                if (TokenMatches(token, serverInner))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TokenMatches(ReadOnlySpan<char> token, string serverInner)
    {
        var inner = StripWeakPrefix(token.ToString());
        return string.Equals(inner, serverInner, StringComparison.Ordinal);
    }

    private static string StripWeakPrefix(string etag)
    {
        if (etag is null)
        {
            return string.Empty;
        }

        var trimmed = etag.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == 'W' && trimmed[1] == '/')
        {
            return trimmed.Substring(2);
        }

        return trimmed;
    }

    /// <summary>
    /// 400 BadRequest with body <c>{"error":&lt;code&gt;}</c>. Emits a
    /// Warning-level audit (R8.2) AND increments
    /// <c>tenant_client_cache.public_read.bad_request</c> counter with
    /// NO tenantKey tag (R8.4 anti-enumeration).
    /// </summary>
    private IActionResult Bad(string error, double elapsedMs)
    {
        _metrics.PublicReadBadRequest();

        AuditEventPublicRead.EmitBadRequest(_logger, new AuditFields(
            EventType: AuditEventPublicRead.EventTypePrefix + AuditOutcome.BadRequest,
            TenantKey: null,                  // R8.4 anti-enumeration: redacted by helper
            ClientId: null,                    // R8.4 anti-enumeration: redacted by helper
            Outcome: AuditOutcome.BadRequest,
            DurationMs: elapsedMs,
            CorrelationId: Activity.Current?.TraceId.ToString(),
            RemoteIpHash: _ipHash.Hash(HttpContext.Connection.RemoteIpAddress),
            HttpStatus: StatusCodes.Status400BadRequest,
            ETagSent: null,
            RetryAfterSeconds: null));

        return new ObjectResult(new { error })
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { ContentTypeJson },
        };
    }

    /// <summary>
    /// 404 Not Found with body <c>{"error":"snapshot_not_found"}</c>.
    /// Emits a Debug-level Miss audit AND increments
    /// <c>tenant_client_cache.public_read.miss</c> tagged with
    /// <c>tenantKey</c> (R8.4 — Miss carries the tag).
    /// </summary>
    private IActionResult MissNotFound(string normalizedTenantKey, string trimmedClientId, double elapsedMs)
    {
        _metrics.PublicReadMiss(normalizedTenantKey, elapsedMs);

        AuditEventPublicRead.EmitMiss(_logger, new AuditFields(
            EventType: AuditEventPublicRead.EventTypePrefix + AuditOutcome.Miss,
            TenantKey: normalizedTenantKey,
            ClientId: trimmedClientId,
            Outcome: AuditOutcome.Miss,
            DurationMs: elapsedMs,
            CorrelationId: Activity.Current?.TraceId.ToString(),
            RemoteIpHash: _ipHash.Hash(HttpContext.Connection.RemoteIpAddress),
            HttpStatus: StatusCodes.Status404NotFound,
            ETagSent: null,
            RetryAfterSeconds: null));

        return new ObjectResult(new { error = "snapshot_not_found" })
        {
            StatusCode = StatusCodes.Status404NotFound,
            ContentTypes = { ContentTypeJson },
        };
    }

    /// <summary>
    /// 503 Service Unavailable with body
    /// <c>{"error":"snapshot_pipeline_disabled"}</c> + <c>Retry-After: 60</c>.
    /// Distinct from the unhandled-exception path (which yields
    /// <c>snapshot_unavailable</c> + <c>Retry-After: 5</c> via
    /// <see cref="PublicReadExceptionFilter"/>).
    /// </summary>
    private IActionResult PipelineDisabled(string normalizedTenantKey, string trimmedClientId, double elapsedMs)
    {
        _metrics.PublicReadServiceUnavailable(normalizedTenantKey);
        Response.Headers["Retry-After"] = "60"; // R7.4

        AuditEventPublicRead.EmitServiceUnavailable(_logger, new AuditFields(
            EventType: AuditEventPublicRead.EventTypePrefix + AuditOutcome.ServiceUnavailable,
            TenantKey: normalizedTenantKey,
            ClientId: trimmedClientId,
            Outcome: AuditOutcome.ServiceUnavailable,
            DurationMs: elapsedMs,
            CorrelationId: Activity.Current?.TraceId.ToString(),
            RemoteIpHash: _ipHash.Hash(HttpContext.Connection.RemoteIpAddress),
            HttpStatus: StatusCodes.Status503ServiceUnavailable,
            ETagSent: null,
            RetryAfterSeconds: 60));

        return new ObjectResult(new { error = "snapshot_pipeline_disabled" })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentTypes = { ContentTypeJson },
        };
    }

    /// <summary>Information-level Hit audit + counter + histogram (R8.2, R8.5).</summary>
    private void EmitHit(string normalizedTenantKey, string trimmedClientId, string etag, double elapsedMs)
    {
        _metrics.PublicReadHit(normalizedTenantKey, elapsedMs);

        AuditEventPublicRead.EmitHit(_logger, new AuditFields(
            EventType: AuditEventPublicRead.EventTypePrefix + AuditOutcome.Hit,
            TenantKey: normalizedTenantKey,
            ClientId: trimmedClientId,
            Outcome: AuditOutcome.Hit,
            DurationMs: elapsedMs,
            CorrelationId: Activity.Current?.TraceId.ToString(),
            RemoteIpHash: _ipHash.Hash(HttpContext.Connection.RemoteIpAddress),
            HttpStatus: StatusCodes.Status200OK,
            ETagSent: etag,
            RetryAfterSeconds: null));
    }

    /// <summary>Information-level NotModified audit + counter + histogram (R8.2, R8.5).</summary>
    private void EmitNotModified(string normalizedTenantKey, string trimmedClientId, string etag, double elapsedMs)
    {
        _metrics.PublicReadNotModified(normalizedTenantKey, elapsedMs);

        AuditEventPublicRead.EmitNotModified(_logger, new AuditFields(
            EventType: AuditEventPublicRead.EventTypePrefix + AuditOutcome.NotModified,
            TenantKey: normalizedTenantKey,
            ClientId: trimmedClientId,
            Outcome: AuditOutcome.NotModified,
            DurationMs: elapsedMs,
            CorrelationId: Activity.Current?.TraceId.ToString(),
            RemoteIpHash: _ipHash.Hash(HttpContext.Connection.RemoteIpAddress),
            HttpStatus: StatusCodes.Status304NotModified,
            ETagSent: etag,
            RetryAfterSeconds: null));
    }

    /// <summary>
    /// Lightweight stopwatch — mirrors the helper inside
    /// <c>TenantClientCacheService</c> (parent spec) so we do not allocate
    /// a <see cref="Stopwatch"/> per request.
    /// </summary>
    private readonly struct ValueStopwatch
    {
        private static readonly double TimestampToMilliseconds =
            1000.0 / Stopwatch.Frequency;

        private readonly long _start;

        private ValueStopwatch(long start) => _start = start;

        public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

        public double GetElapsedMs() =>
            (Stopwatch.GetTimestamp() - _start) * TimestampToMilliseconds;
    }
}
