// Feature: tenant-client-cache-expansion, Task 5
//
// Property-based tests for TenantClientCacheService. Pinned properties:
//
//   P4  KeyFormat       — PerClientKey + PerTenantListKey produce the
//                         logical-key shape required by R3.1, R3.2, and
//                         do NOT collide with legacy / sibling namespaces.
//
//   P5  RejectEmpty     — every public method throws ArgumentException for
//                         null / empty / whitespace tenantKey or clientId
//                         AND never touches IDistributedCache.
//
//   P9  IdempotentWrite — N successive writes (N ∈ [1..5]) for the same
//                         (tenantKey, client) under a fixed clock leave
//                         IDistributedCache holding the byte-equal payload
//                         of the last write.
//
//   P11 TtlOptions      — every write captures DistributedCacheEntryOptions
//                         whose AbsoluteExpirationRelativeToNow == AbsoluteTtl
//                         and whose SlidingExpiration tracks SlidingTtl
//                         (null when null). Read path never calls SetAsync.
//
//   P12 DisabledNoOp    — Options.Enabled = false ⇒ every public method
//                         skips IDistributedCache entirely AND emits
//                         exactly one Debug audit per call with the
//                         documented "WriteSkippedDisabled" outcome.
//
// Validates: Requirements 1.7, 1.8, 3.1, 3.2, 3.3, 3.4, 3.5, 3.7, 3.8,
//            5.4, 5.5, 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7, 12.4

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

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

public sealed class TenantClientCacheKeyProperties
{
    // ===== Generators ==================================================

    public static class Arbs
    {
        private static readonly char[] AsciiPool =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_".ToCharArray();

        // Non-empty, non-whitespace, no internal whitespace. The service
        // contract says we Trim before normalizing, so it's enough to
        // generate identifiers that survive Trim() unchanged.
        private static Gen<string> NonEmptyIdGen()
            => from len in Gen.Choose(1, 16)
               from chars in Gen.Elements(AsciiPool).ListOf(len)
               select new string(chars.ToArray());

        // Whitespace-only or null inputs that R3.3 / R3.4 require us to
        // reject before touching the cache.
        private static Gen<string?> WhitespaceOrNullGen()
            => Gen.Elements<string?>("", " ", "\t", "\n", "  ", null);

        public static Arbitrary<NonEmptyId> NonEmptyId()
            => NonEmptyIdGen().Select(s => new NonEmptyId(s)).ToArbitrary();

        public static Arbitrary<WhitespaceOrNull> WhitespaceOrNull()
            => WhitespaceOrNullGen().Select(s => new WhitespaceOrNull(s)).ToArbitrary();

        public static Arbitrary<WriteCount> WriteCount()
            => Gen.Choose(1, 5).Select(n => new WriteCount(n)).ToArbitrary();
    }

    /// <summary>Strongly-typed wrapper so FsCheck doesn't try to use its
    /// default <c>string</c> arbitrary (which can produce arbitrary Unicode
    /// and would mask tenant-key normalization bugs).</summary>
    public sealed record NonEmptyId(string Value);

    /// <summary>Strongly-typed wrapper for a value that R3.3 / R3.4 mandate
    /// must be rejected: null, empty, or all-whitespace.</summary>
    public sealed record WhitespaceOrNull(string? Value);

    /// <summary>Bounded write iteration count for P9 idempotence. 1..5 is
    /// enough to catch race-style bugs without slowing the suite.</summary>
    public sealed record WriteCount(int Value);

    // ===== Helpers =====================================================

    private static TenantClientCacheOptions BuildOptions(
        bool enabled = true,
        TimeSpan? slidingTtl = null,
        TimeSpan? absoluteTtl = null)
        => new()
        {
            Enabled = enabled,
            AbsoluteTtl = absoluteTtl ?? TimeSpan.FromHours(1),
            SlidingTtl = slidingTtl,
            RefreshInterval = TimeSpan.FromHours(1),
            WriteTimeoutMs = 2000,
            MaxClientsPerTenant = 5000,
        };

    private static ClientDto BuildClient(string clientId)
        => new()
        {
            ClientId = clientId,
            ClientName = "Sample",
            Enabled = true,
            ProtocolType = "oidc",
            AllowedScopes = new List<string> { "openid", "profile" },
            RedirectUris = new List<string> { "https://app/callback" },
        };

    private static FakeTimeProvider FixedTime()
        => new(new DateTimeOffset(2025, 6, 15, 10, 30, 45, TimeSpan.Zero));

    // ===== Property 04 — KeyFormat =====================================

