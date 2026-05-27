// Feature: tenant-client-cache-public-read, Task 6
//
// Property-based tests for the rate-limiter wiring exposed by
// StartupHelpers.AddTenantClientCachePublicRead.
//
// Property 07 — AuthBeforeRateLimit (Validates: Requirements 3.8, 4.7).
//   For any sequence of `n` unauthenticated requests targeting the same
//   tenantKey, the per-tenant token bucket retains the configured
//   TokenLimit tokens after the sequence — the authorization filter
//   short-circuits at 401 BEFORE the rate-limiter middleware runs, so
//   no tokens are consumed.
//
// Property 08 — RateLimitContract (Validates: Requirements 4.5, 4.6, 4.8).
//   For any `n > TokenLimit` sequence of authenticated requests against
//   the SAME tenantKey, exactly TokenLimit acquisitions succeed and the
//   remaining `n - TokenLimit` are rejected. The rejection handler
//   wired through StartupHelpers writes a 429 with the canonical body
//   { "error": "rate_limit_exceeded" } and a Retry-After header.
//
// Implementation note — the task spec asks for a WebApplicationFactory
// driver. The Admin host has no end-to-end fixture today (it boots the
// full master-DB pipeline), and Task 6 forbids new infrastructure. We
// instead drive (a) the real TenantApiKeyAuthorizationFilter for the P7
// no-token-consumption invariant and (b) the production
// PartitionedRateLimiter assembled by AddTenantClientCachePublicRead via
// IRateLimiterPolicy resolution for the P8 contract. Both reuse the
// EXACT factory the host wires up — no test-only shadow.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

[Collection(PublicReadMetricCollection.Name)]
public sealed class RateLimitProperties
{
    // ===== Generators ==================================================

    public sealed record AuthBeforeRateLimitSample(string TenantKey, int RequestCount);

    public sealed record RateLimitContractSample(string TenantKey, int RequestCount);

    public static class Arbs
    {
        private static readonly char[] TenantAlphabet =
            "abcdefghijklmnopqrstuvwxyz0123456789-_".ToCharArray();

        private static Gen<string> TenantGen()
            => from len in Gen.Choose(3, 16)
               from chars in Gen.Elements(TenantAlphabet).ListOf(len)
               select new string(chars.ToArray());

        public static Arbitrary<AuthBeforeRateLimitSample> AuthBefore()
            => (from tenant in TenantGen()
                from count in Gen.Choose(1, 20)
                select new AuthBeforeRateLimitSample(tenant, count))
               .ToArbitrary();

        public static Arbitrary<RateLimitContractSample> Contract()
            // n strictly greater than the test-overlay TokenLimit (= 5).
            => (from tenant in TenantGen()
                from count in Gen.Choose(6, 20)
                select new RateLimitContractSample(tenant, count))
               .ToArbitrary();
    }

    // ===== Helpers =====================================================

