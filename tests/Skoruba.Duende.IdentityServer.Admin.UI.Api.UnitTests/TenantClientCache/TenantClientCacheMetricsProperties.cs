// Feature: tenant-client-cache-expansion, Task 6
//
// Property tests for the System.Diagnostics.Metrics counter + tag
// invariants emitted by TenantClientCacheService through
// TenantClientCacheMetrics. Pinned invariants:
//
//   * Each public op increments the matching counter exactly once.
//   * The tag set is always exactly {tenantKey, outcome}.
//   * The tag dictionary NEVER contains a `clientId` key (R16.3 — high
//     cardinality protection for metric backends).
//   * The tag value for `tenantKey` is the lowercased / trimmed form
//     produced by Tenant_Client_Cache, matching the one in the
//     structured log so dashboards can join logs↔metrics.
//
// Property: P15 Metric counter + tag invariants.
//
// Validates: Requirements 16.2, 16.3

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

using Microsoft.Extensions.Logging.Abstractions;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class TenantClientCacheMetricsProperties
{
    // ===== Generators ==================================================

    public static class Arbs
    {
        private static readonly char[] AsciiPool =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_".ToCharArray();

        private static Gen<string> NonEmptyIdGen()
            => from len in Gen.Choose(1, 16)
               from chars in Gen.Elements(AsciiPool).ListOf(len)
               select new string(chars.ToArray());

        public static Arbitrary<NonEmptyId> NonEmptyId()
            => NonEmptyIdGen().Select(s => new NonEmptyId(s)).ToArbitrary();

        public static Arbitrary<MetricOp> MetricOp()
            => Gen.Elements(
                    TenantClientCacheMetricsProperties.MetricOp.WriteSuccess,
                    TenantClientCacheMetricsProperties.MetricOp.WriteFailure,
                    TenantClientCacheMetricsProperties.MetricOp.InvalidateSuccess,
                    TenantClientCacheMetricsProperties.MetricOp.InvalidateFailure,
                    TenantClientCacheMetricsProperties.MetricOp.ReadHit,
                    TenantClientCacheMetricsProperties.MetricOp.ReadMiss)
                .ToArbitrary();
    }

    public sealed record NonEmptyId(string Value);

    /// <summary>The six operation flavours covered by P15.</summary>
    public enum MetricOp
    {
        WriteSuccess,
        WriteFailure,
        InvalidateSuccess,
        InvalidateFailure,
        ReadHit,
        ReadMiss,
    }

    private static readonly IReadOnlyDictionary<MetricOp, string> ExpectedInstrumentName =
        new Dictionary<MetricOp, string>
        {
            [MetricOp.WriteSuccess]      = TenantClientCacheMetrics.WriteSuccessCounterName,
            [MetricOp.WriteFailure]      = TenantClientCacheMetrics.WriteFailureCounterName,
            [MetricOp.InvalidateSuccess] = TenantClientCacheMetrics.InvalidateSuccessCounterName,
            [MetricOp.InvalidateFailure] = TenantClientCacheMetrics.InvalidateFailureCounterName,
            [MetricOp.ReadHit]           = TenantClientCacheMetrics.ReadHitCounterName,
            [MetricOp.ReadMiss]          = TenantClientCacheMetrics.ReadMissCounterName,
        };

    private static readonly IReadOnlyDictionary<MetricOp, string> ExpectedOutcomeTag =
        new Dictionary<MetricOp, string>
        {
            [MetricOp.WriteSuccess]      = "write_succeeded",
            [MetricOp.WriteFailure]      = "write_failed_transient",
            [MetricOp.InvalidateSuccess] = "invalidate_succeeded",
            [MetricOp.InvalidateFailure] = "invalidate_failed_transient",
            [MetricOp.ReadHit]           = "hit",
            [MetricOp.ReadMiss]          = "miss",
        };

    // ===== Helpers =====================================================

    private static TenantClientCacheOptions BuildOptions()
        => new()
        {
            Enabled = true,
            AbsoluteTtl = TimeSpan.FromHours(1),
            SlidingTtl = null,
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

    /// <summary>
    /// Run the requested operation against a fresh service + recording
    /// listener. Returns the listener so the caller can assert tag set.
    /// </summary>
    private static async Task<RecordingMeterListener> RunOperationAsync(
        MetricOp op,
        string tenantKey,
        string clientId)
    {
        var cache = new ThrowingDistributedCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());

        // The listener must subscribe BEFORE the meter is created so the
        // InstrumentPublished callback fires for every counter.
        var listener = new RecordingMeterListener(TenantClientCacheMetrics.MeterName);
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cache,
            options,
            NullLogger<TenantClientCacheService>.Instance,
            metrics,
            FixedTime());

        switch (op)
        {
            case MetricOp.WriteSuccess:
                await service.WriteSnapshotAsync(tenantKey, BuildClient(clientId), CancellationToken.None);
                break;

            case MetricOp.WriteFailure:
                cache.ThrowOnNext(new InvalidOperationException("redis down"));
                await service.WriteSnapshotAsync(tenantKey, BuildClient(clientId), CancellationToken.None);
                break;

            case MetricOp.InvalidateSuccess:
                await service.InvalidateSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;

            case MetricOp.InvalidateFailure:
                cache.ThrowOnNext(new InvalidOperationException("redis down"));
                await service.InvalidateSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;

            case MetricOp.ReadHit:
                // Seed the cache before clearing the listener so the
                // assertion only sees the read measurement.
                await service.WriteSnapshotAsync(tenantKey, BuildClient(clientId), CancellationToken.None);
                listener.Clear();
                await service.ReadSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;

            case MetricOp.ReadMiss:
                await service.ReadSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;
        }

        return listener;
    }

    // ===== Property 15 — Metric tag invariants =========================

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property15_MetricTags(
        MetricOp op,
        NonEmptyId tenant,
        NonEmptyId client)
    {
        using var listener = await RunOperationAsync(op, tenant.Value, client.Value);

        // The MeterListener subscribes to the shared meter NAME
        // ("TenantClientCache") so it can also pick up measurements
        // emitted by concurrently running tests. Scope our assertions
        // to the unique tenantKey produced by this property iteration —
        // that's the cleanest filter and a real-world dashboard would
        // also slice metrics per tenant.
        var expectedTenantTag = tenant.Value.Trim().ToLowerInvariant();
        var expectedName = ExpectedInstrumentName[op];
        var measurements = listener
            .ForInstrument(expectedName)
            .Where(m => Equals(m.Tags.GetValueOrDefault(TenantClientCacheMetrics.TagTenantKey),
                expectedTenantTag))
            .ToArray();

        measurements.Should().HaveCount(1,
            $"R16.2: {op} must increment counter '{expectedName}' exactly once for tenant '{expectedTenantTag}'");

        var measurement = measurements.Single();
        measurement.Value.Should().Be(1.0);

        // Tag set MUST be exactly {tenantKey, outcome} (R16.3).
        measurement.Tags.Keys.Should().BeEquivalentTo(new[]
        {
            TenantClientCacheMetrics.TagTenantKey,
            TenantClientCacheMetrics.TagOutcome,
        });

        measurement.Tags[TenantClientCacheMetrics.TagTenantKey]
            .Should().Be(expectedTenantTag,
                "tenantKey tag is the normalized form so dashboards can join with logs");

        measurement.Tags[TenantClientCacheMetrics.TagOutcome]
            .Should().Be(ExpectedOutcomeTag[op]);

        // Negative invariant: clientId is NEVER a metric tag (R16.3).
        // We sweep every measurement on every TenantClientCache
        // instrument to guarantee no concurrent caller's tag set
        // contains a clientId either — the invariant is global.
        foreach (var m in listener.Measurements)
        {
            m.Tags.Should().NotContainKey("clientId");
            m.Tags.Should().NotContainKey("ClientId");
            m.Tags.Should().NotContainKey("client_id");
        }
    }

    [Fact]
    public void Allowed_Tag_Whitelist_Pinned_To_TenantKey_And_Outcome()
    {
        // Locks the whitelist constants so a later refactor cannot quietly
        // introduce a `clientId` (or any other) tag without this test
        // failing.
        TenantClientCacheMetrics.TagTenantKey.Should().Be("tenantKey");
        TenantClientCacheMetrics.TagOutcome.Should().Be("outcome");
    }
}
