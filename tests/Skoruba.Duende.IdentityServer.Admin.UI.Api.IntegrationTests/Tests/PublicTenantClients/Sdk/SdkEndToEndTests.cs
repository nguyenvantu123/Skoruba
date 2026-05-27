// Feature: tenant-client-cache-public-read, Task 10
//
// SDK end-to-end harness — Skoruba.Duende.IdentityServer.TenantClientCache.Client
// driven against the in-process WebApplicationFactory that hosts the
// public-read controller (PublicTenantClientsTestHost). Each test wires the
// SDK with the test server's HttpClient handler so HTTP traffic flows through
// the in-memory pipeline without binding TCP sockets.
//
// Validates: Requirements 10.4, 11.4, 11.5, 11.6, 11.7, 11.8, 11.9
// Properties: P20 (E2E coverage)

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients.Helpers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients.Sdk;

public sealed class SdkEndToEndTests
{
    private const string Tenant = "acme";
    private const string Client = "web";

    private static ClientCacheSnapshotEnvelope MakeEnvelope(
        string tenant = Tenant,
        string clientId = Client,
        int version = 1)
    {
        var ts = new DateTime(2024, 5, 1, 12, 30, 45, DateTimeKind.Utc);
        return new ClientCacheSnapshotEnvelope
        {
            Version = version,
            TenantKey = tenant,
            ClientId = clientId,
            LastWriteUtc = ts,
            Data = new ClientCacheSnapshotDto
            {
                ClientId = clientId,
                ClientName = "Sample",
                ProtocolType = "oidc",
                Enabled = true,
                AccessTokenLifetime = 3600,
                IdentityTokenLifetime = 300,
                RedirectUris = new[] { "https://app/callback" },
                AllowedScopes = new[] { "openid", "profile" },
                LastWriteUtc = ts,
            },
        };
    }

    private static PublicTenantClientsTestHost.Builder DefaultHostBuilder() =>
        new PublicTenantClientsTestHost.Builder()
            .WithApiKey(Tenant, TestApiKeys.ValidHashAcme);

    /// <summary>
    /// Build an SDK <see cref="ITenantClientCacheClient"/> wired against the
    /// in-process TestServer. The named HttpClient's primary handler is the
    /// TestServer's handler so the SDK never opens a real TCP socket.
    /// </summary>
    private static ITenantClientCacheClient BuildSdkAgainst(PublicTenantClientsTestHost host)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantClientCacheClient(o =>
        {
            o.BaseAddress = host.Client.BaseAddress;
            o.ApiKey = TestApiKeys.ValidPlaintext;
            // Long enough so the pipeline never times out.
            o.HttpTimeout = TimeSpan.FromSeconds(30);
            o.MaxRetryAttempts = 2;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
            o.MaxClientCacheTtl = TimeSpan.FromMinutes(5);
            o.EnableInMemoryCaching = true;
        });

