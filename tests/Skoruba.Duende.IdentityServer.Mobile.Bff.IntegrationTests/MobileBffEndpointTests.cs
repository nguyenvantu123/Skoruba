// Integration tests for the Mobile BFF endpoint.
//
// Synthetic test prefix `test-tenant-` per AGENTS.md. All tenant keys and
// client IDs are fake; no real production identifiers appear here.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

using Skoruba.Duende.IdentityServer.Mobile.Bff.IntegrationTests.Infrastructure;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.IntegrationTests;

public sealed class MobileBffEndpointTests : IClassFixture<MobileBffEndpointTests.Fixture>
{
    public sealed class Fixture : IDisposable
    {
        public MobileBffWebApplicationFactory Factory { get; }

        public Fixture()
        {
            Factory = new MobileBffWebApplicationFactory();
        }

        public void Dispose() => Factory.Dispose();
    }

    private const string SyntheticTenantKey = "test-tenant-alpha";
    private const string ApiKeyValue = "test-api-key-PLACEHOLDER";

    private readonly Fixture _fixture;

    public MobileBffEndpointTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient CreateClient(string? tenantKey = SyntheticTenantKey, bool authenticated = true)
    {
        // Reset only counters/logs; tests stage SDK outcomes before
        // calling CreateClient, so we MUST NOT touch the responder here.
        _fixture.Factory.ResetCounters();

        var client = _fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        if (authenticated)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationDefaults.AuthMarkerHeader, "1");
        }
        if (tenantKey is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationDefaults.TenantKeyHeader, tenantKey);
        }
        // Send a Bearer marker so the authn pipeline gets exercised end-to-end.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-test-token");
        return client;
    }

    [Fact]
    public async Task GetClient_Returns_200_With_Slim_Body_When_Sdk_Returns_Hit()
    {
        var snapshot = TestSnapshots.Sample("test-client-001");
        _fixture.Factory.FakeSdk.WhenAnyKey_Returns(new TenantClientSnapshotResult(
            Snapshot: snapshot,
            Etag: "W/\"abc123\"",
            LastWriteUtc: DateTimeOffset.UtcNow,
            Version: 1,
            Outcome: SdkCacheOutcome.Hit,
            RetryAfter: null));

        using var client = CreateClient();

        using var response = await client.GetAsync("/mobile/clients/test-client-001");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // EntityTagHeaderValue.ToString() includes the W/ prefix.
        response.Headers.ETag?.ToString().Should().Be("W/\"abc123\"");
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(60));

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Slim shape: only the documented 11 fields are allowed.
        var allowedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "clientId",
            "clientName",
            "enabled",
            "redirectUris",
            "postLogoutRedirectUris",
            "allowedScopes",
            "allowedGrantTypes",
            "requirePkce",
            "initiateLoginUri",
            "accessTokenLifetime",
            "identityTokenLifetime"
        };
        var actualKeys = root.EnumerateObject().Select(p => p.Name).ToList();
        actualKeys.Should().BeEquivalentTo(allowedKeys);
        actualKeys.Should().NotContain("clientSecrets");
        actualKeys.Should().NotContain("claims");
        actualKeys.Should().NotContain("properties");

        root.GetProperty("clientId").GetString().Should().Be("test-client-001");
        root.GetProperty("requirePkce").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetClient_Returns_403_When_TenantKey_Claim_Missing()
    {
        using var client = CreateClient(tenantKey: null);

        using var response = await client.GetAsync("/mobile/clients/test-client-002");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _fixture.Factory.FakeSdk.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("/mobile/clients/%20")]   // whitespace-only
    [InlineData("/mobile/clients/has space")] // disallowed character
    public async Task GetClient_Returns_400_When_ClientId_Empty_Or_Whitespace(string url)
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        payload!.Error.Should().Be("invalid_client_id");
        _fixture.Factory.FakeSdk.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetClient_Returns_404_When_Sdk_Returns_NotFound()
    {
        _fixture.Factory.FakeSdk.WhenAnyKey_NotFound();

        using var client = CreateClient();

        using var response = await client.GetAsync("/mobile/clients/test-client-missing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var payload = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        payload!.Error.Should().Be("client_not_found");
    }

    [Fact]
    public async Task GetClient_Returns_502_When_Sdk_Returns_Unauthorized()
    {
        _fixture.Factory.FakeSdk.WhenAnyKey_Unauthorized();

        using var client = CreateClient();

        using var response = await client.GetAsync("/mobile/clients/test-client-003");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        // Logged at Error level (BFF API-key issue, not user issue).
        _fixture.Factory.Logs.Entries
            .Should().Contain(entry =>
                entry.Level == LogLevel.Error
                && entry.RenderedMessage.Contains("Unauthorized", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetClient_Returns_503_With_RetryAfter_When_Sdk_Returns_RateLimited()
    {
        _fixture.Factory.FakeSdk.WhenAnyKey_RateLimited(TimeSpan.FromSeconds(7));

        using var client = CreateClient();

        using var response = await client.GetAsync("/mobile/clients/test-client-004");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryGetValues(HeaderNames.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Contain("7");
    }

    [Fact]
    public async Task GetClient_Forwards_IfNoneMatch_And_Returns_304_On_NotModified()
    {
        const string etag = "W/\"e-deadbeef\"";
        _fixture.Factory.FakeSdk.WhenIfNoneMatch_NotModified(etag);

        using var client = CreateClient();
        // HttpClient strips reserved headers from DefaultRequestHeaders, so
        // build the request explicitly.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mobile/clients/test-client-005");
        request.Headers.IfNoneMatch.Add(System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        _fixture.Factory.FakeSdk.LastIfNoneMatch.Should().Be(etag);
    }

    [Fact]
    public async Task GetClient_Logs_NoApiKey_NoSnapshotBody_Leakage()
    {
        var snapshot = TestSnapshots.Sample("test-client-006");
        _fixture.Factory.FakeSdk.WhenAnyKey_Returns(new TenantClientSnapshotResult(
            Snapshot: snapshot,
            Etag: "W/\"abc\"",
            LastWriteUtc: DateTimeOffset.UtcNow,
            Version: 1,
            Outcome: SdkCacheOutcome.Hit,
            RetryAfter: null));

        using var client = CreateClient();

        using var response = await client.GetAsync("/mobile/clients/test-client-006");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Inspect every captured log entry: rendered message + each parameter
        // value. Neither the API key nor any redirect URI from the snapshot
        // body must appear.
        foreach (var entry in _fixture.Factory.Logs.Entries)
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

        // Sanity: at least one structured log entry was emitted.
        _fixture.Factory.Logs.Entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetClient_Returns_404_When_ClientId_Routing_Slot_Empty()
    {
        // ASP.NET routing requires a non-empty value for {clientId}, so a
        // bare "/mobile/clients/" returns 404. Document the behavior so the
        // contract is explicit.
        using var client = CreateClient();

        using var response = await client.GetAsync("/mobile/clients/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_Endpoint_Returns_200_Without_Auth()
    {
        using var client = _fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record ErrorEnvelope(string Error)
    {
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string Error { get; init; } = Error;
    }
}
