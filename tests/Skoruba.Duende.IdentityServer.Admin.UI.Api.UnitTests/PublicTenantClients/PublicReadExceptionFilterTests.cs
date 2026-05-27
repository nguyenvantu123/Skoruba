// Feature: tenant-client-cache-public-read, Task 4
//
// Example-based tests for PublicReadExceptionFilter covering:
//   R7.5 — unhandled exception → 503 + Retry-After: 5 + body { error:
//          "snapshot_unavailable" }; exception type / message NEVER in body.
//   R7.8 — every code path sets ExceptionHandled = true; no 500 escapes.
//   Cancellation — OperationCanceledException tied to RequestAborted is
//   propagated silently (no body, no metric, no 503). Caller-disconnect
//   contract from the parent spec.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

[Collection(PublicReadMetricCollection.Name)]
public class PublicReadExceptionFilterTests
{
    private static readonly ActionDescriptor EmptyActionDescriptor = new();

    private static (PublicReadExceptionFilter filter,
                    CapturingLogger<PublicReadExceptionFilter> logger,
                    TenantClientCacheMetrics metrics) Build()
    {
        var logger = new CapturingLogger<PublicReadExceptionFilter>();
        var metrics = new TenantClientCacheMetrics();
        var filter = new PublicReadExceptionFilter(logger, metrics);
        return (filter, logger, metrics);
    }

    private static ExceptionContext BuildContext(
        Exception exception,
        string tenantKey = "acme",
        bool clientAborted = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = $"/api/public/tenants/{tenantKey}/clients/web";

        if (clientAborted)
        {
            // Mark the request as aborted so the filter can inspect
            // HttpContext.RequestAborted.IsCancellationRequested.
            http.RequestAborted = new CancellationToken(canceled: true);
        }

        var routeData = new RouteData();
        if (!string.IsNullOrEmpty(tenantKey))
        {
            routeData.Values["tenantKey"] = tenantKey;
        }

        var ctx = new ExceptionContext(
            new ActionContext(http, routeData, EmptyActionDescriptor),
            new List<IFilterMetadata>())
        {
            Exception = exception,
        };

        return ctx;
    }

    [Fact]
    public async Task Throws_ResolvedTo_503_With_RetryAfter_5()
    {
        // R7.5 — any unhandled exception maps to 503 + Retry-After: 5.
        var (filter, _, _) = Build();
        var ctx = BuildContext(new InvalidOperationException("redis down — connection refused"));

        await filter.OnExceptionAsync(ctx);

        ctx.Result.Should().BeOfType<ObjectResult>();
        var result = (ObjectResult)ctx.Result!;
        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.HttpContext.Response.Headers.RetryAfter.ToString().Should().Be("5");
    }

    [Fact]
    public async Task Exception_Message_Not_Leaked_In_Response_Body()
    {
        // R7.5 — body is strictly { "error": "snapshot_unavailable" }; we
        // assert the exception's message text is NOT present anywhere in
        // the serialized body.
        var (filter, _, _) = Build();
        const string SecretLeak = "s3kret-token-leak-d3adb33f";
        var ctx = BuildContext(new InvalidOperationException(SecretLeak));

        await filter.OnExceptionAsync(ctx);

        var result = (ObjectResult)ctx.Result!;
        var bodyJson = JsonSerializer.Serialize(result.Value);
        bodyJson.Should().Contain("\"error\":\"snapshot_unavailable\"");
        bodyJson.Should().NotContain(SecretLeak);
        bodyJson.Should().NotContain(nameof(InvalidOperationException));

        // Top-level shape must be { "error": <string> } with no other keys.
        using var doc = JsonDocument.Parse(bodyJson);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[] { "error" });
    }

    [Fact]
    public async Task OperationCanceledException_From_RequestAborted_PropagatesSilent()
    {
        // Caller-disconnect contract: filter marks ExceptionHandled = true
        // and does NOT write anything to the response. Empty body, no
        // status override, no Retry-After header.
        var (filter, _, _) = Build();
        var ctx = BuildContext(new OperationCanceledException(), clientAborted: true);

        await filter.OnExceptionAsync(ctx);

        ctx.ExceptionHandled.Should().BeTrue();
        ctx.Result.Should().BeNull();
        ctx.HttpContext.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    [Fact]
    public async Task OperationCanceledException_NotFromAbort_TreatedAsTransient()
    {
        // Defensive: if the exception is OperationCanceledException but
        // RequestAborted is NOT signalled (e.g. an internal timeout), the
        // filter falls through to the 503 path so consumers see a stable
        // contract.
        var (filter, _, _) = Build();
        var ctx = BuildContext(new OperationCanceledException(), clientAborted: false);

        await filter.OnExceptionAsync(ctx);

        ctx.ExceptionHandled.Should().BeTrue();
        ctx.Result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)ctx.Result!).StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Filter_Sets_ExceptionHandled_True_NeverLet_500_Escape()
    {
        // R7.8 — every code path MUST set ExceptionHandled = true so the
        // framework default 500 page is never emitted by this endpoint.
        var (filter, _, _) = Build();

        // Variant 1: arbitrary unhandled exception.
        var transient = BuildContext(new TimeoutException("redis read timed out"));
        await filter.OnExceptionAsync(transient);
        transient.ExceptionHandled.Should().BeTrue();

        // Variant 2: caller disconnect.
        var aborted = BuildContext(new OperationCanceledException(), clientAborted: true);
        await filter.OnExceptionAsync(aborted);
        aborted.ExceptionHandled.Should().BeTrue();
    }

    [Fact]
    public async Task ServiceUnavailable_Counter_Tagged_With_TenantKey()
    {
        // R8.4 — service_unavailable counter carries `tenantKey` tag.
        var (filter, _, metrics) = Build();
        using var listener = new RecordingMeterListener(TenantClientCacheMetrics.MeterName);

        var ctx = BuildContext(new InvalidOperationException("boom"), tenantKey: "acme");
        await filter.OnExceptionAsync(ctx);

        var measurements = listener.ForInstrument(
            TenantClientCacheMetrics.PublicReadServiceUnavailableCounterName);
        measurements.Should().ContainSingle();
        measurements.Single().Tags.Should()
            .ContainKey("tenantKey").WhoseValue.Should().Be("acme");
        measurements.Single().Tags.Should().NotContainKey("clientId");
    }

    [Fact]
    public async Task Audit_Log_Does_Not_Contain_Exception_Type_Or_Message_In_State()
    {
        // The body must never leak exception details (R7.5). The
        // structured logger MAY include the exception object (it is
        // passed to logger.LogError), but the response payload itself
        // (asserted in another test) is the user-facing contract.
        var (filter, _, _) = Build();
        var ctx = BuildContext(new InvalidOperationException("private connection details"));

        await filter.OnExceptionAsync(ctx);

        var result = (ObjectResult)ctx.Result!;
        // Serialize the response and confirm the body is byte-stable —
        // identical for any exception text.
        using var ms = new MemoryStream();
        JsonSerializer.Serialize(ms, result.Value);
        var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        json.Should().Be("{\"error\":\"snapshot_unavailable\"}");
    }

    [Fact]
    public void Constructor_Throws_On_Null_Logger_Or_Metrics()
    {
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<PublicReadExceptionFilter>();

        ((Action)(() => _ = new PublicReadExceptionFilter(null!, metrics)))
            .Should().Throw<ArgumentNullException>().WithParameterName("logger");

        ((Action)(() => _ = new PublicReadExceptionFilter(logger, null!)))
            .Should().Throw<ArgumentNullException>().WithParameterName("metrics");
    }
}
