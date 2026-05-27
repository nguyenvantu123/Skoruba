// Feature: tenant-client-cache-public-read, Task 3
//
// Example-based tests for TenantApiKeyAuthorizationFilter covering:
//   R3.1 — Missing / whitespace header → 401 missing_api_key.
//   R3.2 — Invalid hash → 401 invalid_api_key.
//   R3.3 — Unregistered tenant identical 401 invalid_api_key (anti-enum).
//   R3.4 — Audit log redaction: no raw header, hash, raw tenantKey logged.
//   R3.7 — Header-only credential — query / body ignored.
//   ITenantClientCacheService is NEVER consulted on the failure path
//   (filter short-circuits at the authorization layer).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

[Collection(PublicReadMetricCollection.Name)]
public class TenantApiKeyAuthorizationFilterTests
{
    private const string Tenant = "acme";
    private const string ApiKey = "super-secret-key-1";

    private static string Sha256HexLower(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record FilterUnderTest(
        TenantApiKeyAuthorizationFilter Filter,
        CapturingLogger<TenantApiKeyAuthorizationFilter> Logger,
        TenantClientCacheMetrics Metrics,
        StubOptionsMonitor<TenantClientCachePublicReadOptions> Monitor);

    private static FilterUnderTest Build(
        Dictionary<string, string>? apiKeys = null,
        bool logIpHash = true,
        string salt = "salt")
    {
        var options = new TenantClientCachePublicReadOptions();
        if (apiKeys is not null)
        {
            options.ApiKeys = apiKeys;
        }

        options.Audit.LogIpHash = logIpHash;
        options.Audit.RemoteIpSalt = salt;
        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(options);
        var validator = new TenantApiKeyValidator(monitor);
        var ipHash = new IpHashHelper(monitor);
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantApiKeyAuthorizationFilter>();
        var filter = new TenantApiKeyAuthorizationFilter(validator, logger, monitor, metrics, ipHash);
        return new FilterUnderTest(filter, logger, metrics, monitor);
    }

    private static AuthorizationFilterContext BuildContext(
        string? headerValue,
        string tenantRouteValue = Tenant,
        string? queryString = null,
        System.Net.IPAddress? remoteIp = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("api.example.com");
        http.Request.Path = $"/api/public/tenants/{tenantRouteValue}/clients/web";
        if (queryString is not null)
        {
            http.Request.QueryString = new QueryString(queryString);
        }

        if (headerValue is not null)
        {
            http.Request.Headers[TenantApiKeyAuthorizationFilter.HeaderName] = headerValue;
        }

        if (remoteIp is not null)
        {
            http.Connection.RemoteIpAddress = remoteIp;
        }

        var routeData = new RouteData();
        routeData.Values["tenantKey"] = tenantRouteValue;
        var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new IFilterMetadata[0]);
    }

    [Fact]
    public async Task MissingHeader_Returns_401_MissingApiKey()
    {
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var ctx = BuildContext(headerValue: null);

        await sut.Filter.OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<ObjectResult>();
        var result = (ObjectResult)ctx.Result!;
        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        result.Value.Should().BeEquivalentTo(new { error = "missing_api_key" });
        result.ContentTypes.Should().Contain("application/json; charset=utf-8");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("  \t  ")]
    public async Task WhitespaceHeader_Returns_401_MissingApiKey(string headerValue)
    {
        // R3.1 — whitespace-only is treated as "missing" (filter does not
        // call the validator, so the response is missing_api_key not
        // invalid_api_key, anti-enumeration R3.3 is preserved because both
        // missing and invalid responses share status code).
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var ctx = BuildContext(headerValue: headerValue);

        await sut.Filter.OnAuthorizationAsync(ctx);

        var result = ctx.Result.Should().BeOfType<ObjectResult>().Which;
        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        result.Value.Should().BeEquivalentTo(new { error = "missing_api_key" });
    }

    [Fact]
    public async Task InvalidKey_Returns_401_InvalidApiKey()
    {
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var ctx = BuildContext(headerValue: "the-wrong-key");

        await sut.Filter.OnAuthorizationAsync(ctx);

        var result = ctx.Result.Should().BeOfType<ObjectResult>().Which;
        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        result.Value.Should().BeEquivalentTo(new { error = "invalid_api_key" });
    }

    [Fact]
    public async Task UnregisteredTenant_Returns_401_InvalidApiKey_SameAs_WrongKey()
    {
        // R3.3 — tenant not registered must be indistinguishable from
        // wrong-key on the registered tenant (status, body, content-type).
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var ctxUnknown = BuildContext(headerValue: ApiKey, tenantRouteValue: "unknown-tenant");
        var ctxWrong = BuildContext(headerValue: "wrong", tenantRouteValue: Tenant);

        await sut.Filter.OnAuthorizationAsync(ctxUnknown);
        await sut.Filter.OnAuthorizationAsync(ctxWrong);

        var unknown = ctxUnknown.Result.Should().BeOfType<ObjectResult>().Which;
        var wrong = ctxWrong.Result.Should().BeOfType<ObjectResult>().Which;
        unknown.StatusCode.Should().Be(wrong.StatusCode);
        unknown.Value.Should().BeEquivalentTo(wrong.Value);
        unknown.ContentTypes.Should().BeEquivalentTo(wrong.ContentTypes);
    }

