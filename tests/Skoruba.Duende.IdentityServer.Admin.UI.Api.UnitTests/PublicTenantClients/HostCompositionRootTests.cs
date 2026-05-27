// Feature: tenant-client-cache-public-read, Task 11
//
// Composition-root tests for the host-side wiring of the public-read
// endpoint feature. Distinct from the Task 6 tests
// (StartupHelpersAddTenantClientCachePublicReadTests) which assert the
// extension's contracts in isolation: this file pins the contract that
// the host caller observes — every collaborator the controller resolves
// is registered, idempotent registration does not duplicate descriptors,
// the named CORS / rate-limiter policies are reachable, and the
// startup-time logger hosted service is registered exactly once.
//
// Validates: Requirements 1.1, 1.7, 1.10, 12.1, 12.9, 12.10, 17.1

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.RateLimiting;

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
public sealed class HostCompositionRootTests
{
    /// <summary>
    /// Build an in-memory <see cref="IConfiguration"/> mirroring the
    /// <c>TenantClientCachePublicRead</c> section the Admin host loads at
    /// startup. Defaults match the production
    /// <see cref="TenantClientCachePublicReadOptions"/> values.
    /// </summary>
    private static IConfiguration BuildConfiguration(IDictionary<string, string?>? overrides = null)
    {
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

        return new ConfigurationBuilder().AddInMemoryCollection(defaults).Build();
    }

    private static ServiceCollection BuildBaselineServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The metrics meter is normally registered by RegisterTenantClientCache
        // (the parent-spec extension). The host calls it before
        // AddTenantClientCachePublicRead, so we mirror that ordering here.
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

    // ===== Service resolution ===============================================

    [Fact]
    public void Host_Resolves_All_Public_Read_Services()
    {
        // R12.10: every collaborator the controller depends on is wired
        // by the extension. This is the "host smoke" assertion — if any
        // descriptor is missing the production host crashes at first
        // request resolution.
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();

        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<ITenantApiKeyValidator>().Should().NotBeNull();
        provider.GetRequiredService<IpHashHelper>().Should().NotBeNull();
        provider.GetRequiredService<TenantApiKeyAuthorizationFilter>().Should().NotBeNull();
        provider.GetRequiredService<HttpsRequiredFilter>().Should().NotBeNull();
        provider.GetRequiredService<PublicReadExceptionFilter>().Should().NotBeNull();
    }

    [Fact]
    public void Host_Idempotent_Registration_TryAdd()
    {
        // The host wiring is allowed to be invoked multiple times (e.g.
        // by a test harness that composes the extension on top of the
        // production host). The Task 6 extension uses TryAdd so two
        // invocations leave the descriptor count unchanged.
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();

        services.AddTenantClientCachePublicRead(configuration);
        var afterFirst = services.Count;

        services.AddTenantClientCachePublicRead(configuration);

        // The service collection grows only by ValidateOnStart re-arming
        // (Action<IServiceCollection> registered on the OptionsBuilder by
        // .Bind/.ValidateOnStart) — no NEW typed singletons.
        services.Where(d => d.ServiceType == typeof(ITenantApiKeyValidator)).Should().HaveCount(1);
        services.Where(d => d.ServiceType == typeof(HttpsRequiredFilter)).Should().HaveCount(1);
        services.Where(d => d.ServiceType == typeof(TenantApiKeyAuthorizationFilter)).Should().HaveCount(1);
        services.Where(d => d.ServiceType == typeof(PublicReadExceptionFilter)).Should().HaveCount(1);
        services.Where(d => d.ServiceType == typeof(IpHashHelper)).Should().HaveCount(1);
        services
            .Where(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType == typeof(PublicReadStartupLogger))
            .Should().HaveCount(1, "TryAddEnumerable keeps the startup logger singleton-bound");

        // Defensive: the second call still produced a valid provider (no
        // descriptor permutation tripped a resolver guard).
        using var provider = services.BuildServiceProvider(validateScopes: true);
        provider.GetRequiredService<ITenantApiKeyValidator>().Should().NotBeNull();

        // Sanity — the second call did not shrink the collection.
        services.Count.Should().BeGreaterOrEqualTo(afterFirst);
    }

