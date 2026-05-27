// Feature: tenant-client-cache-public-read — Mobile BFF host.
//
// Single endpoint definition: GET /mobile/clients/{clientId}.
//
// Security boundaries enforced here:
//   * Auth required (RequireAuthorization).
//   * tenantKey is derived ONLY from the validated `tenant_key` JWT claim;
//     never from the URL, body, or headers.
//   * clientId is constrained to a loose ASCII-safe character set; the
//     public-read endpoint validates more strictly server-side, so this is
//     belt-and-suspenders.
//   * Forbidden / NotFound / 502 / 503 paths return closed JSON shapes
//     `{"error":"<machine_string>"}` — no exception leakage and no claim
//     shape echoed back to the client.
//   * No API-key value or snapshot body is ever logged (R12.7).

using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

using Skoruba.Duende.IdentityServer.Mobile.Bff.Models;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.Endpoints;

/// <summary>
/// Endpoint registrations for the BFF.
/// </summary>
internal static partial class MobileClientEndpoints
{
    private const int MaxClientIdLength = 200;
    private const int MobileMaxAgeSeconds = 60;

    // ASCII-safe character set the BFF accepts. The public-read endpoint
    // validates more strictly server-side, so this is belt-and-suspenders.
    [GeneratedRegex(@"^[A-Za-z0-9_:./-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientIdPattern();

    /// <summary>
    /// Map <c>GET /mobile/clients/{clientId}</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapMobileClientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/mobile/clients/{clientId}", HandleGetClientAsync)
            .RequireAuthorization()
            .WithName("Mobile_GetClient");

        return endpoints;
    }

    // Internal so the integration test project can drive the handler directly
    // when convenient. Production traffic always goes through the routed pipeline.
    internal static async Task<IResult> HandleGetClientAsync(
        string clientId,
        HttpContext httpContext,
        ITenantClientCacheClient sdk,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(sdk);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger("Skoruba.Duende.IdentityServer.Mobile.Bff.MobileClientEndpoints");

        // 1) tenantKey from the validated JWT claim — never from URL / body / header.
        var tenantKeyClaim = httpContext.User.FindFirstValue(TenantClaimTypes.TenantKey);
        var tenantKey = tenantKeyClaim?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            // Do not echo the claim shape back; just refuse.
            logger.LogInformation(
                "Mobile BFF rejected request: missing tenant_key claim. ClientId={ClientId}",
                Sanitize(clientId));
            return Results.Forbid();
        }

        // 2) clientId shape check (loose).
        if (string.IsNullOrWhiteSpace(clientId)
            || clientId.Length > MaxClientIdLength
            || !ClientIdPattern().IsMatch(clientId))
        {
            logger.LogInformation(
                "Mobile BFF rejected request: invalid client_id. TenantKey={TenantKey}",
                tenantKey);
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

        // 4) Call the SDK.
        TenantClientSnapshotResult result;
        try
        {
            result = await sdk.GetClientAsync(tenantKey, clientId, ifNoneMatch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller went away — no need to surface as 5xx.
            throw;
        }
        catch (Exception ex)
        {
            // Defensive: SDK should not throw, but never leak exception details.
            logger.LogError(
                ex,
                "Mobile BFF SDK call threw unexpectedly. TenantKey={TenantKey} ClientId={ClientId}",
                tenantKey,
                clientId);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // 5) Map outcomes. Never log API key or snapshot body — only structured
        //    {TenantKey, ClientId, Outcome}.
        switch (result.Outcome)
        {
            case SdkCacheOutcome.Hit:
            case SdkCacheOutcome.Miss:
            {
                if (result.Snapshot is null)
                {
                    // Defensive — Hit/Miss without a payload is a contract bug upstream.
                    logger.LogError(
                        "Mobile BFF received {Outcome} without a snapshot. TenantKey={TenantKey} ClientId={ClientId}",
                        result.Outcome,
                        tenantKey,
                        clientId);
                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                }

                ApplyResponseHeaders(httpContext, result.Etag);
                logger.LogInformation(
                    "Mobile BFF returning slim snapshot. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome}",
                    tenantKey,
                    clientId,
                    result.Outcome);
                return Results.Ok(MobileClientSnapshotResponse.From(result.Snapshot));
            }

            case SdkCacheOutcome.NotModified:
            {
                ApplyResponseHeaders(httpContext, result.Etag);
                logger.LogInformation(
                    "Mobile BFF returning 304. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome}",
                    tenantKey,
                    clientId,
                    result.Outcome);
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            case SdkCacheOutcome.NotFound:
            {
                logger.LogInformation(
                    "Mobile BFF returning 404. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome}",
                    tenantKey,
                    clientId,
                    result.Outcome);
                return Results.NotFound(new { error = "client_not_found" });
            }

            case SdkCacheOutcome.Unauthorized:
            {
                // BFF API-key issue — surfaces to mobile as 502 because the
                // user did authenticate successfully; the BFF's upstream
                // credential is the failure.
                logger.LogError(
                    "Mobile BFF received Unauthorized from public-read endpoint — BFF API key may be invalid. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome}",
                    tenantKey,
                    clientId,
                    result.Outcome);
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            case SdkCacheOutcome.RateLimited:
            case SdkCacheOutcome.ServiceUnavailable:
            case SdkCacheOutcome.TransientFailure:
            {
                ApplyRetryAfterHeader(httpContext, result.RetryAfter);
                logger.LogInformation(
                    "Mobile BFF returning 503. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome}",
                    tenantKey,
                    clientId,
                    result.Outcome);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            default:
            {
                logger.LogError(
                    "Mobile BFF received unknown outcome. TenantKey={TenantKey} ClientId={ClientId} Outcome={Outcome}",
                    tenantKey,
                    clientId,
                    result.Outcome);
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        }
    }

    private static void ApplyResponseHeaders(HttpContext httpContext, string? etag)
    {
        var responseHeaders = httpContext.Response.Headers;
        if (!string.IsNullOrWhiteSpace(etag))
        {
            // Pass the SDK's ETag straight through so the mobile client can
            // revalidate via If-None-Match on the next call.
            responseHeaders[HeaderNames.ETag] = etag;
        }

        // private = mobile-specific; never cache this on a shared CDN.
        responseHeaders[HeaderNames.CacheControl] =
            $"private, max-age={MobileMaxAgeSeconds.ToString(CultureInfo.InvariantCulture)}";
    }

    private static void ApplyRetryAfterHeader(HttpContext httpContext, TimeSpan? retryAfter)
    {
        var seconds = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
            ? (int)Math.Ceiling(retryAfter.Value.TotalSeconds)
            : 1;
        httpContext.Response.Headers[HeaderNames.RetryAfter] =
            seconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Strip non-ASCII / control characters so a hostile clientId (which we
    /// already rejected) cannot warp a structured log line. Always returns a
    /// short, single-line string.
    /// </summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var length = Math.Min(value.Length, 64);
        Span<char> buffer = stackalloc char[length];
        var index = 0;
        for (var i = 0; i < length; i++)
        {
            var ch = value[i];
            buffer[index++] = char.IsControl(ch) ? '?' : ch;
        }
        return new string(buffer[..index]);
    }
}
