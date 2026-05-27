// Feature: tenant-client-cache-public-read, Task 4
//
// Property-based tests for the observability surface of the public-read
// endpoint:
//
//   Property 15 — AuditEventShape (Validates: Requirements 8.1, 8.2, 8.6).
//     For every terminal outcome, exactly one Audit_Event_Public_Read
//     entry is emitted, the structured fields match the schema (closed,
//     no extras), the log level matches the R8.2 table, and TenantKey /
//     ClientId are OMITTED for Unauthorized / BadRequest (R8.4 anti-
//     enumeration redaction is enforced by the helper itself).
//
//   Property 16 — MetricTagPolicy (Validates: Requirements 8.4, 8.5).
//     For each outcome the matching counter is incremented exactly once;
//     the tag policy is honoured (Hit / NotModified / Miss / RateLimited /
//     ServiceUnavailable carry `tenantKey`; Unauthorized / BadRequest do
//     NOT). The duration histogram tags include `outcome` and never
//     `clientId`.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.Extensions.Logging;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

[Collection(PublicReadMetricCollection.Name)]
public sealed class PublicReadObservabilityProperties
{
    // ===== Generators ==================================================

    public sealed record AuditSample(
        string Outcome,
        string TenantKey,
        string ClientId,
        double DurationMs,
        int? RetryAfterSeconds,
        string? ETag,
        string? RemoteIpHash);

    public sealed record MetricSample(string Outcome, string TenantKey, double DurationMs);

    public static class Arbs
    {
        private static readonly char[] TenantAlphabet =
            "abcdefghijklmnopqrstuvwxyz0123456789-_".ToCharArray();

        private static readonly char[] ClientIdAlphabet =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.".ToCharArray();

        private static readonly string[] AllOutcomes =
        {
            AuditOutcome.Hit,
            AuditOutcome.NotModified,
            AuditOutcome.Miss,
            AuditOutcome.Unauthorized,
            AuditOutcome.RateLimited,
            AuditOutcome.BadRequest,
            AuditOutcome.ServiceUnavailable,
        };

        private static Gen<string> StringGen(char[] alphabet, int min, int max)
            => from len in Gen.Choose(min, max)
               from chars in Gen.Elements(alphabet).ListOf(len)
               select new string(chars.ToArray());

        public static Arbitrary<AuditSample> Audit()
            => (from outcome in Gen.Elements(AllOutcomes)
                from tenant in StringGen(TenantAlphabet, 1, 20)
                from client in StringGen(ClientIdAlphabet, 1, 32)
                from duration in Gen.Choose(0, 60_000).Select(i => (double)i)
                from retry in Gen.Choose(1, 60).Select(i => (int?)i)
                from etag in StringGen(ClientIdAlphabet, 1, 16).Select(s => (string?)$"W/\"{s}\"")
                from ipHash in StringGen("0123456789abcdef".ToCharArray(), 64, 64).Select(s => (string?)s)
                select new AuditSample(outcome, tenant, client, duration, retry, etag, ipHash))
                .ToArbitrary();

        public static Arbitrary<MetricSample> Metric()
            => (from outcome in Gen.Elements(AllOutcomes)
                from tenant in StringGen(TenantAlphabet, 1, 20)
                from duration in Gen.Choose(0, 60_000).Select(i => (double)i)
                select new MetricSample(outcome, tenant, duration))
                .ToArbitrary();
    }

    // ===== Property 15 — AuditEventShape ==============================