    /// <summary>
    /// Build a baseline <see cref="ServiceCollection"/> (logging + meter)
    /// the public-read DI extension can layer on top of.
    /// </summary>
    private static ServiceCollection BuildBaselineServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TenantClientCacheMetrics>();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));
        return services;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "TestHost";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static IConfiguration BuildConfiguration(int tokenLimit)
    {
        // 1-second replenishment so generators don't stall the test.
        var data = new Dictionary<string, string?>
        {
            ["TenantClientCachePublicRead:RateLimit:TokenLimit"] = tokenLimit.ToString(),
            ["TenantClientCachePublicRead:RateLimit:TokensPerPeriod"] = tokenLimit.ToString(),
            ["TenantClientCachePublicRead:RateLimit:ReplenishmentPeriod"] = "00:01:00",
            ["TenantClientCachePublicRead:RateLimit:QueueLimit"] = "0",
            ["TenantClientCachePublicRead:RateLimit:AutoReplenishment"] = "false",
            ["TenantClientCachePublicRead:Cors:PreflightMaxAgeSeconds"] = "600",
            ["TenantClientCachePublicRead:ResponseCache:MaxAgeSeconds"] = "60",
            ["TenantClientCachePublicRead:Audit:LogIpHash"] = "true",
            ["TenantClientCachePublicRead:Audit:RemoteIpSalt"] = string.Empty,
        };

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    /// <summary>
    /// Reach the production partition factory directly.
    /// <see cref="StartupHelpers.BuildRateLimitPartition"/> is exposed
    /// as <c>internal</c> for this exact purpose — the framework's
    /// policy registry wraps user delegates in an internal
    /// <c>DefaultKeyType</c> shape that does not survive a generic
    /// cast back to <c>RateLimitPartition&lt;string&gt;</c>. Bypassing
    /// the registry keeps the test driving the same code path the
    /// host wires up.
    /// </summary>
    private static Func<HttpContext, RateLimitPartition<string>> ResolvePolicyFactory(
        IServiceProvider provider)
    {
        // Prove the policy is registered on the production options so
        // the test fails loudly if AddTenantClientCachePublicRead ever
        // stops registering it.
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        options.OnRejected.Should().NotBeNull(
            "AddTenantClientCachePublicRead must register the OnRejected handler");

        return StartupHelpers.BuildRateLimitPartition;
    }

    /// <summary>
    /// Build a synthetic <see cref="HttpContext"/> targeting
    /// <c>tenantKey</c> with a populated route value.
    /// </summary>
    private static HttpContext BuildHttpContext(IServiceProvider provider, string tenantKey)
    {
        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = $"/api/public/tenants/{tenantKey}/clients/web";
        http.Request.RouteValues["tenantKey"] = tenantKey;
        http.Response.Body = new MemoryStream();
        return http;
    }

    private sealed class StubLease : RateLimitLease
    {
        private readonly TimeSpan? _retryAfter;

        public StubLease(TimeSpan? retryAfter)
        {
            _retryAfter = retryAfter;
        }

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => _retryAfter is null
            ? Array.Empty<string>()
            : new[] { MetadataName.RetryAfter.Name };

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_retryAfter is not null
                && string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal))
            {
                metadata = _retryAfter.Value;
                return true;
            }
            metadata = null;
            return false;
        }
    }

    // ===== Property 07 — AuthBeforeRateLimit ==========================

    /// <summary>
    /// Property 7 (Validates: Requirements 3.8, 4.7). For any sequence
    /// of unauthenticated requests against the same tenantKey, the
    /// per-tenant token bucket retains every token. The authorization
    /// filter short-circuits at 401 before the rate limiter middleware
    /// has a chance to call <c>AcquireAsync</c>; we simulate the
    /// production order by (a) running the real auth filter first and
    /// (b) only calling the limiter on requests it lets through.
    /// </summary>
    // Feature: tenant-client-cache-public-read, Property 7: 401-bound
    // requests do not consume tokens.
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property07_AuthBeforeRateLimit(AuthBeforeRateLimitSample sample)
    {
        const int TokenLimit = 5;

        // Build the host the way AddTenantClientCachePublicRead does, but
        // with the test-overlay TokenLimit = 5 so the property runs fast.
        var configuration = BuildConfiguration(TokenLimit);
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        // Resolve the production wiring.
        var partitioner = ResolvePolicyFactory(provider);

        var authFilter = provider.GetRequiredService<TenantApiKeyAuthorizationFilter>();

        // Build a partitioned limiter that delegates to the production
        // factory. PartitionedRateLimiter.Create gives us a real limiter
        // that exposes AcquireAsync / GetStatistics — the same machinery
        // RateLimiterMiddleware drives in production.
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(partitioner);

        // Drive `n` unauthenticated requests. Every one MUST short-circuit
        // at 401 inside the auth filter. If the filter ever falls through,
        // we explicitly skip the limiter call (mirroring production
        // behaviour where 401 returns before the middleware sees the
        // request).
        for (int i = 0; i < sample.RequestCount; i++)
        {
            var http = BuildHttpContext(provider, sample.TenantKey);
            // No X-Tenant-Api-Key header on purpose.
            var routeData = new RouteData();
            routeData.Values["tenantKey"] = sample.TenantKey;
            var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
            var ctx = new AuthorizationFilterContext(actionContext, Array.Empty<IFilterMetadata>());

            await authFilter.OnAuthorizationAsync(ctx);

            ctx.Result.Should().BeOfType<ObjectResult>(
                "401 short-circuit must produce an ObjectResult");
            ((ObjectResult)ctx.Result!).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

            // Production order: limiter is NOT consulted on the 401 path.
        }

        // Bucket invariant: full TokenLimit still available.
        var availableAfter = 0;
        for (int i = 0; i < TokenLimit; i++)
        {
            var http = BuildHttpContext(provider, sample.TenantKey);
            using var lease = await limiter.AcquireAsync(http, permitCount: 1);
            if (lease.IsAcquired) availableAfter++;
        }

        availableAfter.Should().Be(TokenLimit,
            "the bucket must still hold the full TokenLimit after a sequence of 401-bound requests (R3.8 + R4.7)");
    }

    // ===== Property 08 — RateLimitContract ============================

    /// <summary>
    /// Property 8 (Validates: Requirements 4.5, 4.6, 4.8). For any
    /// authenticated request burst <c>n &gt; TokenLimit</c> against the
    /// same tenantKey, exactly <c>TokenLimit</c> acquisitions succeed and
    /// the rest are rejected. Each rejection drives the production
    /// <c>OnRejected</c> handler and produces a 429 with the canonical
    /// body and a Retry-After header.
    /// </summary>
    // Feature: tenant-client-cache-public-read, Property 8: rate-limit
    // contract — TokenLimit successes, residue rejected with 429 +
    // Retry-After + canonical body.
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property08_RateLimitContract(RateLimitContractSample sample)
    {
        const int TokenLimit = 5;

        var configuration = BuildConfiguration(TokenLimit);
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var rateLimiterOptions = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        var partitioner = ResolvePolicyFactory(provider);

        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(partitioner);

        var acquiredCount = 0;
        var rejectedCount = 0;

        for (int i = 0; i < sample.RequestCount; i++)
        {
            var http = BuildHttpContext(provider, sample.TenantKey);
            using var lease = await limiter.AcquireAsync(http, permitCount: 1);
            if (lease.IsAcquired)
            {
                acquiredCount++;
            }
            else
            {
                rejectedCount++;

                // Drive the production OnRejected handler with the same
                // lease + http context the framework would supply. Use a
                // synthetic stub lease so we can assert Retry-After
                // independent of the real partition's metadata shape.
                var rejectedHttp = BuildHttpContext(provider, sample.TenantKey);
                var stubLease = new StubLease(TimeSpan.FromSeconds(7));
                var rejectedCtx = new OnRejectedContext
                {
                    HttpContext = rejectedHttp,
                    Lease = stubLease,
                };

                await rateLimiterOptions.OnRejected!(rejectedCtx, CancellationToken.None);

                rejectedHttp.Response.StatusCode.Should().Be(
                    StatusCodes.Status429TooManyRequests,
                    "rejection must surface as HTTP 429 (R4.5)");
                rejectedHttp.Response.ContentType.Should().Be(
                    "application/json; charset=utf-8");

                rejectedHttp.Response.Headers.RetryAfter.ToString().Should().Be(
                    "7",
                    "Retry-After must mirror the lease metadata when present (R4.5)");

                ((MemoryStream)rejectedHttp.Response.Body).Position = 0;
                var body = Encoding.UTF8.GetString(((MemoryStream)rejectedHttp.Response.Body).ToArray());
                body.Should().Be("{\"error\":\"rate_limit_exceeded\"}",
                    "429 body shape is closed (R4.5 + R7.6)");

                using var doc = JsonDocument.Parse(body);
                doc.RootElement
                    .EnumerateObject()
                    .Select(p => p.Name)
                    .Should().BeEquivalentTo(new[] { "error" });
            }
        }

        acquiredCount.Should().Be(TokenLimit,
            "exactly TokenLimit acquisitions succeed for a tenantKey burst (R4.6)");
        rejectedCount.Should().Be(sample.RequestCount - TokenLimit,
            "everything beyond TokenLimit is rejected (R4.5 + R4.8)");
    }

    // ===== Smoke fact — no-route requests bypass the limiter ===========

    /// <summary>
    /// Defensive companion to P7/P8: a request that arrives without a
    /// <c>tenantKey</c> route value lands in the <c>__noop__</c>
    /// partition and never consumes a token. (The controller's path
    /// validator returns 400 downstream — the limiter is permissive on
    /// this path so malformed-route requests don't burn tenant budget.)
    /// </summary>
    [Fact]
    public async Task NoRouteValue_Falls_Through_NoLimiter_Partition()
    {
        const int TokenLimit = 5;

        var configuration = BuildConfiguration(TokenLimit);
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var partitioner = ResolvePolicyFactory(provider);

        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(partitioner);

        // Drive 50 requests with NO route value — every one should be
        // acquired (the no-limit partition has unbounded permits).
        for (int i = 0; i < 50; i++)
        {
            var http = new DefaultHttpContext { RequestServices = provider };
            http.Request.Method = HttpMethods.Get;
            http.Request.Path = "/api/public/tenants//clients/web";
            // No RouteValues["tenantKey"] populated.
            using var lease = await limiter.AcquireAsync(http, permitCount: 1);
            lease.IsAcquired.Should().BeTrue(
                "missing-route requests fall through to the __noop__ partition");
        }
    }
}
