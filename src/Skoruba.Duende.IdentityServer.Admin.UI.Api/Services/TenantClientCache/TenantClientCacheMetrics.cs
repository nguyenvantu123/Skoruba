// Feature: tenant-client-cache-expansion, Task 5
// Feature: tenant-client-cache-public-read, Task 4
//
// Singleton holding the System.Diagnostics.Metrics primitives for the
// tenant-client cache feature. Centralising the Meter ensures every
// counter / histogram / gauge is created exactly once and that the tag
// whitelist is enforced in one place.
//
// Tag set for the parent-spec instruments (R16.3) is restricted to
// {tenantKey, outcome}. ClientId is deliberately NOT a tag because it is
// high-cardinality and we want dashboards / alerts to remain bounded
// per tenant.
//
// Public-read endpoint instruments (feature
// `tenant-client-cache-public-read`) are appended on the SAME meter
// (R8.3 — no second meter). Tag policy:
//   * Hit / NotModified / Miss / RateLimited / ServiceUnavailable
//     counters are tagged with `tenantKey` ONLY (R8.4).
//   * Unauthorized / BadRequest counters are UN-tagged
//     (R8.4 anti-enumeration).
//   * The duration histogram is tagged with `outcome` AND, where
//     applicable per R8.4, `tenantKey` (R8.5).
//
// Validates: Requirements 16.1, 16.2, 16.3, 16.4 (parent),
//            Requirements 8.1, 8.2, 8.3, 8.4, 8.5 (public-read).

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

/// <summary>
/// Owns the <see cref="Meter"/> instance and exposes typed Record helpers
/// that enforce the tag whitelist. The class is registered as a singleton
/// so all callers (cache service + background sweep, Task 9, AND the
/// public-read pipeline of feature <c>tenant-client-cache-public-read</c>)
/// share the same instrument identities.
/// </summary>
public sealed class TenantClientCacheMetrics : IDisposable
{
    /// <summary>Public meter name. Pinned for back-compat (Task 12 runbook).</summary>
    public const string MeterName = "TenantClientCache";

    /// <summary>Public meter version. Bump this only on a breaking instrument change.</summary>
    public const string MeterVersion = "1.0";

    /// <summary>Counter name: read returned a fresh hit.</summary>
    public const string ReadHitCounterName = "tenant_client_cache.read.hit";

    /// <summary>Counter name: read returned no usable envelope.</summary>
    public const string ReadMissCounterName = "tenant_client_cache.read.miss";

    /// <summary>Counter name: write succeeded (Cache_Outcome.WriteSucceeded).</summary>
    public const string WriteSuccessCounterName = "tenant_client_cache.write.success";

    /// <summary>Counter name: write failed transiently (fail-soft, never throws).</summary>
    public const string WriteFailureCounterName = "tenant_client_cache.write.failure";

    /// <summary>Counter name: invalidate succeeded.</summary>
    public const string InvalidateSuccessCounterName = "tenant_client_cache.invalidate.success";

    /// <summary>Counter name: invalidate failed transiently.</summary>
    public const string InvalidateFailureCounterName = "tenant_client_cache.invalidate.failure";

    /// <summary>Histogram name: background sweep wall-clock duration.</summary>
    public const string RefreshSweepDurationHistogramName = "tenant_client_cache.refresh.sweep.duration_ms";

    /// <summary>Observable gauge name: unix-seconds of last completed sweep.</summary>
    public const string RefreshLastCompletedAtGaugeName = "tenant_client_cache.refresh.last_completed_at";

    // ===== Public_Read_Endpoint instruments (feature: tenant-client-cache-public-read) =====
    // Reuse the existing Meter "TenantClientCache" per R8.3 — we deliberately
    // do NOT introduce a second Meter, so dashboards remain a single coherent
    // surface.

    /// <summary>Counter name: successful 200 response (R8.3).</summary>
    public const string PublicReadHitCounterName = "tenant_client_cache.public_read.hit";

    /// <summary>Counter name: 304 Not Modified response (R8.3).</summary>
    public const string PublicReadNotModifiedCounterName = "tenant_client_cache.public_read.not_modified";

    /// <summary>Counter name: 404 snapshot_not_found response (R8.3).</summary>
    public const string PublicReadMissCounterName = "tenant_client_cache.public_read.miss";

    /// <summary>Counter name: 401 unauthorized response (R8.3, no tenantKey tag — R8.4).</summary>
    public const string PublicReadUnauthorizedCounterName = "tenant_client_cache.public_read.unauthorized";

    /// <summary>Counter name: 429 rate_limit_exceeded response (R8.3).</summary>
    public const string PublicReadRateLimitedCounterName = "tenant_client_cache.public_read.rate_limited";

    /// <summary>Counter name: 400 bad-request response (R8.3, no tenantKey tag — R8.4).</summary>
    public const string PublicReadBadRequestCounterName = "tenant_client_cache.public_read.bad_request";