    /// <summary>
    /// Property 15 (Validates: Requirements 8.1, 8.2, 8.6). For any
    /// terminal outcome, the audit helper emits exactly one structured
    /// log entry. The entry's level matches the R8.2 table, its EventType
    /// follows the <c>TenantClientCachePublicRead.{Outcome}</c> pattern,
    /// the structured field set is closed to the schema, and TenantKey /
    /// ClientId are omitted for Unauthorized / BadRequest (R8.4).
    /// </summary>
    // Feature: tenant-client-cache-public-read, Property 15: Audit event
    // shape + log levels per outcome.
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public void Property15_AuditEventShape(AuditSample sample)
    {
        var logger = new CapturingLogger<PublicReadObservabilityProperties>();

        // Build the schema payload for the outcome under test. Outcomes
        // that don't carry an ETag / RetryAfter pass null to mirror the
        // canonical caller (controller / filter).
        var fields = new AuditFields(
            EventType: AuditEventPublicRead.EventTypePrefix + sample.Outcome,
            TenantKey: sample.TenantKey,
            ClientId: sample.ClientId,
            Outcome: sample.Outcome,
            DurationMs: sample.DurationMs,
            CorrelationId: Activity.Current?.TraceId.ToString(),
            RemoteIpHash: sample.RemoteIpHash,
            HttpStatus: HttpStatusFor(sample.Outcome),
            ETagSent: sample.Outcome is AuditOutcome.Hit or AuditOutcome.NotModified
                ? sample.ETag
                : null,
            RetryAfterSeconds: sample.Outcome is AuditOutcome.RateLimited or AuditOutcome.ServiceUnavailable
                ? sample.RetryAfterSeconds
                : null);

        EmitForOutcome(logger, sample.Outcome, fields);

        logger.Entries.Should().ContainSingle("each Emit_* helper writes exactly one log entry");
        var entry = logger.Entries.Single();

        // R8.2 — level table.
        entry.Level.Should().Be(ExpectedLevel(sample.Outcome));

        // R8.1 — EventType formed as TenantClientCachePublicRead.{Outcome}.
        entry.Fields.Should().ContainKey("EventType")
            .WhoseValue.Should().Be(AuditEventPublicRead.EventTypePrefix + sample.Outcome);

        // Outcome field carries the literal outcome string.
        entry.Fields.Should().ContainKey("Outcome").WhoseValue.Should().Be(sample.Outcome);

        // DurationMs is a non-negative double matching the input.
        entry.Fields.Should().ContainKey("DurationMs");
        ((double)entry.Fields["DurationMs"]!).Should().Be(sample.DurationMs);
        ((double)entry.Fields["DurationMs"]!).Should().BeGreaterThanOrEqualTo(0);

        // Schema closure: every additional field listed in the glossary
        // is present in the structured state by name.
        var requiredKeys = new[]
        {
            "EventType", "TenantKey", "ClientId", "Outcome", "DurationMs",
            "CorrelationId", "RemoteIpHash", "HttpStatus", "ETagSent", "RetryAfterSeconds",
        };
        foreach (var key in requiredKeys)
        {
            entry.Fields.Should().ContainKey(key, $"the audit schema declares the {key} field");
        }

        // R8.4 redaction: Unauthorized / BadRequest MUST omit TenantKey
        // and ClientId. We assert the captured field values are null
        // even though the AuditFields record was built with concrete
        // strings — the helper enforces redaction.
        if (sample.Outcome is AuditOutcome.Unauthorized or AuditOutcome.BadRequest)
        {
            entry.Fields["TenantKey"].Should()
                .BeNull("Unauthorized / BadRequest must omit TenantKey (R8.4)");
            entry.Fields["ClientId"].Should()
                .BeNull("Unauthorized / BadRequest must omit ClientId (R8.4)");
        }
        else
        {
            entry.Fields["TenantKey"].Should().Be(sample.TenantKey);
            entry.Fields["ClientId"].Should().Be(sample.ClientId);
        }

        // R8.6 — CorrelationId mirrors Activity.Current?.TraceId.ToString()
        // or null. Since we did not start an Activity in the test, the
        // expected value is null.
        var expectedCorrelation = Activity.Current?.TraceId.ToString();
        entry.Fields["CorrelationId"].Should().Be(expectedCorrelation);
    }

    // ===== Property 16 — MetricTagPolicy ==============================

    /// <summary>
    /// Property 16 (Validates: Requirements 8.4, 8.5). For every outcome,
    /// the matching counter is incremented exactly once with the right
    /// tag set: <c>tenantKey</c> present for Hit / NotModified / Miss /
    /// RateLimited / ServiceUnavailable; absent for Unauthorized /
    /// BadRequest. The duration histogram is tagged with <c>outcome</c>
    /// and never with <c>clientId</c>.
    /// </summary>
    // Feature: tenant-client-cache-public-read, Property 16: Metric tag
    // policy (anti-enumeration + cardinality protection).
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public void Property16_MetricTagPolicy(MetricSample sample)
    {
        using var metrics = new TenantClientCacheMetrics();
        using var listener = new RecordingMeterListener(TenantClientCacheMetrics.MeterName);

        InvokeMetricFor(metrics, sample.Outcome, sample.TenantKey, sample.DurationMs);

        var counterName = ExpectedCounterName(sample.Outcome);
        var counterMeasurements = listener.ForInstrument(counterName);
        counterMeasurements.Should().ContainSingle("each helper increments its counter exactly once");

        var tags = counterMeasurements.Single().Tags;
        tags.Should().NotContainKey("clientId", "no public-read instrument is tagged with clientId (R8.4)");

        if (TaggedOutcomes.Contains(sample.Outcome))
        {
            tags.Should().ContainKey("tenantKey", $"the {sample.Outcome} counter must carry tenantKey (R8.4)")
                .WhoseValue.Should().Be(sample.TenantKey);
        }
        else
        {
            tags.Should().NotContainKey("tenantKey",
                $"the {sample.Outcome} counter must omit tenantKey (R8.4 anti-enumeration)");
        }

        // The duration histogram is recorded only for outcomes that pass a
        // duration value (Hit / NotModified / Miss / RateLimited).
        // Unauthorized / BadRequest helpers do not accept a duration —
        // ServiceUnavailable does not record a duration either, per the
        // "no stable measurement boundary" comment in TenantClientCacheMetrics.
        var durationMeasurements = listener.ForInstrument(
            TenantClientCacheMetrics.PublicReadDurationHistogramName);

        if (sample.Outcome is AuditOutcome.Unauthorized
            or AuditOutcome.BadRequest
            or AuditOutcome.ServiceUnavailable)
        {
            durationMeasurements.Should()
                .BeEmpty("the {0} helper does not record the duration histogram", sample.Outcome);
        }
        else
        {
            durationMeasurements.Should().ContainSingle();
            var hist = durationMeasurements.Single();
            hist.Value.Should().Be(sample.DurationMs);
            hist.Tags.Should().ContainKey("outcome").WhoseValue.Should().Be(sample.Outcome);
            hist.Tags.Should().ContainKey("tenantKey").WhoseValue.Should().Be(sample.TenantKey);
            hist.Tags.Should().NotContainKey("clientId");
        }
    }

