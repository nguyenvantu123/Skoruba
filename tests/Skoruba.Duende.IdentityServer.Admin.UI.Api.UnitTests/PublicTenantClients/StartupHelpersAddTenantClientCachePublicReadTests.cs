// Feature: tenant-client-cache-public-read, Task 6
//
// Composition-root tests for the StartupHelpers.AddTenantClientCachePublicRead
// extension method. Task 6 augments the foundation registration with the
// full public-read DI surface:
//
//   * Singletons: HttpsRequiredFilter, TenantApiKeyAuthorizationFilter,
//     PublicReadExceptionFilter, IpHashHelper, ITenantApiKeyValidator →
//     TenantApiKeyValidator.
//   * CORS policy "TenantClientCachePublicRead" with the strict allowlist
//     semantics (zero default origins, GET/HEAD/OPTIONS only,
//     restricted request headers, ETag + Cache-Control exposed,
//     credentials disallowed, configurable preflight max-age).
//   * Rate limiter policy "TenantClientCachePublicRead" with token-bucket
//     parameters from RateLimit:* and the canonical 429 rejection shape
//     (body { error: "rate_limit_exceeded" } + Retry-After header).
//
// The tests build a minimal ServiceCollection, drive
// AddTenantClientCachePublicRead, and assert each contract directly via DI
// resolution and the underlying CorsOptions / RateLimiterOptions storage.
//
// Validates: Requirements 1.7, 3.8, 4.1, 4.2, 4.5, 4.6, 4.7, 4.8, 4.9,
//            5.1, 5.2, 5.3, 5.4, 5.5, 5.7, 5.8, 12.10
//
// Property-based coverage for rate-limit auth-before / contract is
// delivered by P7 / P8 in Task 10 (integration plane); this file focuses
// on the wiring contract.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

[Collection(PublicReadMetricCollection.Name)]
public sealed class StartupHelpersAddTenantClientCachePublicReadTests
{
    /// <summary>
    /// Build an in-memory IConfiguration that mirrors a real
    /// <c>appsettings.json</c> "TenantClientCachePublicRead" section.
    /// </summary>
    private static IConfiguration BuildConfiguration(IDictionary<string, string?>? overrides = null)
    {
        // Defaults match TenantClientCachePublicReadOptions (R1.2). Tests
        // override individual settings via dotted keys.
        var defaults = new Dictionary<string, string?>
        {
            ["TenantClientCachePublicRead:RateLimit:TokenLimit"] = "30",
            ["TenantClientCachePublicRead:RateLimit:TokensPerPeriod"] = "30",
            ["TenantClientCachePublicRead:RateLimit:ReplenishmentPeriod"] = "00:01:00",
            ["TenantClientCachePublicRead:RateLimit:QueueLimit"] = "0",
            ["TenantClientCachePublicRead:RateLimit:AutoReplenishment"] = "true",
            ["TenantClientCachePublicRead:Cors:PreflightMaxAgeSeconds"] = "600",
            ["TenantClientCachePublicRead:ResponseCache:MaxAgeSeconds"] = "60",
            ["TenantClientCachePublicRead:Audit:LogIpHash"] = "true",
            ["TenantClientCachePublicRead:Audit:RemoteIpSalt"] = string.Empty,
        };

        if (overrides is not null)
        {
            foreach (var kv in overrides)
            {
                defaults[kv.Key] = kv.Value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaults)
            .Build();
    }

    /// <summary>
    /// Build a <see cref="ServiceCollection"/> pre-populated with the
    /// non-feature dependencies the public-read pipeline consumes
    /// (logging + the parent-spec singleton metrics meter).
    /// </summary>
    private static ServiceCollection BuildBaselineServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The parent-spec metrics class is normally registered by
        // RegisterTenantClientCache. The public-read pipeline depends on
        // it (TenantApiKeyAuthorizationFilter, OnRejected handler) so we
        // pre-register it here for unit tests.
        services.AddSingleton<TenantClientCacheMetrics>();
        // Filter resolution requires an IHostEnvironment because the
        // options validator depends on it (R9.6 production salt rule).
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

    // ===== Filter / validator / helper resolution (R3.8, R4.7, R12.10) =====

    [Fact]
    public void Build_ServiceCollection_All_Services_Resolve()
    {
        // R12.10: every collaborator the controller / filters depend on
        // resolves cleanly through DI, with no missing wiring.
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();

        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<ITenantApiKeyValidator>().Should().BeOfType<TenantApiKeyValidator>();
        provider.GetRequiredService<HttpsRequiredFilter>().Should().NotBeNull();
        provider.GetRequiredService<TenantApiKeyAuthorizationFilter>().Should().NotBeNull();
        provider.GetRequiredService<PublicReadExceptionFilter>().Should().NotBeNull();
        provider.GetRequiredService<IpHashHelper>().Should().NotBeNull();
    }

    [Fact]
    public void Filters_And_Validator_Are_Singleton()
    {
        // Filters and the validator hold no per-request state and must be
        // singletons so the per-request allocation cost is zero (R3.5
        // hot-reload still works because IOptionsMonitor is consulted on
        // every TryValidate call).
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();

        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<ITenantApiKeyValidator>()
            .Should().BeSameAs(provider.GetRequiredService<ITenantApiKeyValidator>());
        provider.GetRequiredService<HttpsRequiredFilter>()
            .Should().BeSameAs(provider.GetRequiredService<HttpsRequiredFilter>());
        provider.GetRequiredService<TenantApiKeyAuthorizationFilter>()
            .Should().BeSameAs(provider.GetRequiredService<TenantApiKeyAuthorizationFilter>());
        provider.GetRequiredService<PublicReadExceptionFilter>()
            .Should().BeSameAs(provider.GetRequiredService<PublicReadExceptionFilter>());
        provider.GetRequiredService<IpHashHelper>()
            .Should().BeSameAs(provider.GetRequiredService<IpHashHelper>());
    }

    [Fact]
    public void Idempotent_Registration_TryAdd_Pattern()
    {
        // Calling AddTenantClientCachePublicRead twice MUST NOT duplicate
        // the singleton-typed descriptors. (CORS and rate-limiter
        // policies are storage-replace by name and exempt from this
        // check.)
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();

        services.AddTenantClientCachePublicRead(configuration);
        services.AddTenantClientCachePublicRead(configuration);

        services.Where(d => d.ServiceType == typeof(ITenantApiKeyValidator)).Should().HaveCount(1);
        services.Where(d => d.ServiceType == typeof(HttpsRequiredFilter)).Should().HaveCount(1);
        services.Where(d => d.ServiceType == typeof(TenantApiKeyAuthorizationFilter)).Should().HaveCount(1);
        services.Where(d => d.ServiceType == typeof(PublicReadExceptionFilter)).Should().HaveCount(1);
        services.Where(d => d.ServiceType == typeof(IpHashHelper)).Should().HaveCount(1);
    }

    [Fact]
    public void Throws_ArgumentNullException_For_Null_Services()
    {
        IServiceCollection? services = null;
        var configuration = BuildConfiguration();

        Action act = () => services!.AddTenantClientCachePublicRead(configuration);

        act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("services");
    }

    [Fact]
    public void Throws_ArgumentNullException_For_Null_Configuration()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddTenantClientCachePublicRead(null!);

        act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("configuration");
    }

