// Feature: tenant-client-cache-public-read — Mobile BFF host (cold-start).
//
// Anonymous bootstrap endpoint: GET /mobile/bootstrap/{tenantKey}/{clientId}.
//
// Why this exists
// ───────────────
// The companion endpoint `GET /mobile/clients/{clientId}` requires a
// validated Bearer token AND derives `tenantKey` from the user's
// `tenant_key` claim. A freshly-installed Flutter app has no token yet —
// it cannot acquire one without first knowing the OIDC authority,
// clientId, redirectUri, and allowed scopes. That's the chicken-and-egg
// problem this endpoint solves.
//
// The bootstrap surface intentionally exposes ONLY the minimum metadata
// required to start a PKCE flow:
//   authority, clientId, clientName, redirectUris, postLogoutRedirectUris,
//   allowedScopes, allowedGrantTypes, requirePkce.
//
// No token lifetimes. No logout URIs. No secrets / claims / properties.
// (See `MobileClientBootstrapResponse` for the closed shape.)
//
// Anti-enumeration:
//   * NotFound responses do NOT distinguish "tenant doesn't exist" vs
//     "client doesn't exist within tenant" — same `client_not_found`
//     error in both cases (R3.3-style).
//   * IP-based fixed-window rate limiter applied via the policy
//     `MobileBff_Bootstrap`. The handler runs only after the limiter
//     accepts the request.
//
// Logging policy:
//   * Outcome logged at Information with structured
//     `{TenantKey, ClientId, Outcome, RemoteIp}`. RemoteIp is acceptable
//     here because the endpoint is anonymous and IP is the only identity
//     available for rate-limit attribution.
//   * Never log API key or full snapshot body.

using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Skoruba.Duende.IdentityServer.Mobile.Bff.Configuration;
using Skoruba.Duende.IdentityServer.Mobile.Bff.Models;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.Endpoints;

/// <summary>
/// Endpoint registration for the anonymous cold-start bootstrap surface.
/// </summary>
internal static partial class MobileBootstrapEndpoints
{
    /// <summary>Rate-limiter policy name applied to the bootstrap endpoint.</summary>
    public const string RateLimitPolicyName = "MobileBff_Bootstrap";

    private const int MaxTenantKeyLength = 128;
    private const int MaxClientIdLength = 200;

    // The bootstrap response is identical for every anonymous caller of
    // the same (tenantKey, clientId) tuple — there is no per-user state
    // in the body. That makes it CDN-cacheable, so we use `public` rather
    // than `private`. Trade-off: revoking a misconfigured client takes
    // up to MaxAge seconds to propagate downstream. Acceptable because
    // OIDC client metadata changes infrequently and post-auth traffic
    // uses the authenticated `/mobile/clients/{id}` endpoint with
    // `Cache-Control: private, max-age=60` instead.
    private const int BootstrapMaxAgeSeconds = 300;

