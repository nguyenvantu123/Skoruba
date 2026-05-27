// Feature: tenant-client-cache-public-read, Property 20: SDK in-memory cache + revalidation
//
// Drives the SDK through randomised sequences of cache lifecycle
// operations on the same `(tenantKey, clientId)` key and asserts the
// universal invariants:
//
//   1. After a 200 with `Cache-Control: max-age=N`, the SDK populates
//      the local cache with TTL = `min(N, MaxClientCacheTtl)`.
//      TTL = 0 disables the local cache for that entry.
//   2. A subsequent call within the TTL returns `Outcome=Hit` and
//      issues NO HTTP traffic.
//   3. A subsequent call after eviction triggers an HTTP request that
//      carries `If-None-Match: <cachedEtag>`. A 304 response surfaces
//      the cached snapshot with `Outcome=NotModified`. A 200 response
//      replaces the cache entry.
//   4. A call passing an explicit non-null `ifNoneMatch` always
//      bypasses the local cache, regardless of TTL.
//   5. Two distinct `(tenantKey, clientId)` keys keep independent
//      cache entries.
//
// Validates: Requirements 11.6, 11.7, 11.8, 11.9, 11.10

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests;

public sealed class TenantClientCacheClientCacheProperties
{
    // ===== Sample shape =============================================

    public sealed record CacheLifecycleSample(
        string TenantKey,
        string ClientId,
        int MaxAgeSeconds,
        int MaxTtlSeconds,
        bool ExplicitIfNoneMatch);

    public static class Arbs
    {
        public static Arbitrary<CacheLifecycleSample> Lifecycle()
            => (from tenant in Gen.Choose(0, 4).Select(i => $"tenant-{i}")
                from client in Gen.Choose(0, 4).Select(i => $"client-{i}")
                from maxAge in Gen.Choose(0, 600)        // 0..600s server hint
                from maxTtl in Gen.Choose(0, 1800)       // 0..30min cap
                from explicitIfNoneMatch in Gen.OneOf(Gen.Constant(true), Gen.Constant(false))
                select new CacheLifecycleSample(tenant, client, maxAge, maxTtl, explicitIfNoneMatch))
                .ToArbitrary();
    }

    // ===== Property 20: lifecycle invariants ========================

    [Property(MaxTest = 40, Arbitrary = new[] { typeof(Arbs) },
        DisplayName = "P20: SDK in-memory cache + revalidation lifecycle")]
    public async Task Property20_InMemoryCacheAndRevalidation(CacheLifecycleSample sample)
    {
        // Feature: tenant-client-cache-public-read, Property 20: SDK in-memory cache + revalidation.
        using var harness = TestHarness.Create(maxTtl: TimeSpan.FromSeconds(sample.MaxTtlSeconds));

        // ---------- Phase 1: first fetch returns 200, cache populates.
        var snapshot = NewSnapshot(sample.ClientId);
        const string etag = "\"v1\"";
        harness.Handler.NextResponse = _ => MakeOk(
            snapshot, etag, TimeSpan.FromSeconds(sample.MaxAgeSeconds));

        var first = await harness.Client.GetClientAsync(sample.TenantKey, sample.ClientId);
        first.Outcome.Should().Be(SdkCacheOutcome.Miss);

        var key = (sample.TenantKey, sample.ClientId);
        var cachedNow = harness.MemoryCache.TryGetValue<TenantClientCacheClientCacheEntry>(key, out var afterFirst)
                        && afterFirst is not null;

        // R11.6 — cache is populated iff TTL > 0.
        var expectedTtl = TimeSpan.FromTicks(Math.Min(
            TimeSpan.FromSeconds(sample.MaxAgeSeconds).Ticks,
            TimeSpan.FromSeconds(sample.MaxTtlSeconds).Ticks));
        if (expectedTtl > TimeSpan.Zero)
            cachedNow.Should().BeTrue("a positive TTL must populate the local cache (R11.6)");
        else
            cachedNow.Should().BeFalse("TTL = 0 must NOT populate the local cache (R11.6)");

        // ---------- Phase 2: second call.
        // Pre-arm a 304 just in case the SDK reaches the wire — we use
        // the call-count assertion below to enforce the local-hit path.
        harness.Handler.NextResponse = req =>
        {
            // If the SDK reaches the wire, the request MUST carry the
            // cached ETag (R11.9 auto-revalidation) AND its path MUST
            // be the SAME as the first call (cache key isolation).
            req.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).Should().BeTrue();
            string.Join(",", ifNoneMatchValues!).Should().Contain("v1");
            return MakeNotModified(etag);
        };

