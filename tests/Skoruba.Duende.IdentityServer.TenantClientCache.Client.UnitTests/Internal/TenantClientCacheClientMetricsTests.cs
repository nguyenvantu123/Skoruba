// Feature: tenant-client-cache-public-read, Task 8
//
// Example-based tests for TenantClientCacheClientMetrics. Asserts the
// SDK Meter name, the per-outcome counter set, and the tag policy
// (R11.11: only `outcome` is tagged; `tenantKey` is NEVER tagged).
//
// Validates: Requirements 11.11, 11.12

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

using FluentAssertions;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests.Internal;

public sealed class TenantClientCacheClientMetricsTests
{
    [Fact]
    public void Meter_Has_Distinct_Name_From_Server_Side_Meter()
    {
        // R11.11: SDK meter MUST be different from the server-side meter
        // "TenantClientCache" (parent spec). Mixing the two is forbidden.
        TenantClientCacheClientMetrics.MeterName.Should()
            .Be("Skoruba.Duende.IdentityServer.TenantClientCache.Client");
        TenantClientCacheClientMetrics.MeterName.Should()
            .NotBe("TenantClientCache");
    }

    [Fact]
    public void All_Counter_Helpers_Emit_One_Increment_Without_TenantKey_Tag()
    {
        using var listener = new RecordingClientMeterListener(TenantClientCacheClientMetrics.MeterName);
        using var metrics = new TenantClientCacheClientMetrics();

        metrics.HitLocal();
        metrics.HitRemote();
        metrics.NotModified();
        metrics.Miss();
        metrics.Unauthorized();
        metrics.RateLimited();
        metrics.ServiceUnavailable();
        metrics.TransientFailure();
        metrics.RetryAttempted();

        var byInstrument = listener.Measurements
            .GroupBy(m => m.InstrumentName)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        // Each helper emits exactly one increment.
        byInstrument[TenantClientCacheClientMetrics.HitLocalCounterName].Should().HaveCount(1);
        byInstrument[TenantClientCacheClientMetrics.HitRemoteCounterName].Should().HaveCount(1);
        byInstrument[TenantClientCacheClientMetrics.NotModifiedCounterName].Should().HaveCount(1);
        byInstrument[TenantClientCacheClientMetrics.MissCounterName].Should().HaveCount(1);
        byInstrument[TenantClientCacheClientMetrics.UnauthorizedCounterName].Should().HaveCount(1);
        byInstrument[TenantClientCacheClientMetrics.RateLimitedCounterName].Should().HaveCount(1);
        byInstrument[TenantClientCacheClientMetrics.ServiceUnavailableCounterName].Should().HaveCount(1);
        byInstrument[TenantClientCacheClientMetrics.TransientFailureCounterName].Should().HaveCount(1);
        byInstrument[TenantClientCacheClientMetrics.RetryAttemptedCounterName].Should().HaveCount(1);

        // R11.11 anti-cardinality: NO measurement carries a tenantKey tag.
        foreach (var measurement in listener.Measurements)
        {
            measurement.Tags.Should().NotContainKey("tenantKey",
                "SDK metrics must never tag by tenantKey (R11.11)");
            measurement.Tags.Should().NotContainKey("clientId",
                "SDK metrics must never tag by clientId");
        }
    }

    [Fact]
    public void Counters_NoTenantKey_Tag_Ever()
    {
        // Reinforces the property spelled out above as a separate
        // assertion so a regression points directly at the tag policy
        // requirement.
        using var listener = new RecordingClientMeterListener(TenantClientCacheClientMetrics.MeterName);
        using var metrics = new TenantClientCacheClientMetrics();

        // Drive every counter helper exactly once.
        metrics.HitLocal();
        metrics.HitRemote();
        metrics.NotModified();
        metrics.Miss();
        metrics.Unauthorized();
        metrics.RateLimited();
        metrics.ServiceUnavailable();
        metrics.TransientFailure();
        metrics.RetryAttempted();

        listener.Measurements.Should().NotBeEmpty();
        listener.Measurements.Should().OnlyContain(m =>
            !m.Tags.ContainsKey("tenantKey")
            && !m.Tags.ContainsKey("clientId"),
            "no SDK measurement may carry tenantKey or clientId (R11.11)");
    }

