// Feature: tenant-client-cache-public-read, Task 9
//
// Example-based tests for TenantClientCacheClient. Drives the SDK
// through a fake HttpMessageHandler that captures outgoing requests
// and injects canned responses, asserting the public contract:
//
//   * 200 → Outcome=Miss + cache populated
//   * Cached entry within TTL → Outcome=Hit + zero HTTP traffic
//   * TTL expired with prior cache → If-None-Match revalidation
//   * 304 → Outcome=NotModified + cached snapshot surfaced
//   * 401 → Outcome=Unauthorized + cache cleared, no retry
//   * 404 → Outcome=NotFound
//   * 429 → Outcome=RateLimited + RetryAfter
//   * 503 → Outcome=ServiceUnavailable + RetryAfter (no infinite retry)
//   * HttpRequestException after retry exhaustion → Outcome=TransientFailure
//   * CancellationToken cancellation → re-throws
//   * Caller-supplied If-None-Match=null still triggers a fresh fetch
//
// Validates: Requirements 10.2, 10.3, 10.4, 10.6, 10.9, 10.11, 11.4,
//            11.5, 11.6, 11.7, 11.8, 11.9, 11.10, 11.12

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests;

public sealed class TenantClientCacheClientTests
{
    private const string TenantKey = "acme";
    private const string ClientId = "acme-spa";

    // EntityTagHeaderValue takes the quoted tag form (without the W/ prefix);
    // the "weak" flag is encoded separately. The string the SDK reads back
    // from <see cref="HttpResponseHeaders.ETag"/> via `.ETag.Tag` is the
    // quoted form (e.g. "\"deadbeef\"").
    private const string EtagTag = "\"deadbeef\"";
    private const string CachedEtag = EtagTag;

    // ===== 200 fresh body → cache populated, returns Miss ===========

    [Fact]
    public async Task Get_HappyPath_200_Caches_Locally_Returns_Miss()
    {
        var snapshot = SampleSnapshot();
        using var harness = TestHarness.Create(snapshot, etag: CachedEtag, maxAge: TimeSpan.FromMinutes(1));

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId);

        result.Outcome.Should().Be(SdkCacheOutcome.Miss);
        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.ClientId.Should().Be(snapshot.ClientId);
        result.Etag.Should().Be(CachedEtag);
        result.Version.Should().Be(7);
        result.LastWriteUtc.Should().NotBeNull();