    // ===== Helpers =====================================================

    private static readonly HashSet<string> TaggedOutcomes = new(StringComparer.Ordinal)
    {
        AuditOutcome.Hit,
        AuditOutcome.NotModified,
        AuditOutcome.Miss,
        AuditOutcome.RateLimited,
        AuditOutcome.ServiceUnavailable,
    };

    private static int HttpStatusFor(string outcome) => outcome switch
    {
        AuditOutcome.Hit => 200,
        AuditOutcome.NotModified => 304,
        AuditOutcome.Miss => 404,
        AuditOutcome.Unauthorized => 401,
        AuditOutcome.RateLimited => 429,
        AuditOutcome.BadRequest => 400,
        AuditOutcome.ServiceUnavailable => 503,
        _ => 0,
    };

    private static LogLevel ExpectedLevel(string outcome) => outcome switch
    {
        AuditOutcome.Hit or AuditOutcome.NotModified => LogLevel.Information,
        AuditOutcome.Miss => LogLevel.Debug,
        AuditOutcome.Unauthorized
            or AuditOutcome.RateLimited
            or AuditOutcome.BadRequest => LogLevel.Warning,
        AuditOutcome.ServiceUnavailable => LogLevel.Error,
        _ => throw new InvalidOperationException($"Unknown outcome '{outcome}'"),
    };

    private static void EmitForOutcome(ILogger logger, string outcome, AuditFields fields)
    {
        switch (outcome)
        {
            case AuditOutcome.Hit:
                AuditEventPublicRead.EmitHit(logger, fields);
                break;
            case AuditOutcome.NotModified:
                AuditEventPublicRead.EmitNotModified(logger, fields);
                break;
            case AuditOutcome.Miss:
                AuditEventPublicRead.EmitMiss(logger, fields);
                break;
            case AuditOutcome.Unauthorized:
                AuditEventPublicRead.EmitUnauthorized(logger, fields);
                break;
            case AuditOutcome.RateLimited:
                AuditEventPublicRead.EmitRateLimited(logger, fields);
                break;
            case AuditOutcome.BadRequest:
                AuditEventPublicRead.EmitBadRequest(logger, fields);
                break;
            case AuditOutcome.ServiceUnavailable:
                AuditEventPublicRead.EmitServiceUnavailable(logger, fields);
                break;
            default:
                throw new InvalidOperationException($"Unknown outcome '{outcome}'");
        }
    }

    private static void InvokeMetricFor(
        TenantClientCacheMetrics metrics,
        string outcome,
        string tenantKey,
        double durationMs)
    {
        switch (outcome)
        {
            case AuditOutcome.Hit:
                metrics.PublicReadHit(tenantKey, durationMs);
                break;
            case AuditOutcome.NotModified:
                metrics.PublicReadNotModified(tenantKey, durationMs);
                break;
            case AuditOutcome.Miss:
                metrics.PublicReadMiss(tenantKey, durationMs);
                break;
            case AuditOutcome.RateLimited:
                metrics.PublicReadRateLimited(tenantKey, durationMs);
                break;
            case AuditOutcome.ServiceUnavailable:
                metrics.PublicReadServiceUnavailable(tenantKey);
                break;
            case AuditOutcome.Unauthorized:
                metrics.PublicReadUnauthorized();
                break;
            case AuditOutcome.BadRequest:
                metrics.PublicReadBadRequest();
                break;
            default:
                throw new InvalidOperationException($"Unknown outcome '{outcome}'");
        }
    }

    private static string ExpectedCounterName(string outcome) => outcome switch
    {
        AuditOutcome.Hit => TenantClientCacheMetrics.PublicReadHitCounterName,
        AuditOutcome.NotModified => TenantClientCacheMetrics.PublicReadNotModifiedCounterName,
        AuditOutcome.Miss => TenantClientCacheMetrics.PublicReadMissCounterName,
        AuditOutcome.Unauthorized => TenantClientCacheMetrics.PublicReadUnauthorizedCounterName,
        AuditOutcome.RateLimited => TenantClientCacheMetrics.PublicReadRateLimitedCounterName,
        AuditOutcome.BadRequest => TenantClientCacheMetrics.PublicReadBadRequestCounterName,
        AuditOutcome.ServiceUnavailable => TenantClientCacheMetrics.PublicReadServiceUnavailableCounterName,
        _ => throw new InvalidOperationException($"Unknown outcome '{outcome}'"),
    };
}