    /// <summary>Counter name: 503 snapshot_unavailable / pipeline_disabled response (R8.3).</summary>
    public const string PublicReadServiceUnavailableCounterName = "tenant_client_cache.public_read.service_unavailable";

    /// <summary>Histogram name: per-request wall-clock duration tagged by outcome (R8.5).</summary>
    public const string PublicReadDurationHistogramName = "tenant_client_cache.public_read.duration_ms";

    /// <summary>Tag key: terminal outcome label used on the public-read instruments.</summary>
    public const string TagPublicReadOutcome = "outcome";

    /// <summary>Tag key: normalized tenant key.</summary>
    public const string TagTenantKey = "tenantKey";

    /// <summary>Tag key: <see cref="Cache_Outcome"/> formatted as snake_case.</summary>
    public const string TagOutcome = "outcome";

    private static readonly HashSet<string> AllowedTags =
        new(StringComparer.Ordinal) { TagTenantKey, TagOutcome };

    private readonly Meter _meter;
    private readonly Counter<long> _readHit;
    private readonly Counter<long> _readMiss;
    private readonly Counter<long> _writeSuccess;
    private readonly Counter<long> _writeFailure;
    private readonly Counter<long> _invalidateSuccess;
    private readonly Counter<long> _invalidateFailure;
    private readonly Histogram<double> _refreshSweepDuration;

    // ===== Public_Read_Endpoint instruments (feature: tenant-client-cache-public-read) =====
    private readonly Counter<long> _publicReadHit;
    private readonly Counter<long> _publicReadNotModified;
    private readonly Counter<long> _publicReadMiss;
    private readonly Counter<long> _publicReadUnauthorized;
    private readonly Counter<long> _publicReadRateLimited;
    private readonly Counter<long> _publicReadBadRequest;
    private readonly Counter<long> _publicReadServiceUnavailable;
    private readonly Histogram<double> _publicReadDuration;

#pragma warning disable IDE0052 // Observable instruments are kept alive by the Meter; the field is intentionally read-only.
    private readonly ObservableGauge<long> _refreshLastCompletedAt;
#pragma warning restore IDE0052

    private long _lastSweepCompletedAtUnixSeconds;

    public TenantClientCacheMetrics()
    {
        _meter = new Meter(MeterName, MeterVersion);

        _readHit = _meter.CreateCounter<long>(ReadHitCounterName);
        _readMiss = _meter.CreateCounter<long>(ReadMissCounterName);
        _writeSuccess = _meter.CreateCounter<long>(WriteSuccessCounterName);
        _writeFailure = _meter.CreateCounter<long>(WriteFailureCounterName);
        _invalidateSuccess = _meter.CreateCounter<long>(InvalidateSuccessCounterName);
        _invalidateFailure = _meter.CreateCounter<long>(InvalidateFailureCounterName);
        _refreshSweepDuration = _meter.CreateHistogram<double>(RefreshSweepDurationHistogramName);

        // ===== Public_Read_Endpoint instruments (feature: tenant-client-cache-public-read) =====
        // Reuse the existing Meter instance per R8.3. Tag policy is enforced
        // inside the helper methods below (Unauthorized / BadRequest emit no
        // tenantKey tag — R8.4 anti-enumeration).
        _publicReadHit                = _meter.CreateCounter<long>(PublicReadHitCounterName);
        _publicReadNotModified        = _meter.CreateCounter<long>(PublicReadNotModifiedCounterName);
        _publicReadMiss               = _meter.CreateCounter<long>(PublicReadMissCounterName);
        _publicReadUnauthorized       = _meter.CreateCounter<long>(PublicReadUnauthorizedCounterName);
        _publicReadRateLimited        = _meter.CreateCounter<long>(PublicReadRateLimitedCounterName);
        _publicReadBadRequest         = _meter.CreateCounter<long>(PublicReadBadRequestCounterName);
        _publicReadServiceUnavailable = _meter.CreateCounter<long>(PublicReadServiceUnavailableCounterName);
        _publicReadDuration           = _meter.CreateHistogram<double>(PublicReadDurationHistogramName);

        // Background sweep (Task 9) calls SetLastSweepCompletedAt to keep
        // the gauge fresh. Until then the gauge reports 0.
        _refreshLastCompletedAt = _meter.CreateObservableGauge(
            RefreshLastCompletedAtGaugeName,
            () => System.Threading.Interlocked.Read(ref _lastSweepCompletedAtUnixSeconds));
    }

    /// <summary>
    /// Update the observable gauge to <paramref name="unixSeconds"/>.
    /// Used by <c>TenantClientCacheRefreshService</c> at end of sweep.
    /// </summary>
    public void SetLastSweepCompletedAt(long unixSeconds)
    {
        System.Threading.Interlocked.Exchange(ref _lastSweepCompletedAtUnixSeconds, unixSeconds);
    }