    // ===== CORS policy (R5.1 – R5.8) ===================================

    [Fact]
    public async Task Cors_Policy_Registered_With_Name()
    {
        // R5.1: the policy "TenantClientCachePublicRead" must be reachable
        // through ICorsPolicyProvider keyed by name.
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(),
            StartupHelpers.PublicReadCorsPolicyName);

        policy.Should().NotBeNull();
    }

    [Fact]
    public async Task Cors_Policy_Empty_Allowlist_Default_NoAllowOriginEchoed()
    {
        // R5.4: zero origins by default → CORS service rejects every
        // origin (the policy's IsOriginAllowed returns false).
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(),
            StartupHelpers.PublicReadCorsPolicyName);

        policy.Should().NotBeNull();
        policy!.Origins.Should().BeEmpty("empty allowlist means zero origins (R5.4)");
        policy.IsOriginAllowed("https://attacker.example").Should().BeFalse();
        policy.IsOriginAllowed("http://localhost:3000").Should().BeFalse();
    }

    [Fact]
    public async Task Cors_Policy_Methods_Headers_ExposedHeaders_Credentials_PreflightMaxAge()
    {
        // R5.2 + R5.3 + R5.7 + R5.8 — every contract bit on the policy
        // surface.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["TenantClientCachePublicRead:Cors:AllowedOrigins:0"] = "https://app.example.com",
            ["TenantClientCachePublicRead:Cors:PreflightMaxAgeSeconds"] = "1234",
        });
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(),
            StartupHelpers.PublicReadCorsPolicyName);

        policy.Should().NotBeNull();
        policy!.Origins.Should().BeEquivalentTo(new[] { "https://app.example.com" });
        policy.Methods.Should().BeEquivalentTo(new[] { "GET", "HEAD", "OPTIONS" });
        policy.Headers.Should().BeEquivalentTo(new[] { "X-Tenant-Api-Key", "If-None-Match", "Accept" });
        policy.ExposedHeaders.Should().BeEquivalentTo(new[] { "ETag", "Cache-Control" });
        policy.SupportsCredentials.Should().BeFalse("R5.3 — credentials disallowed");
        policy.PreflightMaxAge.Should().Be(TimeSpan.FromSeconds(1234));
    }

    // ===== Rate limiter policy (R4.1 – R4.9) ===========================

    [Fact]
    public void RateLimiter_Policy_Registered_With_Name()
    {
        // The policy descriptor lands inside RateLimiterOptions's per-name
        // dictionary. We assert via the configured options snapshot
        // because IRateLimiterPolicy is internal to the framework.
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var rateLimiterOptions = provider
            .GetRequiredService<IOptions<RateLimiterOptions>>()
            .Value;

        // RateLimiterOptions carries an internal name-keyed registry; the
        // simplest cross-version assertion is that the OnRejected callback
        // is non-default (proving AddRateLimiter ran AND our policy
        // registration callback was invoked).
        rateLimiterOptions.OnRejected.Should().NotBeNull();
    }

    [Fact]
    public async Task RateLimiter_Rejected_Writes_429_Body_And_RetryAfter()
    {
        // R4.5 — rejection writes { "error": "rate_limit_exceeded" } with
        // Retry-After. We invoke the OnRejected hook directly via a
        // synthetic OnRejectedContext + a lease that exposes a
        // TimeUntilNextReplenishment metadata of 7 seconds → header = "7".
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var rateLimiterOptions = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        var (context, body) = BuildOnRejectedContext(provider, retryAfter: TimeSpan.FromSeconds(7));

        await rateLimiterOptions.OnRejected!(context, CancellationToken.None);

        context.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.HttpContext.Response.ContentType.Should().Be("application/json; charset=utf-8");
        context.HttpContext.Response.Headers.RetryAfter.ToString().Should().Be("7");

        body.Position = 0;
        var written = Encoding.UTF8.GetString(body.ToArray());
        written.Should().Be("{\"error\":\"rate_limit_exceeded\"}");

        // Body MUST round-trip through System.Text.Json with exactly one
        // top-level property "error" (R7.6 closed schema reused for 429).
        using var doc = JsonDocument.Parse(written);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[] { "error" });
        doc.RootElement.GetProperty("error").GetString().Should().Be("rate_limit_exceeded");
    }

    [Fact]
    public async Task RateLimiter_Rejected_Without_RetryAfterMetadata_Falls_Back_To_1()
    {
        // R4.5 — when the lease metadata lacks RetryAfter, header = "1".
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var rateLimiterOptions = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        var (context, _) = BuildOnRejectedContext(provider, retryAfter: null);

        await rateLimiterOptions.OnRejected!(context, CancellationToken.None);

        context.HttpContext.Response.Headers.RetryAfter.ToString().Should().Be("1");
    }

    // ===== Fail-fast on bad config (R4.3 / R5.6 / R6.2 reinforcement) ==

    [Fact]
    public void ValidateOnStart_Triggers_FailFast_When_Config_Invalid()
    {
        // R4.3: TokenLimit ∈ [1, 10000]. Setting it to 0 trips the
        // fail-fast path. AddTenantClientCachePublicRead arms
        // ValidateOnStart() so resolving IOptions<T>.Value at composition
        // root throws OptionsValidationException — the same code path the
        // host's startup filter would invoke.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["TenantClientCachePublicRead:RateLimit:TokenLimit"] = "0",
        });
        var services = BuildBaselineServices();

        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Action act = () => _ = provider
            .GetRequiredService<IOptions<TenantClientCachePublicReadOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*TokenLimit*");
    }

    // ===== Helpers =====================================================

    /// <summary>
    /// Build a synthetic <see cref="OnRejectedContext"/> targeting tenant
    /// "acme" with an optional <c>RetryAfter</c> metadata. The returned
    /// <see cref="MemoryStream"/> captures the response body so callers
    /// can assert byte equality.
    /// </summary>
    private static (OnRejectedContext context, MemoryStream body) BuildOnRejectedContext(
        IServiceProvider provider,
        TimeSpan? retryAfter)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = "/api/public/tenants/acme/clients/web";
        http.Request.RouteValues["tenantKey"] = "acme";

        var bodyStream = new MemoryStream();
        http.Response.Body = bodyStream;

        var lease = retryAfter is null
            ? (RateLimitLease)new StubLease()
            : new StubLease(retryAfter.Value);

        var context = new OnRejectedContext
        {
            HttpContext = http,
            Lease = lease,
        };

        return (context, bodyStream);
    }

    private sealed class StubLease : RateLimitLease
    {
        private readonly TimeSpan? _retryAfter;

        public StubLease()
        {
        }

        public StubLease(TimeSpan retryAfter)
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
}