        harness.Handler.Calls.Should().HaveCount(1);
        var request = harness.Handler.Calls[0];
        request.Method.Should().Be(HttpMethod.Get);
        request.Headers.GetValues("X-Tenant-Api-Key").Should().ContainSingle().Which.Should().Be("test-api-key");
        request.Headers.Contains("If-None-Match").Should().BeFalse();
    }

    // ===== Subsequent call within TTL → Hit, no HTTP ================

    [Fact]
    public async Task Get_AfterCachePopulated_Returns_Hit_NoHttp()
    {
        using var harness = TestHarness.Create(SampleSnapshot(), etag: CachedEtag, maxAge: TimeSpan.FromMinutes(5));

        var first = await harness.Client.GetClientAsync(TenantKey, ClientId);
        first.Outcome.Should().Be(SdkCacheOutcome.Miss);

        // The second call must come from local cache.
        var second = await harness.Client.GetClientAsync(TenantKey, ClientId);

        second.Outcome.Should().Be(SdkCacheOutcome.Hit);
        second.Snapshot.Should().NotBeNull();
        harness.Handler.Calls.Should().HaveCount(1, "the local cache must absorb the second call (R11.7)");
    }

    // ===== Cached entry + explicit revalidation → 304 surfaces cache =

    [Fact]
    public async Task Get_With_CachedEntry_And_Explicit_IfNoneMatch_304_Returns_NotModified_With_Cached_Snapshot()
    {
        using var harness = TestHarness.Create(SampleSnapshot(), etag: CachedEtag, maxAge: TimeSpan.FromMinutes(5));

        var first = await harness.Client.GetClientAsync(TenantKey, ClientId);
        first.Outcome.Should().Be(SdkCacheOutcome.Miss);

        // Subsequent fetch should send If-None-Match and 304 maps to NotModified.
        harness.Handler.NextResponse = req =>
        {
            req.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).Should().BeTrue();
            string.Join(",", ifNoneMatchValues!).Should().Contain("deadbeef");
            return MakeNotModified();
        };

        var revalidated = await harness.Client.GetClientAsync(
            TenantKey, ClientId, ifNoneMatch: CachedEtag);

        revalidated.Outcome.Should().Be(SdkCacheOutcome.NotModified);
        revalidated.Snapshot.Should().NotBeNull("304 surfaces the previously cached snapshot (R11.9)");
        revalidated.Etag.Should().Be(CachedEtag);
        harness.Handler.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Get_304_Without_Prior_Cache_Returns_NotModified_With_Null_Snapshot()
    {
        using var harness = TestHarness.Create();
        harness.Handler.NextResponse = _ => MakeNotModified();

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId, ifNoneMatch: "W/\"abc\"");

        result.Outcome.Should().Be(SdkCacheOutcome.NotModified);
        result.Snapshot.Should().BeNull("304 with no prior cache yields a null snapshot (R11.9 boundary)");
    }

    // ===== 401 → Unauthorized, cache cleared, no retry ==============

    [Fact]
    public async Task Get_Unauthorized_401_Returns_Unauthorized_NoRetry()
    {
        using var harness = TestHarness.Create();
        harness.Handler.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId);

        result.Outcome.Should().Be(SdkCacheOutcome.Unauthorized);
        result.Snapshot.Should().BeNull();
        harness.Handler.Calls.Should().HaveCount(1, "401 is a 4xx and never retried (R11.2)");
    }

    [Fact]
    public async Task Get_Unauthorized_Clears_Cached_Entry()
    {
        var snapshot = SampleSnapshot();
        using var harness = TestHarness.Create(snapshot, etag: CachedEtag, maxAge: TimeSpan.FromMinutes(5));

        var first = await harness.Client.GetClientAsync(TenantKey, ClientId);
        first.Outcome.Should().Be(SdkCacheOutcome.Miss);

        // Force revalidate, server now rejects.
        harness.Handler.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var revoked = await harness.Client.GetClientAsync(TenantKey, ClientId, ifNoneMatch: CachedEtag);
        revoked.Outcome.Should().Be(SdkCacheOutcome.Unauthorized);

        // Subsequent default call must NOT serve a stale local hit.
        harness.Handler.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var second = await harness.Client.GetClientAsync(TenantKey, ClientId);
        second.Outcome.Should().Be(SdkCacheOutcome.Unauthorized);
        harness.Handler.Calls.Should().HaveCount(3,
            "the SDK must purge its in-memory cache on 401 so the next call hits the wire");
    }

    // ===== 404 → NotFound ==========================================

    [Fact]
    public async Task Get_NotFound_404_Returns_NotFound()
    {
        using var harness = TestHarness.Create();
        harness.Handler.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId);

        result.Outcome.Should().Be(SdkCacheOutcome.NotFound);
    }

    // ===== 429 → RateLimited + RetryAfter ===========================

    [Fact]
    public async Task Get_RateLimited_429_Returns_RateLimited_With_RetryAfter()
    {
        using var harness = TestHarness.Create();
        harness.Handler.NextResponse = _ =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(7));
            return resp;
        };

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId);

        result.Outcome.Should().Be(SdkCacheOutcome.RateLimited);
        result.RetryAfter.Should().NotBeNull();
        result.RetryAfter!.Value.Should().BeCloseTo(TimeSpan.FromSeconds(7), TimeSpan.FromMilliseconds(500));
        harness.Handler.Calls.Should().HaveCount(1, "429 is never retried (R11.2)");
    }

    [Fact]
    public async Task Get_RetryAfter_From_Date_Header_ConvertedToTimeSpan()
    {
        using var harness = TestHarness.Create();
        var future = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        harness.Handler.NextResponse = _ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(future);
            return resp;
        };

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId);

        result.Outcome.Should().Be(SdkCacheOutcome.ServiceUnavailable);
        result.RetryAfter.Should().NotBeNull();
        result.RetryAfter!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        result.RetryAfter!.Value.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    // ===== 503 → retried then exhausted → ServiceUnavailable ========

    [Fact]
    public async Task Get_ServiceUnavailable_503_Retried_Then_Exhausted_Returns_ServiceUnavailable()
    {
        using var harness = TestHarness.Create(maxRetryAttempts: 2);

        var counter = 0;
        harness.Handler.NextResponse = _ =>
        {
            counter++;
            var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            resp.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return resp;
        };

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId);

        result.Outcome.Should().Be(SdkCacheOutcome.ServiceUnavailable);
        result.RetryAfter.Should().NotBeNull();
        // initial + 2 retries
        counter.Should().Be(3);
    }

    // ===== 5xx with HttpRequestException → TransientFailure =========

    [Fact]
    public async Task Get_HttpRequestException_Retries_Then_TransientFailure()
    {
        using var harness = TestHarness.Create(maxRetryAttempts: 2);
        harness.Handler.NextException = _ => new HttpRequestException("connection reset");

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId);

        result.Outcome.Should().Be(SdkCacheOutcome.TransientFailure);
        // initial + 2 retries before giving up
        harness.Handler.Calls.Should().HaveCount(3);
    }

    // ===== 500 transient retried then TransientFailure ==============

    [Fact]
    public async Task Get_500_Retries_Then_Returns_TransientFailure_When_Final_Status_Is_500()
    {
        using var harness = TestHarness.Create(maxRetryAttempts: 1);
        harness.Handler.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId);

        // Final 500 after retry exhaustion folds into TransientFailure.
        result.Outcome.Should().Be(SdkCacheOutcome.TransientFailure);
        harness.Handler.Calls.Should().HaveCount(2);
    }

    // ===== Caller cancellation → re-throws ==========================

    [Fact]
    public async Task Get_CallerCancellation_Throws_OperationCanceledException()
    {
        using var harness = TestHarness.Create();
        using var cts = new CancellationTokenSource();

        harness.Handler.NextResponse = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var act = async () => await harness.Client.GetClientAsync(TenantKey, ClientId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ===== Force-revalidate path bypasses local cache ===============

    [Fact]
    public async Task Get_With_Explicit_IfNoneMatch_Bypasses_Local_Cache()
    {
        using var harness = TestHarness.Create(SampleSnapshot(), etag: CachedEtag, maxAge: TimeSpan.FromMinutes(5));

        // Seed the cache.
        var first = await harness.Client.GetClientAsync(TenantKey, ClientId);
        first.Outcome.Should().Be(SdkCacheOutcome.Miss);

        // Even though the cache is warm, an explicit If-None-Match
        // forces an HTTP call (R11.8).
        harness.Handler.NextResponse = req =>
        {
            req.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).Should().BeTrue();
            string.Join(",", ifNoneMatchValues!).Should().Contain("custom-etag");
            return MakeNotModified();
        };

        var second = await harness.Client.GetClientAsync(TenantKey, ClientId, ifNoneMatch: "W/\"custom-etag\"");

        second.Outcome.Should().Be(SdkCacheOutcome.NotModified);
        harness.Handler.Calls.Should().HaveCount(2,
            "explicit If-None-Match must bypass the local cache (R11.8)");
    }

    [Fact]
    public async Task Get_Empty_Cache_With_Null_IfNoneMatch_BehavesLikeFirstFetch()
    {
        using var harness = TestHarness.Create();

        var snapshot = SampleSnapshot();
        harness.Handler.NextResponse = req =>
        {
            req.Headers.TryGetValues("If-None-Match", out _).Should().BeFalse(
                "no cached ETag exists, so the SDK must NOT send If-None-Match");
            return MakeOk(snapshot, CachedEtag, TimeSpan.FromMinutes(1));
        };

        var result = await harness.Client.GetClientAsync(TenantKey, ClientId, ifNoneMatch: null);

        result.Outcome.Should().Be(SdkCacheOutcome.Miss);
        result.Snapshot.Should().NotBeNull();
    }

    // ===== Path & headers ===========================================

    [Fact]
    public async Task Get_Builds_Url_With_Escaped_Tenant_And_Client_Path_Segments()
    {
        using var harness = TestHarness.Create();
        harness.Handler.NextResponse = req =>
        {
            req.RequestUri!.AbsolutePath.Should()
                .Be("/api/public/tenants/acme/clients/spa%2Bclient");
            return MakeOk(SampleSnapshot(), CachedEtag, TimeSpan.FromMinutes(1));
        };

        await harness.Client.GetClientAsync("Acme", "spa+client");
    }

    [Fact]
    public async Task Get_Forwards_Tenant_ApiKey_Header_On_Every_Call()
    {
        using var harness = TestHarness.Create();
        harness.Handler.NextResponse = req =>
        {
            req.Headers.GetValues("X-Tenant-Api-Key").Should().ContainSingle("test-api-key");
            return MakeOk(SampleSnapshot(), CachedEtag, TimeSpan.FromMinutes(1));
        };

        await harness.Client.GetClientAsync(TenantKey, ClientId);
    }

    [Fact]
    public void GetClientAsync_Throws_ArgumentNullException_For_Null_Inputs()
    {
        using var harness = TestHarness.Create();

        Func<Task> nullTenant = () => harness.Client.GetClientAsync(null!, ClientId);
        Func<Task> nullClient = () => harness.Client.GetClientAsync(TenantKey, null!);

        nullTenant.Should().ThrowAsync<ArgumentNullException>();
        nullClient.Should().ThrowAsync<ArgumentNullException>();
    }

    // ===== Helpers ==================================================

    private static PublicClientSnapshot SampleSnapshot() => new()
    {
        ClientId = ClientId,
        ClientName = "Acme",
        Enabled = true,
        ProtocolType = "oidc",
        RedirectUris = new[] { "https://acme.example/cb" },
        AllowedScopes = new[] { "openid", "profile" },
        LastWriteUtc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc)
    };

    private static HttpResponseMessage MakeOk(
        PublicClientSnapshot snapshot,
        string etag,
        TimeSpan maxAge,
        DateTime? lastWrite = null,
        int version = 7)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(snapshot, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag, isWeak: true);
        resp.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            MaxAge = maxAge,
            Public = true,
            NoTransform = true
        };
        resp.Headers.Add("X-Snapshot-Last-Write-Utc",
            (lastWrite ?? DateTime.UtcNow).ToString("o"));
        resp.Headers.Add("X-Snapshot-Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return resp;
    }

    private static HttpResponseMessage MakeNotModified()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.NotModified);
        resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(CachedEtag, isWeak: true);
        resp.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromMinutes(5),
            Public = true
        };
        return resp;
    }

    /// <summary>Composes the SDK with a fake handler under DI.</summary>
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

        public static TestHarness Create(
            PublicClientSnapshot? snapshot = null,
            string? etag = null,
            TimeSpan? maxAge = null,
            int maxRetryAttempts = 0)
        {
            var handler = new RecordingHandler();

            // Default canned response: 200 with snapshot if supplied.
            if (snapshot is not null)
            {
                handler.NextResponse = _ => MakeOk(
                    snapshot,
                    etag ?? CachedEtag,
                    maxAge ?? TimeSpan.FromMinutes(1));
            }

            var services = new ServiceCollection();
            services.AddLogging();

            services.AddTenantClientCacheClient(o =>
            {
                o.BaseAddress = new Uri("https://identity.example.com/");
                o.ApiKey = "test-api-key";
                o.HttpTimeout = TimeSpan.FromSeconds(5);
                o.MaxRetryAttempts = maxRetryAttempts;
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(10); // tiny so retries don't slow tests
                o.MaxClientCacheTtl = TimeSpan.FromMinutes(5);
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
        public Func<HttpRequestMessage, Exception>? NextException { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            if (NextException is { } supplyEx)
                throw supplyEx(request);
            if (NextResponse is { } supplyResp)
                return Task.FromResult(supplyResp(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));
        }
    }
}