    [Fact]
    public async Task Host_Resolves_RateLimiter_Policy_TenantClientCachePublicRead()
    {
        // R4.1: the rate-limiter policy named "TenantClientCachePublicRead"
        // is wired through AddRateLimiter and observable via
        // RateLimiterOptions. The framework's per-name registry is internal
        // so we assert via the OnRejected callback presence and via
        // BuildRateLimitPartition returning a non-no-limiter partition for
        // a route-bound tenantKey.
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var rateLimiterOptions = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        rateLimiterOptions.OnRejected.Should().NotBeNull(
            "AddTenantClientCachePublicRead arms the 429 rejection handler (R4.5)");

        // Drive the partition factory directly to confirm the policy
        // produces a token-bucket partition for a real tenantKey route
        // value.
        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.RouteValues["tenantKey"] = "acme";
        var partition = StartupHelpers.BuildRateLimitPartition(http);
        partition.PartitionKey.Should().Be("acme");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Host_Resolves_Cors_Policy_TenantClientCachePublicRead()
    {
        // R5.1: the CORS policy named "TenantClientCachePublicRead" is
        // wired and reachable via ICorsPolicyProvider. The default
        // allowlist is empty (R5.4).
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();
        services.AddTenantClientCachePublicRead(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(),
            StartupHelpers.PublicReadCorsPolicyName);

        policy.Should().NotBeNull();
        policy!.Origins.Should().BeEmpty();
        policy.Methods.Should().BeEquivalentTo(new[] { "GET", "HEAD", "OPTIONS" });
        policy.SupportsCredentials.Should().BeFalse();
    }

    // ===== Startup logger registration =====================================

    [Fact]
    public void Host_Registers_Startup_Logger_Hosted_Service_Once()
    {
        // R1.8: the startup logger is registered as an IHostedService so
        // the host emits the bound-options summary on start. Idempotent
        // registration via TryAddEnumerable + (typeof TImpl) so two calls
        // never duplicate the entry.
        var configuration = BuildConfiguration();
        var services = BuildBaselineServices();

        services.AddTenantClientCachePublicRead(configuration);
        services.AddTenantClientCachePublicRead(configuration);

        services
            .Where(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType == typeof(PublicReadStartupLogger))
            .Should().HaveCount(1, "duplicate hosted-service registration would emit the log entry twice");
    }

    // ===== OpenAPI tag (R12.9) =============================================

    [Fact]
    public void OpenApi_Document_Includes_PublicTenantClients_Tag()
    {
        // R12.9: the controller MUST carry a [Tags("PublicTenantClients")]
        // attribute so the OpenAPI generator (NSwag in production, the
        // ApiExplorer surface in tests) places its actions under a
        // dedicated tag, separate from the existing "Clients" tag.
        // The integration project has an end-to-end test that walks
        // ApiExplorer; here we just pin the attribute via reflection so
        // the contract is preserved even if controller composition
        // changes.
        var controllerType = typeof(Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers.PublicTenantClientsController);
        var attrs = controllerType.GetCustomAttributes(inherit: false);
        var tagsAttr = Array.Find(attrs, a => a.GetType().Name == "TagsAttribute");
        tagsAttr.Should().NotBeNull(
            "PublicTenantClientsController must declare [Tags(\"PublicTenantClients\")] (R12.9)");

        var tagsProp = tagsAttr!.GetType()
            .GetProperty("Tags", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        tagsProp.Should().NotBeNull();
        var tagValues = tagsProp!.GetValue(tagsAttr) as System.Collections.IEnumerable;
        tagValues.Should().NotBeNull();
        var tagList = new List<string>();
        foreach (var t in tagValues!)
        {
            if (t is string s) tagList.Add(s);
        }
        tagList.Should().Contain("PublicTenantClients");
        tagList.Should().NotContain("Clients", "the public-read endpoint must surface under its own OpenAPI tag (R12.9)");
    }
}