    [GeneratedRegex(@"^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TenantKeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_:./-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientIdPattern();

    /// <summary>
    /// Map <c>GET /mobile/bootstrap/{tenantKey}/{clientId}</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapMobileBootstrapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/mobile/bootstrap/{tenantKey}/{clientId}", HandleBootstrapAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicyName)
            .WithName("Mobile_Bootstrap");

        return endpoints;
    }

    internal static async Task<IResult> HandleBootstrapAsync(
        string tenantKey,
        string clientId,
        HttpContext httpContext,
        ITenantClientCacheClient sdk,
        IOptionsSnapshot<MobileBffConfiguration> bffOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(sdk);
        ArgumentNullException.ThrowIfNull(bffOptions);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger("Skoruba.Duende.IdentityServer.Mobile.Bff.MobileBootstrapEndpoints");
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // 1) Validate tenantKey shape (mirror server-side public-read rules).
        var normalizedTenantKey = (tenantKey ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizedTenantKey)
            || normalizedTenantKey.Length > MaxTenantKeyLength
            || !TenantKeyPattern().IsMatch(normalizedTenantKey))
        {
            logger.LogInformation(
                "Mobile BFF bootstrap rejected: invalid tenant_key. Outcome={Outcome} RemoteIp={RemoteIp}",
                "invalid_tenant_key",
                remoteIp);
            return Results.BadRequest(new { error = "invalid_tenant_key" });
        }

        // 2) Validate clientId shape.
        if (string.IsNullOrWhiteSpace(clientId)
            || clientId.Length > MaxClientIdLength
            || !ClientIdPattern().IsMatch(clientId))
        {
            logger.LogInformation(
                "Mobile BFF bootstrap rejected: invalid client_id. TenantKey={TenantKey} Outcome={Outcome} RemoteIp={RemoteIp}",
                normalizedTenantKey,
                "invalid_client_id",
                remoteIp);
            return Results.BadRequest(new { error = "invalid_client_id" });
        }

        // 3) Optional pass-through revalidation header.
        string? ifNoneMatch = null;
        if (httpContext.Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var headerValues))
        {
            var raw = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                ifNoneMatch = raw;
            }
        }

        // 4) Resolve authority from per-request options snapshot so test
        //    overlays take effect.
        var authority = (bffOptions.Value.Authentication.Authority ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(authority))
        {
            // Fail-fast configuration validator should have caught this
            // at startup, but guard anyway so a partial config never
            // produces a malformed bootstrap body.
            logger.LogError(
                "Mobile BFF bootstrap aborted: Authentication.Authority not configured. TenantKey={TenantKey} ClientId={ClientId} RemoteIp={RemoteIp}",
                normalizedTenantKey,
                clientId,
                remoteIp);
            return Results.Json(new { error = "upstream_misconfigured" }, statusCode: StatusCodes.Status502BadGateway);
        }

        // 5) Call the SDK.
        TenantClientSnapshotResult result;
        try
        {
            result = await sdk.GetClientAsync(normalizedTenantKey, clientId, ifNoneMatch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Mobile BFF bootstrap SDK call threw unexpectedly. TenantKey={TenantKey} ClientId={ClientId} RemoteIp={RemoteIp}",
                normalizedTenantKey,
                clientId,
                remoteIp);
            ApplyRetryAfterHeader(httpContext, retryAfter: null);
            return Results.Json(new { error = "snapshot_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // 6) Map outcomes.
        switch (result.Outcome)
        {
            case SdkCacheOutcome.Hit:
            case SdkCacheOutcome.Miss:
            {
                if (result.Snapshot is null)
                {
                    logger.LogError(
                        "Mobile BFF bootstrap received {Outcome} without snapshot. TenantKey={TenantKey} ClientId={ClientId} RemoteIp={RemoteIp}",
                        result.Outcome,
                        normalizedTenantKey,
                        clientId,
                        remoteIp);
                    return Results.Json(new { error = "snapshot_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                // 304 short-circuit: client already has a fresh copy.
                if (!string.IsNullOrEmpty(ifNoneMatch)
                    && !string.IsNullOrWhiteSpace(result.Etag)
                    && string.Equals(ifNoneMatch, result.Etag, StringComparison.Ordinal))
                {
                    ApplyResponseHeaders(httpContext, result.Etag);
                    logger.LogInformation(
                        "Mobile BFF bootstrap returning 304 (If-None-Match match). TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome} RemoteIp={RemoteIp}",
                        normalizedTenantKey,
                        clientId,
                        result.Outcome,
                        remoteIp);
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                ApplyResponseHeaders(httpContext, result.Etag);
                logger.LogInformation(
                    "Mobile BFF bootstrap returning 200. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome} RemoteIp={RemoteIp}",
                    normalizedTenantKey,
                    clientId,
                    result.Outcome,
                    remoteIp);
                return Results.Ok(MobileClientBootstrapResponse.From(result.Snapshot, authority));
            }

            case SdkCacheOutcome.NotModified:
            {
                ApplyResponseHeaders(httpContext, result.Etag);
                logger.LogInformation(
                    "Mobile BFF bootstrap returning 304. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome} RemoteIp={RemoteIp}",
                    normalizedTenantKey,
                    clientId,
                    result.Outcome,
                    remoteIp);
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            case SdkCacheOutcome.NotFound:
            {
                // Anti-enumeration: same shape regardless of whether the
                // tenant or the client is the missing piece.
                logger.LogInformation(
                    "Mobile BFF bootstrap returning 404. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome} RemoteIp={RemoteIp}",
                    normalizedTenantKey,
                    clientId,
                    result.Outcome,
                    remoteIp);
                return Results.NotFound(new { error = "client_not_found" });
            }

            case SdkCacheOutcome.Unauthorized:
            {
                logger.LogError(
                    "Mobile BFF bootstrap received Unauthorized from public-read endpoint — BFF API key may be invalid. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome} RemoteIp={RemoteIp}",
                    normalizedTenantKey,
                    clientId,
                    result.Outcome,
                    remoteIp);
                return Results.Json(new { error = "upstream_misconfigured" }, statusCode: StatusCodes.Status502BadGateway);
            }

            case SdkCacheOutcome.RateLimited:
            case SdkCacheOutcome.ServiceUnavailable:
            case SdkCacheOutcome.TransientFailure:
            {
                ApplyRetryAfterHeader(httpContext, result.RetryAfter);
                logger.LogInformation(
                    "Mobile BFF bootstrap returning 503. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome} RemoteIp={RemoteIp}",
                    normalizedTenantKey,
                    clientId,
                    result.Outcome,
                    remoteIp);
                return Results.Json(new { error = "snapshot_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            default:
            {
                logger.LogError(
                    "Mobile BFF bootstrap received unknown outcome. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome} RemoteIp={RemoteIp}",
                    normalizedTenantKey,
                    clientId,
                    result.Outcome,
                    remoteIp);
                return Results.Json(new { error = "snapshot_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }
    }

    private static void ApplyResponseHeaders(HttpContext httpContext, string? etag)
    {
        var responseHeaders = httpContext.Response.Headers;
        if (!string.IsNullOrWhiteSpace(etag))
        {
            responseHeaders[HeaderNames.ETag] = etag;
        }

        // public (not private) — see comment on BootstrapMaxAgeSeconds above.
        responseHeaders[HeaderNames.CacheControl] =
            $"public, max-age={BootstrapMaxAgeSeconds.ToString(CultureInfo.InvariantCulture)}";
    }

    private static void ApplyRetryAfterHeader(HttpContext httpContext, TimeSpan? retryAfter)
    {
        var seconds = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
            ? (int)Math.Ceiling(retryAfter.Value.TotalSeconds)
            : 1;
        httpContext.Response.Headers[HeaderNames.RetryAfter] =
            seconds.ToString(CultureInfo.InvariantCulture);
    }
}