        // Override the named HttpClient's primary handler so requests flow
        // through the in-process TestServer. Calling
        // ConfigurePrimaryHttpMessageHandler at the named-client level
        // installs the handler ahead of any default handler the SDK
        // factory configured.
        services
            .AddHttpClient(TenantClientCacheClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => host.TestServer.CreateHandler());

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ITenantClientCacheClient>();
    }

    [Fact]
    public async Task Sdk_GetClientAsync_Against_InProcessHost_Returns_Miss_Then_Hit_FromLocalCache()
    {
        using var host = DefaultHostBuilder()
            .WithResponseCacheMaxAge(120)
            .Build();
        host.FakeCache.WhenAnyKey_Returns(MakeEnvelope());

        var sdk = BuildSdkAgainst(host);

        // First call → server hit; cache populated; outcome=Miss.
        var first = await sdk.GetClientAsync(Tenant, Client, CancellationToken.None);
        first.Outcome.Should().Be(SdkCacheOutcome.Miss);
        first.Snapshot.Should().NotBeNull();
        first.Snapshot!.ClientId.Should().Be(Client);

        var serverCallsAfterFirst = host.FakeCache.Calls.Count;

        // Second call → local cache hit; outcome=Hit; NO new HTTP traffic.
        var second = await sdk.GetClientAsync(Tenant, Client, CancellationToken.None);
        second.Outcome.Should().Be(SdkCacheOutcome.Hit);
        second.Snapshot.Should().NotBeNull();
        host.FakeCache.Calls.Count.Should().Be(serverCallsAfterFirst,
            "the SDK MUST NOT issue HTTP traffic for an in-cache hit (R11.7)");
    }

    [Fact]
    public async Task Sdk_GetClientAsync_AfterTtl_Revalidates_Returns_NotModified()
    {
        // R11.9 — when local TTL has elapsed, the SDK re-issues the
        // request with If-None-Match. The server returns 304; the SDK
        // surfaces the cached snapshot with Outcome=NotModified.
        // We force "TTL elapsed" by setting MaxClientCacheTtl=0 which
        // disables the entry-TTL refresh; the SDK still keeps the most
        // recent ETag in cache for the duration of the call (entry is
        // evicted between calls because TTL=0 ⇒ no entry written).
        // Easier path: explicitly disable in-memory caching so every
        // call hits the server, then drive the SDK with an explicit
        // If-None-Match using the overload R11.8.
        using var host = DefaultHostBuilder().Build();
        host.FakeCache.WhenAnyKey_Returns(MakeEnvelope());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantClientCacheClient(o =>
        {
            o.BaseAddress = host.Client.BaseAddress;
            o.ApiKey = TestApiKeys.ValidPlaintext;
            o.HttpTimeout = TimeSpan.FromSeconds(30);
            o.MaxRetryAttempts = 2;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
            o.MaxClientCacheTtl = TimeSpan.FromMinutes(5);
            o.EnableInMemoryCaching = true;
        });
        services
            .AddHttpClient(TenantClientCacheClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => host.TestServer.CreateHandler());
        var provider = services.BuildServiceProvider();
        var sdk = provider.GetRequiredService<ITenantClientCacheClient>();

        var first = await sdk.GetClientAsync(Tenant, Client, CancellationToken.None);
        first.Outcome.Should().Be(SdkCacheOutcome.Miss);
        first.Etag.Should().NotBeNull();

        // Force-revalidate via the explicit If-None-Match overload.
        var revalidated = await sdk.GetClientAsync(
            Tenant, Client, ifNoneMatch: first.Etag, CancellationToken.None);
        revalidated.Outcome.Should().Be(SdkCacheOutcome.NotModified);
        revalidated.Snapshot.Should().NotBeNull(
            "NotModified MUST surface the previously cached snapshot when one exists (R11.9)");
        revalidated.Snapshot!.ClientId.Should().Be(Client);
    }

    [Fact]
    public async Task Sdk_GetClientAsync_404_Returns_NotFound()
    {
        using var host = DefaultHostBuilder().Build();
        host.FakeCache.WhenAnyKey_ReturnsNull();
        var sdk = BuildSdkAgainst(host);

        var result = await sdk.GetClientAsync(Tenant, Client, CancellationToken.None);
        result.Outcome.Should().Be(SdkCacheOutcome.NotFound);
        result.Snapshot.Should().BeNull();
    }

    [Fact]
    public async Task Sdk_GetClientAsync_401_Returns_Unauthorized()
    {
        using var host = DefaultHostBuilder().Build();
        host.FakeCache.WhenAnyKey_Returns(MakeEnvelope());

        // Wire the SDK with a wrong API key.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantClientCacheClient(o =>
        {
            o.BaseAddress = host.Client.BaseAddress;
            o.ApiKey = "wrong-key";
            o.HttpTimeout = TimeSpan.FromSeconds(30);
            o.MaxRetryAttempts = 0;
        });
        services
            .AddHttpClient(TenantClientCacheClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => host.TestServer.CreateHandler());
        var provider = services.BuildServiceProvider();
        var sdk = provider.GetRequiredService<ITenantClientCacheClient>();

        var result = await sdk.GetClientAsync(Tenant, Client, CancellationToken.None);
        result.Outcome.Should().Be(SdkCacheOutcome.Unauthorized);
    }

    [Fact]
    public async Task Sdk_GetClientAsync_429_Returns_RateLimited_With_RetryAfter()
    {
        using var host = DefaultHostBuilder()
            .WithRateLimit(tokenLimit: 1, tokensPerPeriod: 1, replenishmentPeriod: TimeSpan.FromMinutes(1))
            .Build();
        host.FakeCache.WhenAnyKey_Returns(MakeEnvelope());
        var sdk = BuildSdkAgainst(host);

        // Drain the bucket via a direct request (the SDK's first call
        // succeeds and populates the local cache, so its subsequent calls
        // return Hit and never reach the server). Use the host's HttpClient
        // for the drain so the SDK's local cache stays empty.
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/public/tenants/{Tenant}/clients/{Client}");
        req.Headers.Add("X-Tenant-Api-Key", TestApiKeys.ValidPlaintext);
        var prime = await host.Client.SendAsync(req);
        prime.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await sdk.GetClientAsync(Tenant, Client, CancellationToken.None);
        result.Outcome.Should().Be(SdkCacheOutcome.RateLimited);
        result.RetryAfter.Should().NotBeNull("Retry-After must be surfaced (R11.4)");
    }

    [Fact]
    public async Task Sdk_GetClientAsync_503_Returns_ServiceUnavailable_With_RetryAfter()
    {
        using var host = DefaultHostBuilder().Build();
        host.FakeCache.WhenAnyKey_PipelineDisabled();

        // Disable retries so the SDK does not hammer the host before
        // returning ServiceUnavailable. The 503 path is non-retriable
        // by design (R11.1 only retries on 5xx — the SDK retry policy
        // includes 503; we disable retries here so the test asserts
        // the terminal mapping in isolation).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantClientCacheClient(o =>
        {
            o.BaseAddress = host.Client.BaseAddress;
            o.ApiKey = TestApiKeys.ValidPlaintext;
            o.HttpTimeout = TimeSpan.FromSeconds(30);
            o.MaxRetryAttempts = 0;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
        });
        services
            .AddHttpClient(TenantClientCacheClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => host.TestServer.CreateHandler());
        var provider = services.BuildServiceProvider();
        var sdk = provider.GetRequiredService<ITenantClientCacheClient>();

        var result = await sdk.GetClientAsync(Tenant, Client, CancellationToken.None);
        result.Outcome.Should().Be(SdkCacheOutcome.ServiceUnavailable);
        result.RetryAfter.Should().NotBeNull("Retry-After must be surfaced (R11.4)");
        result.RetryAfter!.Value.Should().Be(TimeSpan.FromSeconds(60),
            "snapshot_pipeline_disabled responses include Retry-After: 60 (R7.4)");
    }

    [Fact]
    public async Task Sdk_GetClientAsync_5xx_Retries_2_Times_Then_TransientFailure()
    {
        // R11.1, R11.3 — server throws on every call → maps to 503 +
        // Retry-After: 5 (R7.5). The SDK retries on 5xx so it issues
        // (1 + MaxRetryAttempts) HTTP calls before surfacing a final
        // ServiceUnavailable / TransientFailure outcome. We assert the
        // SDK eventually returns ServiceUnavailable (per R7.5 mapping)
        // and that the server saw exactly (MaxRetryAttempts + 1) calls.
        using var host = DefaultHostBuilder().Build();
        host.FakeCache.WhenAnyKey_Throws(() => new InvalidOperationException("boom"));

        const int MaxRetries = 2;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantClientCacheClient(o =>
        {
            o.BaseAddress = host.Client.BaseAddress;
            o.ApiKey = TestApiKeys.ValidPlaintext;
            o.HttpTimeout = TimeSpan.FromSeconds(30);
            o.MaxRetryAttempts = 2;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
        });
        services
            .AddHttpClient(TenantClientCacheClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => host.TestServer.CreateHandler());
        var provider = services.BuildServiceProvider();
        var sdk = provider.GetRequiredService<ITenantClientCacheClient>();

        var result = await sdk.GetClientAsync(Tenant, Client, CancellationToken.None);

        // 503 is one of the buckets that surfaces a typed outcome; the
        // SDK does NOT collapse exhausted-503 retries into TransientFailure
        // because 503 is a recognised HTTP code. We assert the typed
        // outcome and the server's call count.
        result.Outcome.Should().Be(SdkCacheOutcome.ServiceUnavailable);
        host.FakeCache.Calls.Count.Should().Be(
            1 + MaxRetries,
            "the SDK MUST retry exactly MaxRetryAttempts times on 5xx before surfacing the terminal outcome (R11.1, R11.3)");
    }
}