    /// <summary>
    /// P4 KeyFormat: PerClientKey / PerTenantListKey are the documented
    /// logical-key strings (no `tenant-registry:` prefix) AND do NOT
    /// collide with the legacy bare-clientId key, the existing
    /// `tenant:{tk}` key, or the public-tenant-names key.
    ///
    /// Validates: Requirements 3.1, 3.2, 3.5, 3.8, 12.4
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public void Property04_KeyFormat(NonEmptyId tenant, NonEmptyId client)
    {
        var tk = tenant.Value;
        var cid = client.Value;

        var perClient = TenantClientCacheService.PerClientKey(tk, cid);
        var perList = TenantClientCacheService.PerTenantListKey(tk);

        var expectedTenantSegment = tk.Trim().ToLowerInvariant();

        perClient.Should().Be(expectedTenantSegment + ":clients:" + cid.Trim(),
            "R3.1: per-client key shape is fixed");
        perList.Should().Be(expectedTenantSegment + ":clients:__list__",
            "R3.2: per-tenant list key uses the __list__ suffix to avoid clientId collision");

        // R12.4: the new key namespace MUST NOT collide with the legacy
        // bare-clientId key produced by IClientScopeCacheService.
        perClient.Should().NotBe(cid.Trim());
        perClient.Should().NotBe(cid);

        // R3.8: the new key namespace MUST NOT collide with sibling
        // TenantInfrastructure keys (tenant:{tk}, tenant:public:names).
        perClient.Should().NotBe("tenant:" + tk);
        perClient.Should().NotBe("tenant:public:names");
        perList.Should().NotBe("tenant:" + tk);
        perList.Should().NotBe("tenant:public:names");
    }

    // ===== Property 05 — RejectEmpty ====================================

    /// <summary>
    /// P5 RejectEmpty: a whitespace / null tenantKey OR clientId argument
    /// causes every public method to throw ArgumentException AND never
    /// touch IDistributedCache.
    ///
    /// Validates: Requirements 3.3, 3.4
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property05_RejectEmpty(WhitespaceOrNull bad, NonEmptyId good)
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

        // Bad tenantKey + good clientId.
        await BadTenantKey(service, bad.Value, good.Value);
        // Good tenantKey + bad clientId.
        await BadClientId(service, good.Value, bad.Value);

