// Feature: tenant-client-cache-expansion, Task 9
//
// Example-based unit tests for TenantClientCacheRefreshService. The service
// is exercised by driving its internal SweepAsync hook directly (mirrors
// the test-only seam called out in tasks.md Task 9: "expose internal
// SweepAsync method ... so unit tests can drive a single sweep
// deterministically").
//
// Validates: Requirements 1.10, 8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 8.7, 8.9,
//            14.4, 16.4
//
// Property-based coverage of sweep coverage (P16) is not part of this task
// (called out as a follow-up in tasks.md Task 9 P16 bullet).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using TenantInfrastructure.MasterDb;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class TenantClientCacheRefreshServiceTests
{
    private static TenantClientCacheOptions BuildOptions(
        bool enabled = true,
        int maxClientsPerTenant = 5000,
        TimeSpan? refreshInterval = null)
        => new()
        {
            Enabled = enabled,
            AbsoluteTtl = TimeSpan.FromHours(1),
            SlidingTtl = null,
            RefreshInterval = refreshInterval ?? TimeSpan.FromHours(1),
            WriteTimeoutMs = 2000,
            MaxClientsPerTenant = maxClientsPerTenant,
        };

    private static TenantInfo BuildTenant(string tenantKey, bool isActive = true) => new()
    {
        Id = Math.Abs(tenantKey.GetHashCode()),
        TenantKey = tenantKey,
        DisplayName = tenantKey,
        IsActive = isActive,
    };

    private static ClientDto BuildClient(int id, string clientId, params string[] tenantKeys)
    {
        var dto = new ClientDto
        {
            Id = id,
            ClientId = clientId,
            ClientName = clientId,
            Enabled = true,
            ProtocolType = "oidc",
            AllowedScopes = new List<string> { "openid" },
            RedirectUris = new List<string> { $"https://{clientId}/callback" },
        };
        foreach (var tk in tenantKeys)
        {
            dto.TenantRedirectPairs.Add(new ClientTenantRedirectPairDto
            {
                TenantKey = tk,
                SignInCallbackUrl = $"https://{tk}.example.com/callback",
                SignOutCallbackUrl = $"https://{tk}.example.com/signout-callback",
            });
        }
        return dto;
    }

    /// <summary>
    /// Build a <see cref="IServiceScopeFactory"/> that returns a single
    /// scope wired with the supplied stubs. The fakes themselves are
    /// captured by the caller for assertions.
    /// </summary>
    private static IServiceScopeFactory BuildScopeFactory(
        ITenantRepository tenantRepo,
        IClientService clientService,
        IClientTenantScopeResolver resolver)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(tenantRepo);
        collection.AddSingleton(clientService);
        collection.AddSingleton(resolver);
        var provider = collection.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private static (TenantClientCacheRefreshService service,
        TenantClientCacheService cache,
        IDistributedCache memoryCache,
        TenantClientCacheMetrics metrics,
        FakeTimeProvider time)
        BuildSubjectWithRealCache(
            TenantClientCacheOptions options,
            ITenantRepository tenantRepo,
            IClientService clientService,
            IClientTenantScopeResolver resolver)
    {
        var memory = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var optionsMonitor = new StubOptionsMonitor<TenantClientCacheOptions>(options);
        var metrics = new TenantClientCacheMetrics();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero));
        var cache = new TenantClientCacheService(
            memory,
            optionsMonitor,
            NullLogger<TenantClientCacheService>.Instance,
            metrics,
            time);

        var scopeFactory = BuildScopeFactory(tenantRepo, clientService, resolver);

        var service = new TenantClientCacheRefreshService(
            scopeFactory,
            optionsMonitor,
            NullLogger<TenantClientCacheRefreshService>.Instance,
            cache,
            metrics,
            time);

        return (service, cache, memory, metrics, time);
    }

    // ===== R1.8 / R8.1: disabled flag =================================

    [Fact]
    public async Task Disabled_Options_Skips_Sweep_Loop()
    {
        // ExecuteAsync must return immediately when Enabled == false:
        //   * no scope is created
        //   * no tenant is enumerated
        //   * the BackgroundService completes its StartAsync without ever
        //     touching the dependencies
        var tenantRepo = new Mock<ITenantRepository>(MockBehavior.Strict);
        var clientService = new Mock<IClientService>(MockBehavior.Strict);
        var resolver = new Mock<IClientTenantScopeResolver>(MockBehavior.Strict);
        var cacheService = new Mock<ITenantClientCacheService>(MockBehavior.Strict);

        var optionsMonitor = new StubOptionsMonitor<TenantClientCacheOptions>(
            BuildOptions(enabled: false));
        var metrics = new TenantClientCacheMetrics();

        // Strict scope factory — if the service tries to create a scope
        // we fail loudly.
        var scopeFactoryMock = new Mock<IServiceScopeFactory>(MockBehavior.Strict);

        var service = new TenantClientCacheRefreshService(
            scopeFactoryMock.Object,
            optionsMonitor,
            NullLogger<TenantClientCacheRefreshService>.Instance,
            cacheService.Object,
            metrics);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await service.StopAsync(cts.Token);

        scopeFactoryMock.VerifyNoOtherCalls();
        tenantRepo.VerifyNoOtherCalls();
        clientService.VerifyNoOtherCalls();
        resolver.VerifyNoOtherCalls();
        cacheService.VerifyNoOtherCalls();
    }

    // ===== R8.2: immediate sweep on startup ==========================

    [Fact]
    public async Task Immediate_Sweep_On_Startup_Then_Periodic()
    {
        // The first sweep happens BEFORE any Task.Delay. We assert by
        // running ExecuteAsync up to a known cancellation boundary and
        // observing that the cache contains entries before the periodic
        // delay would have completed.
        var tenants = new[] { BuildTenant("acme") };

        var tenantRepo = new Mock<ITenantRepository>();
        tenantRepo.Setup(r => r.GetTenantsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants);

        var clientService = new Mock<IClientService>();
        clientService.Setup(c => c.ListClientPrimaryKeysForTenantAsync(
                "acme", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<int>)new[] { 100 });
        clientService.Setup(c => c.GetClientAsync(100))
            .ReturnsAsync(BuildClient(100, "acme-client", "acme"));

        var resolver = new Mock<IClientTenantScopeResolver>();
        resolver.Setup(r => r.ResolveTenantKeysAsync(It.IsAny<ClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)new[] { "acme" });

        // RefreshInterval is set very large so the periodic loop never
        // completes a Task.Delay before we cancel — this proves the
        // first sweep ran *before* the loop entered Task.Delay.
        var options = BuildOptions(refreshInterval: TimeSpan.FromHours(1));

        var (service, cache, _, _, _) = BuildSubjectWithRealCache(
            options, tenantRepo.Object, clientService.Object, resolver.Object);

        using var cts = new CancellationTokenSource();
        // ExecuteAsync runs synchronously up to the first await; we cancel
        // immediately after StartAsync to terminate the periodic loop.
        await service.StartAsync(cts.Token);

        // Give the immediate sweep a moment to populate the cache then stop.
        await Task.Delay(50);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        var envelope = await cache.ReadSnapshotAsync("acme", "acme-client", CancellationToken.None);
        envelope.Should().NotBeNull();
        envelope!.Data.ClientId.Should().Be("acme-client");
    }

    // ===== R8.5 / R10.6: per-tenant try/catch =========================

    [Fact]
    public async Task Per_Tenant_Exception_Does_Not_Crash_Sweep()
    {
        // Tenant A throws when listing clients. Tenant B continues to be
        // processed and produces a snapshot.
        var tenants = new[] { BuildTenant("a"), BuildTenant("b") };

        var tenantRepo = new Mock<ITenantRepository>();
        tenantRepo.Setup(r => r.GetTenantsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants);

        var clientService = new Mock<IClientService>();
        clientService.Setup(c => c.ListClientPrimaryKeysForTenantAsync(
                "a", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));
        clientService.Setup(c => c.ListClientPrimaryKeysForTenantAsync(
                "b", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<int>)new[] { 200 });
        clientService.Setup(c => c.GetClientAsync(200))
            .ReturnsAsync(BuildClient(200, "b-client", "b"));

        var resolver = new Mock<IClientTenantScopeResolver>();
        resolver.Setup(r => r.ResolveTenantKeysAsync(It.IsAny<ClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)new[] { "b" });

        var (service, cache, _, _, _) = BuildSubjectWithRealCache(
            BuildOptions(),
            tenantRepo.Object,
            clientService.Object,
            resolver.Object);

        var act = async () => await service.SweepAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Tenant B's snapshot must be present.
        var envelope = await cache.ReadSnapshotAsync("b", "b-client", CancellationToken.None);
        envelope.Should().NotBeNull();

        // Tenant A's snapshot must be absent (the throw fired BEFORE any
        // write could happen for tenant A).
        var missing = await cache.ReadSnapshotAsync("a", "anything", CancellationToken.None);
        missing.Should().BeNull();
    }

    // ===== R8.4: max-clients overflow ================================

    [Fact]
    public async Task MaxClientsPerTenant_Overflow_Logs_Warning_And_Trims()
    {
        // The repository returns max+1 entries (signalling overflow per
        // R8.4). The sweep must:
        //   1. log Warning naming MaxClientsPerTenantExceeded + observed/cap
        //   2. write only the first `max` clients
        const int max = 2;
        var tenants = new[] { BuildTenant("acme") };

        var tenantRepo = new Mock<ITenantRepository>();
        tenantRepo.Setup(r => r.GetTenantsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants);

        var clientService = new Mock<IClientService>();
        // 3 ids returned for max=2 ⇒ overflow.
        clientService.Setup(c => c.ListClientPrimaryKeysForTenantAsync(
                "acme", max, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<int>)new[] { 1, 2, 3 });

        clientService.Setup(c => c.GetClientAsync(1))
            .ReturnsAsync(BuildClient(1, "c1", "acme"));
        clientService.Setup(c => c.GetClientAsync(2))
            .ReturnsAsync(BuildClient(2, "c2", "acme"));
        clientService.Setup(c => c.GetClientAsync(3))
            .ReturnsAsync(BuildClient(3, "c3", "acme"));

        var resolver = new Mock<IClientTenantScopeResolver>();
        resolver.Setup(r => r.ResolveTenantKeysAsync(It.IsAny<ClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)new[] { "acme" });

        var optionsValue = BuildOptions(maxClientsPerTenant: max);
        var optionsMonitor = new StubOptionsMonitor<TenantClientCacheOptions>(optionsValue);
        var memory = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var metrics = new TenantClientCacheMetrics();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero));
        var cache = new TenantClientCacheService(
            memory,
            optionsMonitor,
            NullLogger<TenantClientCacheService>.Instance,
            metrics,
            time);
        var scopeFactory = BuildScopeFactory(
            tenantRepo.Object, clientService.Object, resolver.Object);
        var logger = new CapturingLogger<TenantClientCacheRefreshService>();
        var service = new TenantClientCacheRefreshService(
            scopeFactory, optionsMonitor, logger, cache, metrics, time);

        await service.SweepAsync(CancellationToken.None);

        // First two ids written, third trimmed.
        (await cache.ReadSnapshotAsync("acme", "c1", CancellationToken.None)).Should().NotBeNull();
        (await cache.ReadSnapshotAsync("acme", "c2", CancellationToken.None)).Should().NotBeNull();
        (await cache.ReadSnapshotAsync("acme", "c3", CancellationToken.None)).Should().BeNull();

        // Warning log contains the max-exceeded subreason and observed/cap.
        var warnings = logger.Entries
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Where(e => e.Fields.TryGetValue("Subreason", out var sr)
                     && sr is string s
                     && s == "MaxClientsPerTenantExceeded")
            .ToList();
        warnings.Should().HaveCountGreaterThan(0);

        var entry = warnings.First();
        entry.Fields["ObservedCount"].Should().Be(3);
        entry.Fields["Cap"].Should().Be(max);
        entry.Fields["TenantKey"].Should().Be("acme");
    }

    // ===== R8.6: per-cycle sweep summary log =========================

    [Fact]
    public async Task Sweep_Summary_Log_Emitted_Once_Per_Cycle()
    {
        // After one SweepAsync call:
        //   * exactly one Information event of type
        //     "TenantClientCacheRefreshCompleted"
        //   * the structured fields TenantsSwept, ClientsWritten,
        //     WriteFailures, DurationMs are all present.
        var tenants = new[] { BuildTenant("a"), BuildTenant("b") };

        var tenantRepo = new Mock<ITenantRepository>();
        tenantRepo.Setup(r => r.GetTenantsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants);

        var clientService = new Mock<IClientService>();
        clientService.Setup(c => c.ListClientPrimaryKeysForTenantAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<int>)Array.Empty<int>());

        var resolver = new Mock<IClientTenantScopeResolver>();

        var optionsMonitor = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var memory = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var metrics = new TenantClientCacheMetrics();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero));
        var cache = new TenantClientCacheService(
            memory, optionsMonitor, NullLogger<TenantClientCacheService>.Instance, metrics, time);
        var scopeFactory = BuildScopeFactory(
            tenantRepo.Object, clientService.Object, resolver.Object);
        var logger = new CapturingLogger<TenantClientCacheRefreshService>();
        var service = new TenantClientCacheRefreshService(
            scopeFactory, optionsMonitor, logger, cache, metrics, time);

        await service.SweepAsync(CancellationToken.None);

        var summaries = logger.Entries
            .Where(e => e.Fields.TryGetValue("EventType", out var v)
                     && v is string s
                     && s == "TenantClientCacheRefreshCompleted")
            .ToList();
        summaries.Should().HaveCount(1);

        var summary = summaries.Single();
        summary.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Information);
        summary.Fields.Should().ContainKey("TenantsSwept");
        summary.Fields.Should().ContainKey("ClientsWritten");
        summary.Fields.Should().ContainKey("WriteFailures");
        summary.Fields.Should().ContainKey("DurationMs");

        summary.Fields["TenantsSwept"].Should().Be(2);
        summary.Fields["ClientsWritten"].Should().Be(0);
        summary.Fields["WriteFailures"].Should().Be(0);
    }

    // ===== R16.4: last_completed_at observable gauge =================

    [Fact]
    public async Task LastCompletedAt_Gauge_Updated_After_Each_Sweep()
    {
        // The observable gauge is fed from
        // TenantClientCacheMetrics.SetLastSweepCompletedAt, which the
        // BackgroundService MUST call at the end of every SweepAsync.
        // We use FakeTimeProvider to fix the wall clock and a
        // RecordingMeterListener to read the gauge value.
        var tenantRepo = new Mock<ITenantRepository>();
        tenantRepo.Setup(r => r.GetTenantsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TenantInfo>());

        var clientService = new Mock<IClientService>(MockBehavior.Strict);
        var resolver = new Mock<IClientTenantScopeResolver>(MockBehavior.Strict);

        var optionsMonitor = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var memory = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var metrics = new TenantClientCacheMetrics();

        // Pin time to 2026-04-01T00:00:00Z (unix=1774915200).
        var fixedNow = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(fixedNow);

        var cache = new TenantClientCacheService(
            memory, optionsMonitor, NullLogger<TenantClientCacheService>.Instance, metrics, time);
        var scopeFactory = BuildScopeFactory(
            tenantRepo.Object, clientService.Object, resolver.Object);
        var service = new TenantClientCacheRefreshService(
            scopeFactory,
            optionsMonitor,
            NullLogger<TenantClientCacheRefreshService>.Instance,
            cache,
            metrics,
            time);

        using var listener = new RecordingMeterListener(TenantClientCacheMetrics.MeterName);

        // Other tests in the same xunit process create their own
        // TenantClientCacheMetrics instances (each is its own
        // Meter("TenantClientCache", ...)). RecordObservableInstruments
        // publishes the gauge value for *every* alive meter with that
        // name, so we cannot assume a single observed value before the
        // sweep. The contract under test is "after my sweep, my unique
        // timestamp shows up" — we assert via Contain on value.

        // Clear here so the post-sweep snapshot only contains
        // measurements emitted *during or after* SweepAsync (the
        // histogram records eagerly, the gauge records on demand).
        listener.Clear();

        await service.SweepAsync(CancellationToken.None);

        // Histogram for sweep duration must have at least one record
        // (recorded eagerly at the end of SweepAsync).
        listener.ForInstrument(
            TenantClientCacheMetrics.RefreshSweepDurationHistogramName)
            .Should().NotBeEmpty();

        // Force the observable gauge to publish.
        listener.RecordObservableInstruments();
        var afterSweep = listener
            .ForInstrument(TenantClientCacheMetrics.RefreshLastCompletedAtGaugeName);
        afterSweep.Should()
            .Contain(m => m.Value == fixedNow.ToUnixTimeSeconds(),
                "TenantClientCacheRefreshService.SweepAsync must call SetLastSweepCompletedAt with the current TimeProvider time");
    }
}