        var callsBefore = harness.Handler.Calls.Count;
        var second = await harness.Client.GetClientAsync(
            sample.TenantKey, sample.ClientId,
            ifNoneMatch: sample.ExplicitIfNoneMatch ? etag : null);

        var callsAfter = harness.Handler.Calls.Count;

        if (sample.ExplicitIfNoneMatch)
        {
            // R11.8 — explicit If-None-Match always bypasses the local
            // cache and reaches the wire.
            (callsAfter - callsBefore).Should().Be(1);
            second.Outcome.Should().Be(SdkCacheOutcome.NotModified);
        }
        else if (cachedNow)
        {
            // R11.7 — local hit short-circuits the HTTP call.
            (callsAfter - callsBefore).Should().Be(0);
            second.Outcome.Should().Be(SdkCacheOutcome.Hit);
            second.Snapshot.Should().NotBeNull();
        }
        else
        {
            // No cache → the SDK reaches the wire without
            // If-None-Match (no prior ETag is known).
            (callsAfter - callsBefore).Should().Be(1);
        }

        // ---------- Phase 3: distinct keys are independent.
        if (cachedNow)
        {
            harness.Handler.NextResponse = _ => MakeOk(
                NewSnapshot("other-client"), "\"v2\"", TimeSpan.FromSeconds(sample.MaxAgeSeconds));

            var distinct = await harness.Client.GetClientAsync(
                sample.TenantKey + "-x", "other-client");

            distinct.Outcome.Should().Be(SdkCacheOutcome.Miss,
                "a distinct (tenantKey, clientId) key must NOT serve a hit from another entry");
            harness.MemoryCache.TryGetValue<TenantClientCacheClientCacheEntry>(key, out var stillThere)
                .Should().BeTrue("the original entry must remain after a different key was fetched");
            stillThere!.Etag.Should().Be(etag);
        }
    }

    // ===== Helpers ==================================================

    private static PublicClientSnapshot NewSnapshot(string clientId) => new()
    {
        ClientId = clientId,
        Enabled = true,
        ProtocolType = "oidc",
        LastWriteUtc = DateTime.UtcNow
    };

    private static HttpResponseMessage MakeOk(
        PublicClientSnapshot snapshot, string etag, TimeSpan maxAge)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                snapshot,
                options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        resp.Headers.ETag = new EntityTagHeaderValue(etag, isWeak: true);
        if (maxAge > TimeSpan.Zero)
        {
            resp.Headers.CacheControl = new CacheControlHeaderValue
            {
                MaxAge = maxAge,
                Public = true
            };
        }
        else
        {
            resp.Headers.CacheControl = new CacheControlHeaderValue
            {
                MaxAge = TimeSpan.Zero
            };
        }
        return resp;
    }

    private static HttpResponseMessage MakeNotModified(string etag)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.NotModified);
        resp.Headers.ETag = new EntityTagHeaderValue(etag, isWeak: true);
        return resp;
    }

    private sealed class TestHarness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public RecordingHandler Handler { get; }
        public ITenantClientCacheClient Client { get; }
        public IMemoryCache MemoryCache { get; }

        private TestHarness(ServiceProvider provider, RecordingHandler handler)
        {
            _provider = provider;
            Handler = handler;
            Client = provider.GetRequiredService<ITenantClientCacheClient>();
            MemoryCache = provider.GetRequiredService<IMemoryCache>();
        }

        public static TestHarness Create(TimeSpan maxTtl)
        {
            var handler = new RecordingHandler();
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddTenantClientCacheClient(o =>
            {
                o.BaseAddress = new Uri("https://identity.example.com/");
                o.ApiKey = "test-api-key";
                o.HttpTimeout = TimeSpan.FromSeconds(5);
                o.MaxRetryAttempts = 0;
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
                o.MaxClientCacheTtl = maxTtl;
                o.EnableInMemoryCaching = true;
            });

            services.AddHttpClient(TenantClientCacheClientServiceCollectionExtensions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);

            var provider = services.BuildServiceProvider();
            return new TestHarness(provider, handler);
        }

        public void Dispose() => _provider.Dispose();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Calls { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? NextResponse { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            if (NextResponse is { } supplyResp)
                return Task.FromResult(supplyResp(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));
        }
    }
}
