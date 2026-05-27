// Integration tests for the anonymous Mobile BFF bootstrap endpoint
// (`GET /mobile/bootstrap/{tenantKey}/{clientId}`).
//
// All tenant keys / client IDs use the synthetic `test-tenant-` prefix per
// AGENTS.md. No real production identifiers appear here.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

using Skoruba.Duende.IdentityServer.Mobile.Bff.IntegrationTests.Infrastructure;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.IntegrationTests;

public sealed class MobileBootstrapEndpointTests
{
    private const string SyntheticTenantKey = "test-tenant-bootstrap";
    private const string ApiKeyValue = "test-api-key-PLACEHOLDER";
    private const string ConfiguredAuthority = "https://sts.test-tenant.example.com";

    /// <summary>
    /// Build a fresh factory per test so config overlays (e.g. tighter rate
    /// limits) don't leak across tests, and the rate-limiter state starts
    /// clean for each scenario.
    /// </summary>
    private static OverlayedHost CreateFactory(
        IDictionary<string, string?>? overrides = null,
        string? authority = ConfiguredAuthority)
    {
        var baseFactory = new MobileBffWebApplicationFactory();

        var overlayed = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var overlay = new Dictionary<string, string?>();
                if (!string.IsNullOrEmpty(authority))
                {
                    overlay["MobileBff:Authentication:Authority"] = authority;
                }
                if (overrides is not null)
                {
                    foreach (var kv in overrides)
                    {
                        overlay[kv.Key] = kv.Value;
                    }
                }
                if (overlay.Count > 0)
                {
                    config.AddInMemoryCollection(overlay);
                }
            });
        });

        return new OverlayedHost(baseFactory, overlayed);
    }

    /// <summary>
    /// Test-only adapter that bundles the underlying factory (so tests can
    /// reach <c>FakeSdk</c> / <c>Logs</c>) with the overlayed instance that
    /// applies per-test configuration. Disposes both on cleanup.
    /// </summary>
    private sealed class OverlayedHost : IAsyncDisposable
    {
        private readonly MobileBffWebApplicationFactory _baseFactory;
        private readonly WebApplicationFactory<Program> _overlayed;

        public OverlayedHost(MobileBffWebApplicationFactory baseFactory, WebApplicationFactory<Program> overlayed)
        {
            _baseFactory = baseFactory;
            _overlayed = overlayed;
        }

        public FakeTenantClientCacheClient FakeSdk => _baseFactory.FakeSdk;
        public CapturingLoggerProvider Logs => _baseFactory.Logs;

        public HttpClient CreateClient() => _overlayed.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        public HttpClient CreateClient(WebApplicationFactoryClientOptions options) => _overlayed.CreateClient(options);

        public async ValueTask DisposeAsync()
        {
            await _overlayed.DisposeAsync().ConfigureAwait(false);
            await _baseFactory.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Bootstrap_Returns_200_With_Authority_And_Slim_Body_When_Sdk_Hit()
    {
        await using var factory = CreateFactory();
        var snapshot = TestSnapshots.Sample("test-client-bootstrap");
        factory.FakeSdk.WhenAnyKey_Returns(new TenantClientSnapshotResult(
            Snapshot: snapshot,
            Etag: "W/\"boot-001\"",
            LastWriteUtc: DateTimeOffset.UtcNow,
            Version: 1,
            Outcome: SdkCacheOutcome.Hit,
            RetryAfter: null));

        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-bootstrap");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Cache-Control: public (CDN-cacheable; document the trade-off).
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(300));

        // ETag propagated from SDK.
        response.Headers.ETag?.ToString().Should().Be("W/\"boot-001\"");

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // EXACTLY 8 fields, no extras.
        var allowedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "authority",
            "clientId",
            "clientName",
            "redirectUris",
            "postLogoutRedirectUris",
            "allowedScopes",
            "allowedGrantTypes",
            "requirePkce"
        };
        var actualKeys = root.EnumerateObject().Select(p => p.Name).ToList();
        actualKeys.Should().BeEquivalentTo(allowedKeys);

        // No token lifetime / advanced fields.
        actualKeys.Should().NotContain("accessTokenLifetime");
        actualKeys.Should().NotContain("identityTokenLifetime");
        actualKeys.Should().NotContain("frontChannelLogoutUri");
        actualKeys.Should().NotContain("backChannelLogoutUri");

        // Defensive filter (regex from spec): no field name matching
        // *secret* or *claim* (case-insensitive).
        var defensiveDeniedPattern = new Regex("(?i).*(secret|claim).*", RegexOptions.CultureInvariant);
        actualKeys.Should().NotContain(name => defensiveDeniedPattern.IsMatch(name));
        actualKeys.Should().NotContain("clientSecrets");
        actualKeys.Should().NotContain("claims");
        actualKeys.Should().NotContain("properties");

        root.GetProperty("authority").GetString().Should().Be(ConfiguredAuthority);
        root.GetProperty("clientId").GetString().Should().Be("test-client-bootstrap");
        root.GetProperty("requirePkce").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Bootstrap_Authority_Field_Matches_Config()
    {
        const string customAuthority = "https://sts.custom.example.org";
        await using var factory = CreateFactory(authority: customAuthority);
        factory.FakeSdk.WhenAnyKey_Returns(new TenantClientSnapshotResult(
            Snapshot: TestSnapshots.Sample("test-client-cfg"),
            Etag: "W/\"cfg\"",
            LastWriteUtc: null,
            Version: 1,
            Outcome: SdkCacheOutcome.Hit,
            RetryAfter: null));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-cfg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("authority").GetString().Should().Be(customAuthority);
    }

    [Fact]
    public async Task Bootstrap_Anonymous_No_Bearer_Token_Required()
    {
        await using var factory = CreateFactory();
        factory.FakeSdk.WhenAnyKey_Returns(new TenantClientSnapshotResult(
            Snapshot: TestSnapshots.Sample("test-client-anon"),
            Etag: "W/\"a\"",
            LastWriteUtc: null,
            Version: 1,
            Outcome: SdkCacheOutcome.Hit,
            RetryAfter: null));

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        // Explicitly clear any default headers — fresh-install simulation.
        client.DefaultRequestHeaders.Clear();

        using var response = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-anon");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bootstrap_Returns_404_When_Sdk_Returns_NotFound()
    {
        await using var factory = CreateFactory();
        factory.FakeSdk.WhenAnyKey_NotFound();

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-missing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var payload = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        payload!.Error.Should().Be("client_not_found");
    }

    [Theory]
    // tenantKey: empty (route slot empty → routing returns 404, not 400 —
    // documented separately). Cases below all reach the handler with a
    // non-empty path segment but fail the post-normalize regex.
    //
    // NOTE: Per the spec, tenantKey is validated AFTER `Trim().ToLowerInvariant()`,
    // so "UPPERCASE" normalizes to "uppercase" and is ACCEPTED. The cases
    // below are characters that don't survive normalization.
    [InlineData("has space")]                  // space (URL-encoded reaches handler)
    [InlineData("has!bang")]                  // disallowed special char
    [InlineData("has.dot")]                   // dot disallowed for tenantKey
    [InlineData("has@at")]                    // @ disallowed
    public async Task Bootstrap_Returns_400_When_TenantKey_Invalid(string badTenantKey)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var encoded = Uri.EscapeDataString(badTenantKey);
        using var response = await client.GetAsync(
            $"/mobile/bootstrap/{encoded}/test-client-x");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        payload!.Error.Should().Be("invalid_tenant_key");
        factory.FakeSdk.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Bootstrap_Returns_400_When_TenantKey_Empty_Whitespace()
    {
        // %20 is whitespace — passes route slot but fails normalization.
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/mobile/bootstrap/%20/test-client-x");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        payload!.Error.Should().Be("invalid_tenant_key");
        factory.FakeSdk.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("%20")]          // whitespace-only
    [InlineData("has%20space")]  // space (URL-encoded)
    public async Task Bootstrap_Returns_400_When_ClientId_Invalid(string urlEncodedClientId)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/{urlEncodedClientId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        payload!.Error.Should().Be("invalid_client_id");
        factory.FakeSdk.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Bootstrap_Returns_502_When_Sdk_Returns_Unauthorized()
    {
        await using var factory = CreateFactory();
        factory.FakeSdk.WhenAnyKey_Unauthorized();

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-uauth");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var payload = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        payload!.Error.Should().Be("upstream_misconfigured");

        factory.Logs.Entries
            .Should().Contain(entry =>
                entry.Level == LogLevel.Error
                && entry.RenderedMessage.Contains("Unauthorized", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bootstrap_Returns_503_With_RetryAfter_When_Sdk_Returns_RateLimited()
    {
        await using var factory = CreateFactory();
        factory.FakeSdk.WhenAnyKey_RateLimited(TimeSpan.FromSeconds(11));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-rl");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryGetValues(HeaderNames.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Contain("11");
        var payload = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        payload!.Error.Should().Be("snapshot_unavailable");
    }

    [Fact]
    public async Task Bootstrap_Forwards_IfNoneMatch_And_Returns_304_On_NotModified()
    {
        const string etag = "W/\"boot-not-mod\"";
        await using var factory = CreateFactory();
        factory.FakeSdk.WhenIfNoneMatch_NotModified(etag);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-nm");
        request.Headers.IfNoneMatch.Add(System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        factory.FakeSdk.LastIfNoneMatch.Should().Be(etag);
    }

    [Fact]
    public async Task Bootstrap_Returns_429_When_IP_Rate_Limit_Exceeded()
    {
        // Tighten the limiter to 2 permits per 60s window.
        await using var factory = CreateFactory(overrides: new Dictionary<string, string?>
        {
            ["MobileBff:RateLimiting:BootstrapPermitLimit"] = "2",
            ["MobileBff:RateLimiting:BootstrapWindowSeconds"] = "60",
            ["MobileBff:RateLimiting:BootstrapQueueLimit"] = "0"
        });

        factory.FakeSdk.WhenAnyKey_Returns(new TenantClientSnapshotResult(
            Snapshot: TestSnapshots.Sample("test-client-rl-ip"),
            Etag: "W/\"rl\"",
            LastWriteUtc: null,
            Version: 1,
            Outcome: SdkCacheOutcome.Hit,
            RetryAfter: null));

        using var client = factory.CreateClient();

        using var first = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-rl-ip");
        using var second = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-rl-ip");
        using var third = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-rl-ip");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        third.Headers.TryGetValues(HeaderNames.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter!.First().Should().NotBeNullOrEmpty();

        var body = await third.Content.ReadFromJsonAsync<ErrorEnvelope>();
        body!.Error.Should().Be("rate_limit_exceeded");

        factory.Logs.Entries
            .Should().Contain(entry =>
                entry.Level == LogLevel.Warning
                && entry.RenderedMessage.Contains("rate-limit exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bootstrap_Logs_NoApiKey_Or_FullSnapshot_Body_Leakage()
    {
        await using var factory = CreateFactory();
        factory.FakeSdk.WhenAnyKey_Returns(new TenantClientSnapshotResult(
            Snapshot: TestSnapshots.Sample("test-client-leak"),
            Etag: "W/\"leak\"",
            LastWriteUtc: null,
            Version: 1,
            Outcome: SdkCacheOutcome.Hit,
            RetryAfter: null));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/mobile/bootstrap/{SyntheticTenantKey}/test-client-leak");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var entry in factory.Logs.Entries)
        {
            entry.RenderedMessage.Should().NotContain(ApiKeyValue);
            entry.RenderedMessage.Should().NotContain("https://app.example.com/callback");
            foreach (var (_, value) in entry.Fields)
            {
                if (value is null) continue;
                value.Should().NotContain(ApiKeyValue);
                value.Should().NotContain("https://app.example.com/callback");
            }
        }

        // Sanity: bootstrap emits at least one structured outcome log.
        factory.Logs.Entries.Should().NotBeEmpty();
    }

    private sealed record ErrorEnvelope(string Error)
    {
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string Error { get; init; } = Error;
    }
}
