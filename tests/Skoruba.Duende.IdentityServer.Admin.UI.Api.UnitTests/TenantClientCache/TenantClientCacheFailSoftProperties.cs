// Feature: tenant-client-cache-expansion, Task 6
//
// Property tests for the fail-soft umbrella contract of
// TenantClientCacheService. For an arbitrary sequence of public
// operations interleaved with deterministic exception injection on the
// underlying IDistributedCache:
//
//   * No exception escapes ITenantClientCacheService.*Async.
//   * Each failed call emits exactly one Audit_Event with outcome
//     write_failed_transient OR invalidate_failed_transient.
//   * The underlying IDistributedCache.*Async is invoked at most once
//     per logical op (R10.3 — no retry).
//
// Property: P10 Fail-soft umbrella.
//
// Validates: Requirements 10.1, 10.2, 10.3, 10.5

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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class TenantClientCacheFailSoftProperties
{
    // ===== Generators ==================================================

    public enum FailSoftOp
    {
        Write,
        Invalidate,
        Read,
    }

    public sealed record FailSoftStep(FailSoftOp Op, bool ShouldThrow);

    public sealed record OpSequence(IReadOnlyList<FailSoftStep> Steps);

    public sealed record NonEmptyId(string Value);

    public static class Arbs
    {
        private static readonly char[] AsciiPool =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_".ToCharArray();

        private static Gen<string> NonEmptyIdGen()
            => from len in Gen.Choose(1, 12)
               from chars in Gen.Elements(AsciiPool).ListOf(len)
               select new string(chars.ToArray());

        public static Arbitrary<NonEmptyId> NonEmptyId()
            => NonEmptyIdGen().Select(s => new NonEmptyId(s)).ToArbitrary();

        public static Arbitrary<OpSequence> OpSequence()
        {
            var stepGen =
                from op in Gen.Elements(FailSoftOp.Write, FailSoftOp.Invalidate, FailSoftOp.Read)
                from shouldThrow in Gen.Elements(true, false)
                select new FailSoftStep(op, shouldThrow);

            return (from len in Gen.Choose(1, 8)
                    from steps in stepGen.ListOf(len)
                    select new OpSequence(steps.ToArray()))
                .ToArbitrary();
        }
    }

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

    // ===== Property 10 — Fail-soft umbrella =============================

    /// <summary>
    /// P10: for any sequence of (op, shouldThrow) the service catches
    /// every IDistributedCache exception, emits exactly one Audit_Event
    /// per failed op carrying the documented transient outcome, and
    /// invokes the underlying cache method at most once per logical op
    /// (R10.3 — no retry loop).
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property10_FailSoft(
        OpSequence seq,
        NonEmptyId tenant,
        NonEmptyId client)
    {
        var cache = new ThrowingDistributedCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(cache, options, logger, metrics, FixedTime());

        var expectedFailures = 0;

        foreach (var step in seq.Steps)
        {
            var beforeCalls = cache.TotalAsyncCallCount;
            logger.Clear();

            if (step.ShouldThrow)
            {
                cache.ThrowOnNext(new InvalidOperationException("redis transient failure"));
            }

            // No exception MUST escape the public surface — the
            // assertion shape is "act + should NotThrow".
            Func<Task> act = step.Op switch
            {
                FailSoftOp.Write => async () => await service.WriteSnapshotAsync(
                    tenant.Value, BuildClient(client.Value), CancellationToken.None),

                FailSoftOp.Invalidate => async () => await service.InvalidateSnapshotAsync(
                    tenant.Value, client.Value, CancellationToken.None),

                FailSoftOp.Read => async () => await service.ReadSnapshotAsync(
                    tenant.Value, client.Value, CancellationToken.None),

                _ => throw new InvalidOperationException("unknown op"),
            };

            await act.Should().NotThrowAsync(
                "R10.2: fail-soft contract — no exception ever escapes ITenantClientCacheService");

            // No retry: the service issued AT MOST one underlying cache
            // call per logical operation.
            var afterCalls = cache.TotalAsyncCallCount;
            (afterCalls - beforeCalls).Should().BeLessThanOrEqualTo(1,
                "R10.3: at most one underlying IDistributedCache call per logical op");

            if (step.ShouldThrow)
            {
                expectedFailures++;

                // Exactly one Audit_Event for the failed op carrying
                // the documented transient outcome value. Reads degrade
                // to a Miss (Debug) audit per R10.4-style behaviour, so
                // they're not counted as Warning failures.
                if (step.Op == FailSoftOp.Read)
                {
                    // Read failure is a Miss (Warning level per service
                    // contract), but the outcome string is still "miss".
                    var entry = logger.Entries.Single();
                    entry.Fields.TryGetValue("Outcome", out var outcome).Should().BeTrue();
                    outcome!.ToString().Should().Be("miss",
                        "R10.4: read failures degrade to Miss without panic");
                }
                else
                {
                    var expectedOutcome = step.Op == FailSoftOp.Write
                        ? "write_failed_transient"
                        : "invalidate_failed_transient";

                    var entry = logger.Entries.SingleOrDefault(
                        e => Equals(e.Fields.GetValueOrDefault("Outcome"), expectedOutcome));
                    entry.Should().NotBeNull(
                        $"a {step.Op} op that throws must emit one '{expectedOutcome}' Audit_Event");
                    entry!.Level.Should().Be(LogLevel.Warning);
                }
            }
        }

        // Sanity: total recorded throws used.
        cache.HasPendingThrows.Should().BeFalse(
            "every scheduled exception was consumed by exactly one op");
        expectedFailures.Should().Be(seq.Steps.Count(s => s.ShouldThrow));
    }

    // ===== Example-based supplements ===================================

    [Fact]
    public async Task Successful_Write_After_Failure_Resets_Audit_Stream()
    {
        // After a failed write the service must remain usable: the next
        // successful write emits a fresh Information audit.
        var cache = new ThrowingDistributedCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(cache, options, logger, metrics, FixedTime());

        cache.ThrowOnNext(new InvalidOperationException("redis flaky"));

        await service.WriteSnapshotAsync("tenant-a", BuildClient("client-1"), CancellationToken.None);
        await service.WriteSnapshotAsync("tenant-a", BuildClient("client-1"), CancellationToken.None);

        logger.Entries.Should().HaveCount(2);
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Fields["Outcome"].Should().Be("write_failed_transient");
        logger.Entries[1].Level.Should().Be(LogLevel.Information);
        logger.Entries[1].Fields["Outcome"].Should().Be("write_succeeded");
    }

    [Fact]
    public async Task FailSoft_Does_Not_Trigger_Synchronous_Retry()
    {
        // R10.3: one logical op = exactly one underlying cache call.
        var cache = new ThrowingDistributedCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var service = new TenantClientCacheService(
            cache, options, NullLogger<TenantClientCacheService>.Instance, metrics, FixedTime());

        cache.ThrowOnNext(new InvalidOperationException("redis down"));
        await service.WriteSnapshotAsync("tenant-a", BuildClient("client-1"), CancellationToken.None);

        cache.SetAsyncCallCount.Should().Be(1, "no synchronous retry loop");
    }
}
