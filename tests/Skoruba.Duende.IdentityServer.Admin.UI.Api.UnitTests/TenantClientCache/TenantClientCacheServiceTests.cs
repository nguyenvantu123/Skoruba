// Feature: tenant-client-cache-expansion, Task 5
//
// Example-based unit tests for TenantClientCacheService. The contract is
// pinned by tasks.md Task 5: round-trip semantics on MemoryDistributedCache,
// IDistributedCache.Strict mock to verify Enabled=false short-circuits, and
// fail-soft behaviour when the underlying cache throws.
//
// Property-based assertions for key format, empty-input rejection,
// idempotent writes, TTL options, and the disabled no-op live in
// TenantClientCacheKeyProperties.cs.
//
// Validates: Requirements 1.7, 1.8, 2.6, 3.1, 3.2, 3.3, 3.4, 3.5, 3.7, 3.8,
//            5.4, 5.5, 6.7, 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7, 10.1, 10.2,
//            10.3, 10.4, 12.4

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class TenantClientCacheServiceTests
{
    private const string SampleTenantKey = "tenant-a";
    private const string SampleClientId = "client-1";

    private static ClientDto BuildClient(string clientId = SampleClientId)
        => new()
        {
            ClientId = clientId,
            ClientName = "Sample",
            Enabled = true,
            ProtocolType = "oidc",
            AllowedScopes = new List<string> { "openid", "profile" },
            RedirectUris = new List<string> { "https://app/callback" },
        };

    private static TenantClientCacheOptions BuildOptions(bool enabled = true)
        => new()
        {
            Enabled = enabled,
            AbsoluteTtl = TimeSpan.FromHours(1),
            SlidingTtl = null,
            RefreshInterval = TimeSpan.FromHours(1),
            WriteTimeoutMs = 2000,
            MaxClientsPerTenant = 5000,
        };

    private static IDistributedCache MemoryCache()
        => new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));

    private static FakeTimeProvider FixedTime()
        => new(new DateTimeOffset(2025, 6, 15, 10, 30, 45, TimeSpan.Zero));

    // ===== Round-trip / overwrite / delete on MemoryDistributedCache =====

    [Fact]
    public async Task Add_Snapshot_Present()
    {
        var cache = MemoryCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cache, options, NullLogger<TenantClientCacheService>.Instance, metrics, FixedTime());

        await service.WriteSnapshotAsync(SampleTenantKey, BuildClient(), CancellationToken.None);

        var envelope = await service.ReadSnapshotAsync(SampleTenantKey, SampleClientId, CancellationToken.None);

        envelope.Should().NotBeNull();
        envelope!.Version.Should().Be(1);
        envelope.TenantKey.Should().Be(SampleTenantKey);
        envelope.ClientId.Should().Be(SampleClientId);
        envelope.Data.ClientId.Should().Be(SampleClientId);
        envelope.Data.AllowedScopes.Should().Equal("openid", "profile");
    }

    [Fact]
    public async Task Update_Snapshot_Replaced()
    {
        var cache = MemoryCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cache, options, NullLogger<TenantClientCacheService>.Instance, metrics, FixedTime());

        var first = BuildClient();
        first.AllowedScopes = new List<string> { "openid" };

        var second = BuildClient();
        second.AllowedScopes = new List<string> { "openid", "email" };

        await service.WriteSnapshotAsync(SampleTenantKey, first, CancellationToken.None);
        await service.WriteSnapshotAsync(SampleTenantKey, second, CancellationToken.None);

        var envelope = await service.ReadSnapshotAsync(SampleTenantKey, SampleClientId, CancellationToken.None);

        envelope.Should().NotBeNull();
        envelope!.Data.AllowedScopes.Should().Equal("openid", "email");
    }

    [Fact]
    public async Task Delete_Snapshot_Removed()
    {
        var cache = MemoryCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cache, options, NullLogger<TenantClientCacheService>.Instance, metrics, FixedTime());

        await service.WriteSnapshotAsync(SampleTenantKey, BuildClient(), CancellationToken.None);
        await service.InvalidateSnapshotAsync(SampleTenantKey, SampleClientId, CancellationToken.None);

        var envelope = await service.ReadSnapshotAsync(SampleTenantKey, SampleClientId, CancellationToken.None);

        envelope.Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_Missing_Key_Is_Idempotent_Success()
    {
        // R6.7: invalidating a key that was never written succeeds silently.
        var cache = MemoryCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cache, options, NullLogger<TenantClientCacheService>.Instance, metrics, FixedTime());

        var act = async () => await service.InvalidateSnapshotAsync(
            "ghost-tenant", "ghost-client", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ===== Disabled / no-op ============================================

    [Fact]
    public async Task Enabled_False_NoOp_Skips_IDistributedCache_Calls()
    {
        // R1.7: every public op must short-circuit BEFORE touching the cache.
        var cacheMock = new Mock<IDistributedCache>(MockBehavior.Strict);
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(
            BuildOptions(enabled: false));
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cacheMock.Object,
            options,
            NullLogger<TenantClientCacheService>.Instance,
            metrics,
            FixedTime());

        await service.WriteSnapshotAsync(SampleTenantKey, BuildClient(), CancellationToken.None);
        await service.WriteSnapshotsAsync(
            new[] { SampleTenantKey, "tenant-b" }, BuildClient(), CancellationToken.None);
        var envelope = await service.ReadSnapshotAsync(SampleTenantKey, SampleClientId, CancellationToken.None);
        await service.InvalidateSnapshotAsync(SampleTenantKey, SampleClientId, CancellationToken.None);
        await service.InvalidateSnapshotsAsync(
            new[] { SampleTenantKey, "tenant-b" }, SampleClientId, CancellationToken.None);

        envelope.Should().BeNull();
        cacheMock.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        cacheMock.Verify(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ===== Fail-soft ===================================================

    [Fact]
    public async Task Redis_Down_Throws_Service_Swallows_Returns()
    {
        // R10.1: the underlying IDistributedCache may throw any exception
        // (Redis connection drop, timeout, etc). The service must catch +
        // log + count, never propagate.
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock
            .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));
        cacheMock
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(
            cacheMock.Object, options, logger, metrics, FixedTime());

        var write = async () => await service.WriteSnapshotAsync(
            SampleTenantKey, BuildClient(), CancellationToken.None);
        var invalidate = async () => await service.InvalidateSnapshotAsync(
            SampleTenantKey, SampleClientId, CancellationToken.None);

        await write.Should().NotThrowAsync();
        await invalidate.Should().NotThrowAsync();

        // Audit_Event must be emitted with the appropriate transient outcome.
        logger.WhereField("Outcome", "write_failed_transient").Should().HaveCountGreaterThan(0);
        logger.WhereField("Outcome", "invalidate_failed_transient").Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task Redis_Down_Audit_Message_Does_Not_Leak_Secret()
    {
        // R13.4: the audit event must use SanitizeExceptionMessage, so a
        // password=... fragment in the exception message MUST NOT survive.
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock
            .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "connstr=server,password=topsecret,auth=AAA"));

        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(
            cacheMock.Object, options, logger, metrics, FixedTime());

        await service.WriteSnapshotAsync(SampleTenantKey, BuildClient(), CancellationToken.None);

        var entry = logger.Entries.Single();
        entry.Fields.TryGetValue("ExceptionMessage", out var sanitized).Should().BeTrue();
        sanitized!.ToString().Should().NotContain("topsecret");
        sanitized.ToString().Should().NotContain("AAA");
    }

    // ===== Oversize ====================================================

    [Fact]
    public async Task Oversize_Snapshot_Rejected_Audit_Emitted()
    {
        // R2.6: snapshots over 256 KiB are rejected without a write.
        // We stuff AllowedScopes with many long strings until the
        // serialized payload crosses 256 KiB, then assert:
        //   1. WriteSnapshotAsync returns without throwing.
        //   2. IDistributedCache.SetAsync was NEVER called.
        //   3. The audit event names subreason "oversize".
        var cacheMock = new Mock<IDistributedCache>(MockBehavior.Strict);
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(
            cacheMock.Object, options, logger, metrics, FixedTime());

        var oversize = BuildClient();
        // Each scope contributes ~520 bytes (a 512-char string + JSON quoting).
        // 512 chars × 600 entries ≈ 307 KiB serialized — comfortably above
        // the 256 KiB ceiling.
        var blob = new string('x', 512);
        oversize.AllowedScopes = Enumerable.Repeat(blob, 600).ToList();

        var act = async () => await service.WriteSnapshotAsync(
            SampleTenantKey, oversize, CancellationToken.None);

        await act.Should().NotThrowAsync();
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Never);

        var failureEntry = logger
            .WhereField("Outcome", "write_failed_transient")
            .SingleOrDefault();
        failureEntry.Should().NotBeNull();
        failureEntry!.Fields.TryGetValue("Subreason", out var subreason).Should().BeTrue();
        subreason!.ToString().Should().Be("oversize");
    }

    // ===== Argument validation =========================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task Empty_TenantKey_Throws_ArgumentException_Without_Touching_Cache(string? tenantKey)
    {
        // R3.3: null/empty/whitespace tenantKey is rejected BEFORE any I/O.
        var cacheMock = new Mock<IDistributedCache>(MockBehavior.Strict);
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cacheMock.Object,
            options,
            NullLogger<TenantClientCacheService>.Instance,
            metrics,
            FixedTime());

        var write = async () => await service.WriteSnapshotAsync(
            tenantKey!, BuildClient(), CancellationToken.None);
        var read = async () => await service.ReadSnapshotAsync(
            tenantKey!, SampleClientId, CancellationToken.None);
        var invalidate = async () => await service.InvalidateSnapshotAsync(
            tenantKey!, SampleClientId, CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentException>();
        await read.Should().ThrowAsync<ArgumentException>();
        await invalidate.Should().ThrowAsync<ArgumentException>();

        cacheMock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n")]
    public async Task Empty_ClientId_Throws_ArgumentException_Without_Touching_Cache(string? clientId)
    {
        // R3.4: null/empty/whitespace clientId is rejected BEFORE any I/O.
        var cacheMock = new Mock<IDistributedCache>(MockBehavior.Strict);
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cacheMock.Object,
            options,
            NullLogger<TenantClientCacheService>.Instance,
            metrics,
            FixedTime());

        var clientWithBadId = BuildClient(clientId!);

        var write = async () => await service.WriteSnapshotAsync(
            SampleTenantKey, clientWithBadId, CancellationToken.None);
        var read = async () => await service.ReadSnapshotAsync(
            SampleTenantKey, clientId!, CancellationToken.None);
        var invalidate = async () => await service.InvalidateSnapshotAsync(
            SampleTenantKey, clientId!, CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentException>();
        await read.Should().ThrowAsync<ArgumentException>();
        await invalidate.Should().ThrowAsync<ArgumentException>();

        cacheMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClientId_Equals_ListSuffix_Throws_ArgumentException()
    {
        // Risks-table mitigation: __list__ is the per-tenant list helper key
        // suffix; refusing it as a clientId argument prevents the list key
        // being overwritten by a malicious / misconfigured caller.
        var cacheMock = new Mock<IDistributedCache>(MockBehavior.Strict);
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cacheMock.Object,
            options,
            NullLogger<TenantClientCacheService>.Instance,
            metrics,
            FixedTime());

        var write = async () => await service.WriteSnapshotAsync(
            SampleTenantKey, BuildClient("__list__"), CancellationToken.None);
        var read = async () => await service.ReadSnapshotAsync(
            SampleTenantKey, "__list__", CancellationToken.None);
        var invalidate = async () => await service.InvalidateSnapshotAsync(
            SampleTenantKey, "__list__", CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentException>();
        await read.Should().ThrowAsync<ArgumentException>();
        await invalidate.Should().ThrowAsync<ArgumentException>();

        cacheMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Null_TenantKeys_Collection_Throws_ArgumentNullException()
    {
        var cacheMock = new Mock<IDistributedCache>(MockBehavior.Strict);
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cacheMock.Object,
            options,
            NullLogger<TenantClientCacheService>.Instance,
            metrics,
            FixedTime());

        var write = async () => await service.WriteSnapshotsAsync(
            null!, BuildClient(), CancellationToken.None);
        var invalidate = async () => await service.InvalidateSnapshotsAsync(
            null!, SampleClientId, CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentNullException>();
        await invalidate.Should().ThrowAsync<ArgumentNullException>();

        cacheMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WriteSnapshotsAsync_Iterates_Sequentially_Per_Tenant()
    {
        // R3.6: one snapshot per (tenantKey, clientId) tuple. We use
        // MemoryDistributedCache to assert each tenant got its own key.
        var cache = MemoryCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cache, options, NullLogger<TenantClientCacheService>.Instance, metrics, FixedTime());

        var tenants = new[] { "tenant-a", "tenant-b", "tenant-c" };

        await service.WriteSnapshotsAsync(tenants, BuildClient(), CancellationToken.None);

        foreach (var tenant in tenants)
        {
            var envelope = await service.ReadSnapshotAsync(tenant, SampleClientId, CancellationToken.None);
            envelope.Should().NotBeNull();
            envelope!.TenantKey.Should().Be(tenant);
        }
    }

    [Fact]
    public async Task InvalidateSnapshotsAsync_Iterates_Sequentially_Per_Tenant()
    {
        var cache = MemoryCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cache, options, NullLogger<TenantClientCacheService>.Instance, metrics, FixedTime());

        var tenants = new[] { "tenant-a", "tenant-b", "tenant-c" };
        await service.WriteSnapshotsAsync(tenants, BuildClient(), CancellationToken.None);
        await service.InvalidateSnapshotsAsync(tenants, SampleClientId, CancellationToken.None);

        foreach (var tenant in tenants)
        {
            var envelope = await service.ReadSnapshotAsync(tenant, SampleClientId, CancellationToken.None);
            envelope.Should().BeNull();
        }
    }

    [Fact]
    public async Task TenantKey_Is_Normalized_Before_Reading_Or_Writing()
    {
        // R3.1: tenantKey normalization is Trim().ToLowerInvariant(). A
        // write under "  TenantA  " must be readable under "tenanta" — and
        // vice versa.
        var cache = MemoryCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cache, options, NullLogger<TenantClientCacheService>.Instance, metrics, FixedTime());

        await service.WriteSnapshotAsync("  TenantA  ", BuildClient(), CancellationToken.None);

        var envelope = await service.ReadSnapshotAsync("tenanta", SampleClientId, CancellationToken.None);

        envelope.Should().NotBeNull();
        envelope!.TenantKey.Should().Be("tenanta");
    }
}