    /// <summary>Record a read outcome. Increments hit OR miss counter.</summary>
    public void RecordRead(string tenantKey, Cache_Outcome outcome)
    {
        var tags = BuildTags(tenantKey, outcome);
        switch (outcome)
        {
            case Cache_Outcome.Hit:
                _readHit.Add(1, tags);
                break;
            case Cache_Outcome.Miss:
            case Cache_Outcome.Stale:
                _readMiss.Add(1, tags);
                break;
            default:
                // Defensive: a non-read outcome was passed. Don't increment;
                // log nothing here (the caller already logs).
                break;
        }
    }

    /// <summary>Record a write outcome. Increments success OR failure counter.</summary>
    public void RecordWrite(string tenantKey, Cache_Outcome outcome)
    {
        var tags = BuildTags(tenantKey, outcome);
        switch (outcome)
        {
            case Cache_Outcome.WriteSucceeded:
                _writeSuccess.Add(1, tags);
                break;
            case Cache_Outcome.WriteFailedTransient:
                _writeFailure.Add(1, tags);
                break;
            // WriteSkippedDisabled is intentionally NOT counted as a
            // success or a failure — it represents a configured no-op and
            // dashboards should not interpret it as "load".
        }
    }

    /// <summary>Record an invalidate outcome. Increments success OR failure counter.</summary>
    public void RecordInvalidate(string tenantKey, Cache_Outcome outcome)
    {
        var tags = BuildTags(tenantKey, outcome);
        switch (outcome)
        {
            case Cache_Outcome.InvalidateSucceeded:
                _invalidateSuccess.Add(1, tags);
                break;
            case Cache_Outcome.InvalidateFailedTransient:
                _invalidateFailure.Add(1, tags);
                break;
        }
    }

    /// <summary>Record a sweep duration measurement on the histogram.</summary>
    public void RecordRefreshSweepDuration(double durationMs)
    {
        // Background sweep is global, not tenant-scoped — a histogram
        // measurement carries no tenantKey. We still feed an empty tag
        // set so dashboards can rely on the same shape.
        _refreshSweepDuration.Record(durationMs);
    }

    // ===== Public_Read_Endpoint helpers (feature: tenant-client-cache-public-read) =====
    //
    // Tag policy is enforced at the call site (R8.4):
    //
    //   * Hit / NotModified / Miss / RateLimited / ServiceUnavailable
    //     → counter tagged with `tenantKey`; the duration histogram also
    //       carries the `tenantKey` tag plus an `outcome` tag (R8.5).
    //   * Unauthorized / BadRequest
    //     → counter UN-tagged (R8.4 anti-enumeration); no histogram entry
    //       is recorded because the request never reaches a stable
    //       wall-clock measurement boundary in those filters.
    //
    // No helper ever tags with `clientId` — cardinality budget mirrors the
    // parent spec R16.3 invariant.

    /// <summary>
    /// Hit outcome (200) — increments
    /// <c>tenant_client_cache.public_read.hit</c> tagged with
    /// <paramref name="tenantKey"/> AND records a duration sample on
    /// <c>tenant_client_cache.public_read.duration_ms</c> with tags
    /// {<c>outcome</c>=<c>Hit</c>, <c>tenantKey</c>}. R8.3, R8.4, R8.5.
    /// </summary>
    public void PublicReadHit(string tenantKey, double durationMs)
        => RecordPublicReadOutcome(_publicReadHit, tenantKey, "Hit", durationMs);

    /// <summary>
    /// NotModified outcome (304) — increments
    /// <c>tenant_client_cache.public_read.not_modified</c> tagged with
    /// <paramref name="tenantKey"/> AND records a duration sample. R8.3, R8.4, R8.5.
    /// </summary>
    public void PublicReadNotModified(string tenantKey, double durationMs)
        => RecordPublicReadOutcome(_publicReadNotModified, tenantKey, "NotModified", durationMs);

    /// <summary>
    /// Miss outcome (404) — increments
    /// <c>tenant_client_cache.public_read.miss</c> tagged with
    /// <paramref name="tenantKey"/> AND records a duration sample. R8.3, R8.4, R8.5.
    /// </summary>
    public void PublicReadMiss(string tenantKey, double durationMs)
        => RecordPublicReadOutcome(_publicReadMiss, tenantKey, "Miss", durationMs);

    /// <summary>
    /// RateLimited outcome (429) — increments
    /// <c>tenant_client_cache.public_read.rate_limited</c> tagged with
    /// <paramref name="tenantKey"/> AND records a duration sample. R8.3, R8.4, R8.5.
    /// </summary>
    public void PublicReadRateLimited(string tenantKey, double durationMs)
        => RecordPublicReadOutcome(_publicReadRateLimited, tenantKey, "RateLimited", durationMs);