        // Strict mock: zero invocations is the contract.
        cacheMock.VerifyNoOtherCalls();
    }

    private static async Task BadTenantKey(
        TenantClientCacheService service,
        string? badTenant,
        string goodClientId)
    {
        var write = async () => await service.WriteSnapshotAsync(
            badTenant!, BuildClient(goodClientId), CancellationToken.None);
        var writeBatch = async () => await service.WriteSnapshotsAsync(
            new[] { badTenant! }, BuildClient(goodClientId), CancellationToken.None);
        var read = async () => await service.ReadSnapshotAsync(
            badTenant!, goodClientId, CancellationToken.None);
        var invalidate = async () => await service.InvalidateSnapshotAsync(
            badTenant!, goodClientId, CancellationToken.None);
        var invalidateBatch = async () => await service.InvalidateSnapshotsAsync(
            new[] { badTenant! }, goodClientId, CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentException>();
        await writeBatch.Should().ThrowAsync<ArgumentException>();
        await read.Should().ThrowAsync<ArgumentException>();
        await invalidate.Should().ThrowAsync<ArgumentException>();
        await invalidateBatch.Should().ThrowAsync<ArgumentException>();
    }

    private static async Task BadClientId(
        TenantClientCacheService service,
        string goodTenant,
        string? badClientId)
    {
        var clientWithBadId = BuildClient(badClientId!);

        var write = async () => await service.WriteSnapshotAsync(
            goodTenant, clientWithBadId, CancellationToken.None);
        var writeBatch = async () => await service.WriteSnapshotsAsync(
            new[] { goodTenant }, clientWithBadId, CancellationToken.None);
        var read = async () => await service.ReadSnapshotAsync(
            goodTenant, badClientId!, CancellationToken.None);
        var invalidate = async () => await service.InvalidateSnapshotAsync(
            goodTenant, badClientId!, CancellationToken.None);
        var invalidateBatch = async () => await service.InvalidateSnapshotsAsync(
            new[] { goodTenant }, badClientId!, CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentException>();
        await writeBatch.Should().ThrowAsync<ArgumentException>();
        await read.Should().ThrowAsync<ArgumentException>();
        await invalidate.Should().ThrowAsync<ArgumentException>();
        await invalidateBatch.Should().ThrowAsync<ArgumentException>();
    }

    // ===== Property 09 — IdempotentWrite ================================

    /// <summary>
    /// P9 IdempotentWrite: writing the same (tenantKey, client) N times
    /// (N ∈ [1..5]) under a fixed clock leaves IDistributedCache holding
    /// the byte-equal payload of the last write. We use MemoryDistributedCache
    /// + a fixed FakeTimeProvider so the envelope's LastWriteUtc cannot
    /// drift between iterations.
    ///
    /// Validates: Requirements 5.5
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property09_IdempotentWrite(
        NonEmptyId tenant,
        NonEmptyId client,
        WriteCount count)
    {
        var cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var time = FixedTime();
        var service = new TenantClientCacheService(
            cache, options, NullLogger<TenantClientCacheService>.Instance, metrics, time);

        var dto = BuildClient(client.Value);

        // Run N consecutive writes. The clock is fixed, so each iteration
        // produces a byte-identical envelope.
        for (var i = 0; i < count.Value; i++)
        {
            await service.WriteSnapshotAsync(tenant.Value, dto, CancellationToken.None);
        }

        // Compute the expected payload independently to assert byte-equality.
        var expectedSnapshot = ClientCacheSnapshotMapper.Map(dto, time.GetUtcNow().UtcDateTime);
        var expectedEnvelope = new ClientCacheSnapshotEnvelope
        {
            Version = 1,
            TenantKey = tenant.Value.Trim().ToLowerInvariant(),
            ClientId = dto.ClientId.Trim(),
            LastWriteUtc = time.GetUtcNow().UtcDateTime,
            Data = expectedSnapshot,
        };
        var expectedPayload = ClientCacheSnapshotSerializer.Serialize(expectedEnvelope);

        var key = TenantClientCacheService.PerClientKey(tenant.Value, dto.ClientId);
        var actualPayload = await cache.GetAsync(key, CancellationToken.None);

        actualPayload.Should().NotBeNull();
        actualPayload!.Should().Equal(expectedPayload);
    }

    // ===== Property 11 — TtlOptions =====================================

    /// <summary>
    /// P11 TtlOptions: every write call passes a
    /// <see cref="DistributedCacheEntryOptions"/> whose
    /// AbsoluteExpirationRelativeToNow equals Options.AbsoluteTtl AND whose
    /// SlidingExpiration matches Options.SlidingTtl (null when null). The
    /// read path NEVER calls SetAsync.
    ///
    /// Validates: Requirements 5.4, 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property11_TtlOptions(NonEmptyId tenant, NonEmptyId client, bool slidingEnabled)
    {
        // Vary AbsoluteTtl and SlidingTtl across the property's iteration
        // domain via deterministic mapping from the 'slidingEnabled' arg.
        var absolute = TimeSpan.FromMinutes(slidingEnabled ? 30 : 60);
        var sliding = slidingEnabled ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;

        DistributedCacheEntryOptions? captured = null;
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock
            .Setup(c => c.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>(
                (_, _, opts, _) => captured = opts)
            .Returns(Task.CompletedTask);

        var options = new StubOptionsMonitor<TenantClientCacheOptions>(
            BuildOptions(slidingTtl: sliding, absoluteTtl: absolute));
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cacheMock.Object,
            options,
            NullLogger<TenantClientCacheService>.Instance,
            metrics,
            FixedTime());

        await service.WriteSnapshotAsync(tenant.Value, BuildClient(client.Value), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.AbsoluteExpirationRelativeToNow.Should().Be(absolute,
            "R9.1: every write passes AbsoluteTtl as AbsoluteExpirationRelativeToNow");

        if (sliding is null)
        {
            captured.SlidingExpiration.Should().BeNull(
                "R9.3: SlidingExpiration stays null when SlidingTtl is null");
        }
        else
        {
            captured.SlidingExpiration.Should().Be(sliding,
                "R9.2: SlidingExpiration mirrors SlidingTtl when configured");
        }

        // Read path: SetAsync MUST NOT be called.
        await service.ReadSnapshotAsync(tenant.Value, client.Value, CancellationToken.None);
        cacheMock.Verify(c => c.SetAsync(
            It.IsAny<string>(),
            It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ===== Property 12 — DisabledNoOp ===================================

    /// <summary>
    /// P12 DisabledNoOp: when Options.Enabled = false, every public method
    /// (a) skips IDistributedCache entirely, AND (b) emits exactly one
    /// Debug audit per call carrying outcome="write_skipped_disabled" — the
    /// shape every disabled-mode no-op uses across read / write / invalidate.
    ///
    /// Validates: Requirements 1.7, 1.8
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property12_DisabledNoOp(NonEmptyId tenant, NonEmptyId client)
    {
        var cacheMock = new Mock<IDistributedCache>(MockBehavior.Strict);
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(
            BuildOptions(enabled: false));
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(
            cacheMock.Object, options, logger, metrics, FixedTime());

        // Five operations (single + batch) ⇒ five disabled audit entries.
        await service.WriteSnapshotAsync(tenant.Value, BuildClient(client.Value), CancellationToken.None);
        await service.WriteSnapshotsAsync(
            new[] { tenant.Value }, BuildClient(client.Value), CancellationToken.None);
        var read = await service.ReadSnapshotAsync(tenant.Value, client.Value, CancellationToken.None);
        await service.InvalidateSnapshotAsync(tenant.Value, client.Value, CancellationToken.None);
        await service.InvalidateSnapshotsAsync(
            new[] { tenant.Value }, client.Value, CancellationToken.None);

        // Read returns null on a disabled cache (no envelope to surface).
        read.Should().BeNull();

        // Strict mock proves zero IDistributedCache invocations.
        cacheMock.VerifyNoOtherCalls();

        // Each op emits one Debug entry with outcome=write_skipped_disabled.
        var skipped = logger
            .WhereField("Outcome", "write_skipped_disabled")
            .ToList();

        skipped.Should().HaveCount(5);
        skipped.Should().OnlyContain(e => e.Level == LogLevel.Debug,
            "R1.7: disabled-mode audits are emitted at Debug level");
    }
}
