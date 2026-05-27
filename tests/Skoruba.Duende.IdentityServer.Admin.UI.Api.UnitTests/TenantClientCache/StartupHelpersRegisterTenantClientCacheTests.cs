// Feature: tenant-client-cache-expansion, Task 11
//
// Composition-root tests for the StartupHelpers.RegisterTenantClientCache
// extension. The tests build a minimal ServiceCollection (with the
// non-feature dependencies the cache services need — IDistributedCache,
// ILoggerFactory, IClientService) and assert the wiring contract:
//
//   * Options binding pulls values from the IConfiguration section.
//   * Singletons (TenantClientCacheMetrics, ITenantClientCacheService).
//   * Scoped resolver (IClientTenantScopeResolver) is reachable from a
//     scope and is a different instance per scope.
//   * The hosted refresh service is registered exactly once when
//     Enabled=true and zero times when Enabled=false.
//   * ValidateOnStart() fail-fasts when the bound options are out of range.
//
// Validates: Requirements 1.1, 1.7, 1.8, 1.10, 8.1, 17.1
//
// Property-based coverage is not part of this task (wiring only — tied
// behaviours covered by P10–P16 in earlier tasks).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using FluentAssertions;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class StartupHelpersRegisterTenantClientCacheTests
{
    /// <summary>
    /// Build an in-memory IConfiguration that mirrors a real
    /// <c>appsettings.json</c> "TenantClientCache" section.
    /// </summary>
    private static IConfiguration BuildConfiguration(IDictionary<string, string?> overrides)
    {
        // Defaults match TenantClientCacheOptions (R1.2). Tests can pass
        // overrides with dotted-keys (e.g. "TenantClientCache:Enabled" =>
        // "false") to flip individual settings.
        var defaults = new Dictionary<string, string?>
        {
            ["TenantClientCache:Enabled"] = "true",
            ["TenantClientCache:AbsoluteTtl"] = "01:00:00",
            ["TenantClientCache:SlidingTtl"] = null,
            ["TenantClientCache:RefreshInterval"] = "01:00:00",
            ["TenantClientCache:WriteTimeoutMs"] = "2000",
            ["TenantClientCache:MaxClientsPerTenant"] = "5000",
        };
        foreach (var kv in overrides)
        {
            defaults[kv.Key] = kv.Value;
        }
        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaults)
            .Build();
    }

    /// <summary>
    /// Build a <see cref="ServiceCollection"/> pre-populated with the
    /// non-feature dependencies the cache services consume.
    /// </summary>
    /// <remarks>
    /// We intentionally do NOT call <c>AddTenantInfrastructure</c> — that
    /// extension demands a real master-DB connection string and is not
    /// the unit under test. The minimal stand-ins below give the cache
    /// services everything they need to resolve.
    /// </remarks>
    private static ServiceCollection BuildBaselineServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The cache service needs an IDistributedCache; Memory is fine
        // for composition-root tests.
        services.AddSingleton<IDistributedCache>(_ =>
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

        // ClientTenantScopeResolver depends on IClientService at scope
        // creation time. A strict mock that accepts any call (we never
        // invoke a method during these wiring tests) is sufficient.
        services.AddScoped(_ => new Mock<IClientService>().Object);

        return services;
    }

    // ===== Options binding ============================================

    [Fact]
    public void RegisterTenantClientCache_Binds_Options_From_Configuration()
    {
        // Arrange — non-default values to prove the bind actually reaches
        // through.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["TenantClientCache:AbsoluteTtl"] = "02:00:00",
            ["TenantClientCache:RefreshInterval"] = "00:30:00",
            ["TenantClientCache:WriteTimeoutMs"] = "1500",
            ["TenantClientCache:MaxClientsPerTenant"] = "1234",
        });

        var services = BuildBaselineServices();

        // Act
        services.RegisterTenantClientCache(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        // Assert
        var bound = provider.GetRequiredService<IOptions<TenantClientCacheOptions>>().Value;
        bound.Enabled.Should().BeTrue();
        bound.AbsoluteTtl.Should().Be(TimeSpan.FromHours(2));
        bound.RefreshInterval.Should().Be(TimeSpan.FromMinutes(30));
        bound.WriteTimeoutMs.Should().Be(1500);
        bound.MaxClientsPerTenant.Should().Be(1234);
    }

    // ===== Service registration =======================================

    [Fact]
    public void RegisterTenantClientCache_Registers_ITenantClientCacheService_Singleton()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        var services = BuildBaselineServices();

        services.RegisterTenantClientCache(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var first = provider.GetRequiredService<ITenantClientCacheService>();
        var second = provider.GetRequiredService<ITenantClientCacheService>();

        first.Should().NotBeNull();
        second.Should().BeSameAs(first, "ITenantClientCacheService is singleton");
    }

    [Fact]
    public void RegisterTenantClientCache_Registers_TenantClientCacheMetrics_Singleton()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        var services = BuildBaselineServices();

        services.RegisterTenantClientCache(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var first = provider.GetRequiredService<TenantClientCacheMetrics>();
        var second = provider.GetRequiredService<TenantClientCacheMetrics>();

        first.Should().NotBeNull();
        second.Should().BeSameAs(first, "TenantClientCacheMetrics is singleton");
    }

    [Fact]
    public void RegisterTenantClientCache_Registers_IClientTenantScopeResolver_Scoped()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        var services = BuildBaselineServices();

        services.RegisterTenantClientCache(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        // The resolver is Scoped — must be resolved from a scope, not the
        // root provider. ServiceProvider.validateScopes=true would throw
        // if we tried to resolve a scoped service from root, so the scope
        // gymnastics here also encode the lifetime contract.
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var resolver1 = scope1.ServiceProvider.GetRequiredService<IClientTenantScopeResolver>();
        var resolver2 = scope2.ServiceProvider.GetRequiredService<IClientTenantScopeResolver>();

        resolver1.Should().NotBeNull();
        resolver2.Should().NotBeNull();
        resolver1.Should().NotBeSameAs(resolver2, "scoped resolvers differ across scopes");

        var resolver1Again = scope1.ServiceProvider.GetRequiredService<IClientTenantScopeResolver>();
        resolver1Again.Should().BeSameAs(resolver1, "scoped resolver is stable within a scope");
    }

    // ===== Conditional hosted-service registration (R1.8 / R8.1) ======

    [Fact]
    public void RegisterTenantClientCache_Enabled_True_Registers_RefreshService_Once()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["TenantClientCache:Enabled"] = "true",
        });
        var services = BuildBaselineServices();

        services.RegisterTenantClientCache(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        var refreshCount = hostedServices.OfType<TenantClientCacheRefreshService>().Count();

        refreshCount.Should().Be(1, "the refresh service must be registered exactly once when enabled");
    }

    [Fact]
    public void RegisterTenantClientCache_Enabled_False_Does_Not_Register_RefreshService()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["TenantClientCache:Enabled"] = "false",
        });
        var services = BuildBaselineServices();

        services.RegisterTenantClientCache(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        var refreshCount = hostedServices.OfType<TenantClientCacheRefreshService>().Count();

        refreshCount.Should().Be(0, "the refresh service must NOT be registered when disabled (R1.8 / R8.1)");
    }

    // ===== ValidateOnStart fail-fast (R1.3 – R1.6) ====================

    [Fact]
    public void RegisterTenantClientCache_ValidateOnStart_FailsFast_When_AbsoluteTtl_Below_Lower_Bound()
    {
        // R1.3: AbsoluteTtl ∈ [00:05:00, 24:00:00]. Setting it to one
        // minute is firmly below the lower bound and should trigger
        // OptionsValidationException at first IOptions<T> access.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["TenantClientCache:AbsoluteTtl"] = "00:01:00",
        });
        var services = BuildBaselineServices();

        services.RegisterTenantClientCache(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        // ValidateOnStart() is enforced by the host's startup filter; in
        // a unit test we trigger the same code path by resolving
        // IOptions<TenantClientCacheOptions>.Value (which calls every
        // IValidateOptions registered for the type).
        Action act = () => _ = provider.GetRequiredService<IOptions<TenantClientCacheOptions>>().Value;

        act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*TenantClientCache:AbsoluteTtl*")
            .WithMessage("*00:01:00*",
                "the validator MUST name the offending key AND its observed value (R1.3)");
    }

    [Fact]
    public void RegisterTenantClientCache_ValidateOnStart_FailsFast_When_WriteTimeoutMs_Out_Of_Range()
    {
        // R1.6: WriteTimeoutMs ∈ [100, 10000]. 50 is below the lower
        // bound.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["TenantClientCache:WriteTimeoutMs"] = "50",
        });
        var services = BuildBaselineServices();

        services.RegisterTenantClientCache(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Action act = () => _ = provider.GetRequiredService<IOptions<TenantClientCacheOptions>>().Value;

        act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*TenantClientCache:WriteTimeoutMs*")
            .WithMessage("*50*");
    }

    [Fact]
    public void RegisterTenantClientCache_ValidateOnStart_Skips_Range_Checks_When_Disabled()
    {
        // R1.7: when Enabled=false, the cache is a no-op and out-of-range
        // values are tolerated (the validator returns Success). This test
        // documents that the disable switch genuinely suppresses
        // fail-fast — operators flipping the flag in production should
        // not also have to "fix" otherwise-stale config.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["TenantClientCache:Enabled"] = "false",
            ["TenantClientCache:AbsoluteTtl"] = "00:00:30",  // intentionally invalid
            ["TenantClientCache:WriteTimeoutMs"] = "5",       // intentionally invalid
        });
        var services = BuildBaselineServices();

        services.RegisterTenantClientCache(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Action act = () => _ = provider.GetRequiredService<IOptions<TenantClientCacheOptions>>().Value;

        act.Should().NotThrow("disabled cache skips range validation (R1.7)");
    }

    // ===== Caller-contract regressions ================================

    [Fact]
    public void RegisterTenantClientCache_Throws_ArgumentNullException_For_Null_Services()
    {
        IServiceCollection? services = null;
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        Action act = () => services!.RegisterTenantClientCache(configuration);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("services");
    }

    [Fact]
    public void RegisterTenantClientCache_Throws_ArgumentNullException_For_Null_Configuration()
    {
        var services = new ServiceCollection();

        Action act = () => services.RegisterTenantClientCache(null!);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("configuration");
    }

    // ===== Idempotency (calling twice) ================================

    [Fact]
    public void RegisterTenantClientCache_Called_Twice_Does_Not_Duplicate_Singletons()
    {
        // ServiceCollection allows duplicate registrations — but our
        // extension uses TryAdd* which is idempotent. Re-running the
        // wiring should yield the same singleton instance, not stack
        // multiple ITenantClientCacheService registrations.
        //
        // (Hosted services are added unconditionally per call; we only
        // assert the deduped slots here.)
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        var services = BuildBaselineServices();

        services.RegisterTenantClientCache(configuration);
        services.RegisterTenantClientCache(configuration);

        var cacheRegistrations = services
            .Where(d => d.ServiceType == typeof(ITenantClientCacheService))
            .ToList();
        cacheRegistrations.Should().HaveCount(1, "TryAdd singleton is idempotent");
    }
}
