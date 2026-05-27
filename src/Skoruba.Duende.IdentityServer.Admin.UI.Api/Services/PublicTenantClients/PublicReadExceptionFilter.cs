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

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

/// <summary>
/// MVC <see cref="IAsyncExceptionFilter"/> applied to
/// <c>PublicTenantClientsController</c> that converts any exception
/// bubbling out of <c>ITenantClientCacheService.ReadSnapshotAsync</c>
/// (or anywhere downstream of route binding) into the contract failure
/// shape <c>503 {"error":"snapshot_unavailable"}</c> with
/// <c>Retry-After: 5</c> (R7.5, R7.8).
/// </summary>
/// <remarks>
/// <para>
/// The filter takes care of three things:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       Caller disconnect — when the framework surfaces an
///       <see cref="OperationCanceledException"/> tied to
///       <see cref="HttpContext.RequestAborted"/>, the filter marks
///       <see cref="ExceptionContext.ExceptionHandled"/> <c>true</c> and
///       returns without writing anything to the response. The
///       framework then drops the response per the parent spec
///       cancellation contract.
///     </description>
///   </item>
///   <item>
///     <description>
///       Any other unhandled <see cref="Exception"/> is mapped to the
///       canonical 503 body with the exception type and message NEVER
///       included in the body (R7.5). Structured logger MAY include
///       full exception details for operator diagnostics.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="ExceptionContext.ExceptionHandled"/> is always set
///       to <c>true</c> so that no 5xx status other than 503 escapes the
///       endpoint pipeline (R7.8).
///     </description>
///   </item>
/// </list>
/// <para>
/// Lifetime: <c>Singleton</c>. The filter holds no per-request state.
/// </para>
/// </remarks>
internal sealed class PublicReadExceptionFilter : IAsyncExceptionFilter
{
    /// <summary>Route value key used by <c>PublicTenantClientsController</c>.</summary>
    private const string TenantKeyRouteKey = "tenantKey";

    /// <summary>Retry-After hint for transient unavailability (seconds).</summary>
    /// <remarks>R7.5 — fixed value 5, distinct from the longer 60s used
    /// by the controller's pipeline-disabled response.</remarks>
    private const string RetryAfterSeconds = "5";

    /// <summary>Stable JSON body. We deliberately do NOT serialize
    /// dynamically per request to keep the payload byte-stable.</summary>
    private const string ErrorCode = "snapshot_unavailable";

    private readonly ILogger<PublicReadExceptionFilter> _logger;
    private readonly TenantClientCacheMetrics _metrics;

    public PublicReadExceptionFilter(
        ILogger<PublicReadExceptionFilter> logger,
        TenantClientCacheMetrics metrics)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <inheritdoc />
    public Task OnExceptionAsync(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Caller disconnect: framework surfaces an OperationCanceledException
        // wired to HttpContext.RequestAborted. We propagate silently —
        // ExceptionHandled = true with no body written, matching the parent
        // spec cancellation contract.
        if (context.Exception is OperationCanceledException
            && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.ExceptionHandled = true;
            return Task.CompletedTask;
        }

        var tenantKey = NormalizeTenantKey(context);

        _metrics.PublicReadServiceUnavailable(tenantKey);

        // Structured log MAY include exception details for operator
        // diagnostics. The response body intentionally NEVER mentions the
        // exception type or message (R7.5).
        _logger.LogError(
            context.Exception,
            "{EventType} tenant={TenantKey} outcome={Outcome} corr={CorrelationId}",
            "TenantClientCachePublicRead.ServiceUnavailable",
            tenantKey,
            "ServiceUnavailable",
            Activity.Current?.TraceId.ToString());

        var response = context.HttpContext.Response;
        response.Headers.RetryAfter = RetryAfterSeconds;

        context.Result = new ObjectResult(new { error = ErrorCode })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentTypes = { "application/json; charset=utf-8" },
        };

        // R7.8: no 500 ever escapes this endpoint.
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pull the path-bound <c>tenantKey</c> from <c>RouteData</c> and
    /// normalize it via <c>Trim().ToLowerInvariant()</c>. Returns an empty
    /// string when the value is missing or otherwise non-string (e.g. the
    /// exception was thrown before route binding finished).
    /// </summary>
    private static string NormalizeTenantKey(ExceptionContext context)
    {
        if (context.RouteData.Values.TryGetValue(TenantKeyRouteKey, out var raw)
            && raw is string s)
        {
            return s.Trim().ToLowerInvariant();
        }

        return string.Empty;
    }
}