    [Fact]
    public async Task ValidKey_FallsThrough_ResultIsNull()
    {
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var ctx = BuildContext(headerValue: ApiKey);

        await sut.Filter.OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull("a successful auth must not short-circuit the pipeline");
    }

    [Fact]
    public async Task TenantKey_Normalized_Before_Validator_Lookup()
    {
        // R2.3 — filter applies Trim().ToLowerInvariant() to the route value
        // before consulting the validator.
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var ctx = BuildContext(headerValue: ApiKey, tenantRouteValue: "  ACME  ");

        await sut.Filter.OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task Audit_Log_Does_Not_Contain_Raw_Header_Or_Hash_Or_TenantKey()
    {
        // R3.4 / R8.7 — log entries must not contain the raw header, the
        // SHA-256 hash, or the raw tenantKey.
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var rawHeader = "verbatim-secret-3xt7-9";
        var ctx = BuildContext(headerValue: rawHeader);

        await sut.Filter.OnAuthorizationAsync(ctx);

        sut.Logger.Entries.Should().NotBeEmpty();
        foreach (var entry in sut.Logger.Entries)
        {
            entry.Message.Should().NotContain(rawHeader);
            entry.Message.Should().NotContain(Sha256HexLower(rawHeader));
            entry.Message.Should().NotContain(Tenant);

            foreach (var (_, value) in entry.Fields)
            {
                var stringified = value?.ToString() ?? string.Empty;
                stringified.Should().NotContain(rawHeader);
                stringified.Should().NotContain(Sha256HexLower(rawHeader));
                stringified.Should().NotContain(Tenant);
            }
        }
    }

    [Fact]
    public async Task Audit_Log_Includes_Outcome_And_EventType_Fields()
    {
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var ctx = BuildContext(headerValue: "wrong");

        await sut.Filter.OnAuthorizationAsync(ctx);

        var entry = sut.Logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Fields.Should().ContainKey("EventType");
        entry.Fields["EventType"].Should().Be("TenantClientCachePublicRead.Unauthorized");
        entry.Fields.Should().ContainKey("Outcome");
        entry.Fields["Outcome"].Should().Be("Unauthorized");
    }

    [Fact]
    public async Task Reads_Only_Header_Not_Query_String()
    {
        // R3.7 — even when a valid key is supplied via query string, the
        // filter must reject the request because no header was provided.
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var ctx = BuildContext(headerValue: null, queryString: $"?apiKey={ApiKey}");

        await sut.Filter.OnAuthorizationAsync(ctx);

        var result = ctx.Result.Should().BeOfType<ObjectResult>().Which;
        result.Value.Should().BeEquivalentTo(new { error = "missing_api_key" });
    }

    [Fact]
    public async Task EmptyStore_All_Tenants_Return_401_InvalidApiKey_When_HeaderProvided()
    {
        // R1.7 — empty Api_Key_Store with a header present means the
        // tenant-key lookup misses; response must be invalid_api_key
        // (anti-enumeration with R3.3).
        var sut = Build(apiKeys: new Dictionary<string, string>());
        var ctx = BuildContext(headerValue: "some-key");

        await sut.Filter.OnAuthorizationAsync(ctx);

        var result = ctx.Result.Should().BeOfType<ObjectResult>().Which;
        result.Value.Should().BeEquivalentTo(new { error = "invalid_api_key" });
    }

    [Fact]
    public async Task Unauthorized_Counter_Incremented_With_No_TenantKey_Tag()
    {
        // R8.4 — Unauthorized counter MUST NOT carry a tenantKey tag.
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        using var listener = new RecordingMeterListener(TenantClientCacheMetrics.MeterName);
        var ctx = BuildContext(headerValue: "wrong");

        await sut.Filter.OnAuthorizationAsync(ctx);

        var measurements = listener.ForInstrument(TenantClientCacheMetrics.PublicReadUnauthorizedCounterName);
        measurements.Should().ContainSingle();
        measurements.Single().Tags.Should().NotContainKey("tenantKey");
        measurements.Single().Value.Should().Be(1);
    }

    [Fact]
    public void Constructor_Throws_On_Null_Dependencies()
    {
        var sut = Build(new Dictionary<string, string> { [Tenant] = Sha256HexLower(ApiKey) });
        var validator = new TenantApiKeyValidator(sut.Monitor);
        var ipHash = new IpHashHelper(sut.Monitor);

        Action a1 = () => new TenantApiKeyAuthorizationFilter(null!, sut.Logger, sut.Monitor, sut.Metrics, ipHash);
        Action a2 = () => new TenantApiKeyAuthorizationFilter(validator, null!, sut.Monitor, sut.Metrics, ipHash);
        Action a3 = () => new TenantApiKeyAuthorizationFilter(validator, sut.Logger, null!, sut.Metrics, ipHash);
        Action a4 = () => new TenantApiKeyAuthorizationFilter(validator, sut.Logger, sut.Monitor, null!, ipHash);
        Action a5 = () => new TenantApiKeyAuthorizationFilter(validator, sut.Logger, sut.Monitor, sut.Metrics, null!);

        a1.Should().Throw<ArgumentNullException>();
        a2.Should().Throw<ArgumentNullException>();
        a3.Should().Throw<ArgumentNullException>();
        a4.Should().Throw<ArgumentNullException>();
        a5.Should().Throw<ArgumentNullException>();
    }
}