    /// <summary>
    /// ServiceUnavailable outcome (503) — increments
    /// <c>tenant_client_cache.public_read.service_unavailable</c> tagged
    /// with <paramref name="tenantKey"/>. The duration histogram is NOT
    /// updated here because the unhandled-exception path (the canonical
    /// caller — <see cref="PublicTenantClients.PublicReadExceptionFilter"/>)
    /// does not own a stable wall-clock measurement boundary; the
    /// controller measures Hit / NotModified / Miss when it does.
    /// R8.3, R8.4.
    /// </summary>
    public void PublicReadServiceUnavailable(string tenantKey)
    {
        var normalized = tenantKey ?? string.Empty;
        _publicReadServiceUnavailable.Add(
            1,
            new KeyValuePair<string, object?>(TagTenantKey, normalized));
    }

    /// <summary>
    /// Unauthorized outcome (401) — increments
    /// <c>tenant_client_cache.public_read.unauthorized</c> with NO tags.
    /// R8.4 anti-enumeration: <c>tenantKey</c> MUST NOT be tagged so an
    /// attacker cannot enumerate registered tenants by scraping
    /// per-counter time series.
    /// </summary>
    public void PublicReadUnauthorized()
    {
        _publicReadUnauthorized.Add(1);
    }

    /// <summary>
    /// BadRequest outcome (400) — increments
    /// <c>tenant_client_cache.public_read.bad_request</c> with NO tags.
    /// R8.4 anti-enumeration: <c>tenantKey</c> MUST NOT be tagged so an
    /// attacker cannot enumerate the tenant set via path-validation
    /// failure metrics.
    /// </summary>
    public void PublicReadBadRequest()
    {
        _publicReadBadRequest.Add(1);
    }

    /// <summary>
    /// Centralised counter-+-histogram helper used by all public-read
    /// outcomes that carry a <paramref name="tenantKey"/>. The counter is
    /// tagged with <c>tenantKey</c> ONLY (R8.4); the duration histogram
    /// is tagged with <c>outcome</c> AND <c>tenantKey</c> (R8.5).
    /// <paramref name="clientId"/> is never tagged on either instrument
    /// (cardinality protection, mirrors parent spec R16.3).
    /// </summary>
    private void RecordPublicReadOutcome(
        Counter<long> counter,
        string tenantKey,
        string outcome,
        double durationMs)
    {
        var normalized = tenantKey ?? string.Empty;

        counter.Add(
            1,
            new KeyValuePair<string, object?>(TagTenantKey, normalized));

        _publicReadDuration.Record(
            durationMs,
            new KeyValuePair<string, object?>(TagPublicReadOutcome, outcome),
            new KeyValuePair<string, object?>(TagTenantKey, normalized));
    }

    /// <summary>
    /// Build a <see cref="KeyValuePair{TKey,TValue}"/> array suitable for
    /// <c>Counter.Add(value, tags)</c>. In DEBUG, asserts that no caller
    /// has passed a tag outside the whitelist.
    /// </summary>
    private static KeyValuePair<string, object?>[] BuildTags(string tenantKey, Cache_Outcome outcome)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>(TagTenantKey, tenantKey ?? string.Empty),
            new KeyValuePair<string, object?>(TagOutcome, FormatOutcome(outcome)),
        };

#if DEBUG
        // Tripwire: if a future caller adds extra tags they MUST be on the
        // whitelist. The whitelist itself is asserted here so the
        // invariant fails loudly in unit tests.
        foreach (var kv in tags)
        {
            if (!AllowedTags.Contains(kv.Key))
            {
                throw new InvalidOperationException(
                    $"TenantClientCacheMetrics: tag '{kv.Key}' is not on the whitelist {{tenantKey, outcome}}.");
            }
        }
#endif

        return tags;
    }

    /// <summary>
    /// Format <see cref="Cache_Outcome"/> as the snake_case string used in
    /// dashboards and structured logs. Centralised so log message and
    /// metric tag agree exactly.
    /// </summary>
    public static string FormatOutcome(Cache_Outcome outcome) => outcome switch
    {
        Cache_Outcome.Hit => "hit",
        Cache_Outcome.Miss => "miss",
        Cache_Outcome.Stale => "stale",
        Cache_Outcome.WriteSucceeded => "write_succeeded",
        Cache_Outcome.WriteSkippedDisabled => "write_skipped_disabled",
        Cache_Outcome.WriteFailedTransient => "write_failed_transient",
        Cache_Outcome.InvalidateSucceeded => "invalidate_succeeded",
        Cache_Outcome.InvalidateFailedTransient => "invalidate_failed_transient",
        _ => outcome.ToString(),
    };

    public void Dispose() => _meter.Dispose();
}