    [Fact]
    public void RecordDuration_Adds_Outcome_Tag_Only()
    {
        using var listener = new RecordingClientMeterListener(TenantClientCacheClientMetrics.MeterName);
        using var metrics = new TenantClientCacheClientMetrics();

        metrics.RecordDuration(123.45, SdkCacheOutcome.Hit);
        metrics.RecordDuration(67.0, SdkCacheOutcome.NotModified);
        metrics.RecordDuration(450.0, SdkCacheOutcome.TransientFailure);

        var histogram = listener.ForInstrument(TenantClientCacheClientMetrics.DurationHistogramName);
        histogram.Should().HaveCount(3);

        // Each measurement carries exactly one tag, namely `outcome`.
        foreach (var record in histogram)
        {
            record.Tags.Should().ContainKey(TenantClientCacheClientMetrics.TagOutcome);
            record.Tags.Should().HaveCount(1,
                "histogram emits ONLY the outcome tag (R11.11)");
        }

        histogram[0].Tags[TenantClientCacheClientMetrics.TagOutcome].Should().Be("hit");
        histogram[0].Value.Should().Be(123.45);

        histogram[1].Tags[TenantClientCacheClientMetrics.TagOutcome].Should().Be("not_modified");
        histogram[2].Tags[TenantClientCacheClientMetrics.TagOutcome].Should().Be("transient_failure");
    }

    [Fact]
    public void FormatOutcome_Uses_SnakeCase_For_Every_Enum_Value()
    {
        // Centralised outcome → tag string mapping. A regression here
        // would silently break every SDK dashboard.
        TenantClientCacheClientMetrics.FormatOutcome(SdkCacheOutcome.Hit).Should().Be("hit");
        TenantClientCacheClientMetrics.FormatOutcome(SdkCacheOutcome.Miss).Should().Be("miss");
        TenantClientCacheClientMetrics.FormatOutcome(SdkCacheOutcome.NotModified).Should().Be("not_modified");
        TenantClientCacheClientMetrics.FormatOutcome(SdkCacheOutcome.NotFound).Should().Be("not_found");
        TenantClientCacheClientMetrics.FormatOutcome(SdkCacheOutcome.Unauthorized).Should().Be("unauthorized");
        TenantClientCacheClientMetrics.FormatOutcome(SdkCacheOutcome.RateLimited).Should().Be("rate_limited");
        TenantClientCacheClientMetrics.FormatOutcome(SdkCacheOutcome.ServiceUnavailable).Should().Be("service_unavailable");
        TenantClientCacheClientMetrics.FormatOutcome(SdkCacheOutcome.TransientFailure).Should().Be("transient_failure");
    }

    // ===== Local helper: minimal MeterListener wrapper =================

    /// <summary>
    /// One observation captured by the listener.
    /// </summary>
    private sealed record RecordedClientMeasurement(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);

    /// <summary>
    /// SDK-test-local mirror of the parent spec's RecordingMeterListener.
    /// Inlined here to keep the SDK test project self-contained without
    /// taking a project reference on the parent spec's helpers folder.
    /// </summary>
    private sealed class RecordingClientMeterListener : IDisposable
    {
        private readonly string _meterName;
        private readonly MeterListener _listener;
        private readonly object _gate = new();
        private readonly List<RecordedClientMeasurement> _measurements = new();

        public RecordingClientMeterListener(string meterName)
        {
            _meterName = meterName;
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == _meterName)
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.SetMeasurementEventCallback<double>(OnMeasurement);
            _listener.Start();
        }

        public IReadOnlyList<RecordedClientMeasurement> Measurements
        {
            get
            {
                lock (_gate)
                    return _measurements.ToArray();
            }
        }

        public IReadOnlyList<RecordedClientMeasurement> ForInstrument(string name)
        {
            lock (_gate)
                return _measurements.Where(m => m.InstrumentName == name).ToArray();
        }

        public void Dispose() => _listener.Dispose();

        private void OnMeasurement<T>(
            Instrument instrument,
            T measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
            where T : struct
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var t in tags) dict[t.Key] = t.Value;

            var record = new RecordedClientMeasurement(
                instrument.Name,
                Convert.ToDouble(measurement),
                dict);
            lock (_gate) _measurements.Add(record);
        }
    }
}
