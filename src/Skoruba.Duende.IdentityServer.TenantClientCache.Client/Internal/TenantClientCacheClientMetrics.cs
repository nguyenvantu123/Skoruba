// Feature: tenant-client-cache-public-read, Task 8
//
// SDK-side System.Diagnostics.Metrics surface (R11.11). DELIBERATELY
// separate from the server-side Meter "TenantClientCache" (parent spec
// tenant-client-cache-expansion) because the SDK runs in a different
// process with a different cardinality budget. Mixing the two meters
// would let consumer cardinality bleed into operator dashboards.
//
// Tag policy: helper methods emit ONLY the `outcome` tag. tenantKey is
// NEVER tagged (R11.11) — consumers wanting per-tenant breakdown can
// dimension via structured logs.
//
// Validates: Requirements 11.11, 11.12

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;

/// <summary>
/// Owns the <see cref="Meter"/> and instruments emitted by the SDK
/// consumer. Registered as a singleton so all
/// <see cref="ITenantClientCacheClient"/> instances share the same
/// instrument identities (R11.11).
/// </summary>
internal sealed class TenantClientCacheClientMetrics : IDisposable
{
    /// <summary>Public meter name. Pinned for back-compat (Task 12 runbook).</summary>
    public const string MeterName = "Skoruba.Duende.IdentityServer.TenantClientCache.Client";

    /// <summary>Public meter version. Bump only on a breaking instrument change.</summary>
    public const string MeterVersion = "1.0";

    // ----- Counter names (kept as constants so tests can assert by name).

    public const string HitLocalCounterName = "client.read.hit_local";
    public const string HitRemoteCounterName = "client.read.hit_remote";
    public const string NotModifiedCounterName = "client.read.not_modified";
    public const string MissCounterName = "client.read.miss";
    public const string UnauthorizedCounterName = "client.read.unauthorized";
    public const string RateLimitedCounterName = "client.read.rate_limited";
    public const string ServiceUnavailableCounterName = "client.read.service_unavailable";
    public const string TransientFailureCounterName = "client.read.transient_failure";
    public const string RetryAttemptedCounterName = "client.read.retry_attempted";
    public const string DurationHistogramName = "client.read.duration_ms";

    /// <summary>Tag key: outcome (the only tag emitted by the SDK; R11.11).</summary>
    public const string TagOutcome = "outcome";

    private readonly Meter _meter;
    private readonly Counter<long> _hitLocal;
    private readonly Counter<long> _hitRemote;
    private readonly Counter<long> _notModified;
    private readonly Counter<long> _miss;
    private readonly Counter<long> _unauthorized;
    private readonly Counter<long> _rateLimited;
    private readonly Counter<long> _serviceUnavailable;
    private readonly Counter<long> _transientFailure;
    private readonly Counter<long> _retryAttempted;
    private readonly Histogram<double> _duration;

    public TenantClientCacheClientMetrics()
    {
        _meter = new Meter(MeterName, MeterVersion);

        _hitLocal           = _meter.CreateCounter<long>(HitLocalCounterName);
        _hitRemote          = _meter.CreateCounter<long>(HitRemoteCounterName);
        _notModified        = _meter.CreateCounter<long>(NotModifiedCounterName);
        _miss               = _meter.CreateCounter<long>(MissCounterName);
        _unauthorized       = _meter.CreateCounter<long>(UnauthorizedCounterName);
        _rateLimited        = _meter.CreateCounter<long>(RateLimitedCounterName);
        _serviceUnavailable = _meter.CreateCounter<long>(ServiceUnavailableCounterName);
        _transientFailure   = _meter.CreateCounter<long>(TransientFailureCounterName);
        _retryAttempted     = _meter.CreateCounter<long>(RetryAttemptedCounterName);
        _duration           = _meter.CreateHistogram<double>(DurationHistogramName);
    }

    // R11.11: helper methods emit no tags on counters. Per-outcome
    // counters self-identify by name; the histogram carries an `outcome`
    // tag so a single time-series can be sliced.

    /// <summary>SDK in-memory cache hit (R11.7). No HTTP traffic was issued.</summary>
    public void HitLocal() => _hitLocal.Add(1);

    /// <summary>Server returned 200 OK with a fresh body (cache populated/updated).</summary>
    public void HitRemote() => _hitRemote.Add(1);

    /// <summary>Server returned 304 Not Modified (R11.9).</summary>
    public void NotModified() => _notModified.Add(1);

    /// <summary>Server returned 200 with a fresh body (used as the
    /// "remote miss" signal; mirrors the design's <c>Outcome=Miss</c> classification).</summary>
    public void Miss() => _miss.Add(1);

    /// <summary>Server returned 401 Unauthorized (R3.1, R3.2).</summary>
    public void Unauthorized() => _unauthorized.Add(1);

    /// <summary>Server returned 429 Too Many Requests (R4.5).</summary>
    public void RateLimited() => _rateLimited.Add(1);

    /// <summary>Server returned 503 Service Unavailable (R7.4, R7.5).</summary>
    public void ServiceUnavailable() => _serviceUnavailable.Add(1);

    /// <summary>5xx exhausted retries OR an unknown 4xx response was folded into the fail-soft bucket.</summary>
    public void TransientFailure() => _transientFailure.Add(1);

    /// <summary>An additional retry attempt was scheduled (R11.1).</summary>
    public void RetryAttempted() => _retryAttempted.Add(1);

    /// <summary>
    /// Record wall-clock duration of one SDK call, tagged with the
    /// terminal outcome.
    /// </summary>
    public void RecordDuration(double ms, SdkCacheOutcome outcome)
    {
        var tag = new KeyValuePair<string, object?>(TagOutcome, FormatOutcome(outcome));
        _duration.Record(ms, tag);
    }

    /// <summary>
    /// Format <see cref="SdkCacheOutcome"/> as the snake_case string used
    /// in dashboards and structured logs. Centralised so logs and metric
    /// tags agree exactly.
    /// </summary>
    public static string FormatOutcome(SdkCacheOutcome outcome) => outcome switch
    {
        SdkCacheOutcome.Hit                 => "hit",
        SdkCacheOutcome.Miss                => "miss",
        SdkCacheOutcome.NotModified         => "not_modified",
        SdkCacheOutcome.NotFound            => "not_found",
        SdkCacheOutcome.Unauthorized        => "unauthorized",
        SdkCacheOutcome.RateLimited         => "rate_limited",
        SdkCacheOutcome.ServiceUnavailable  => "service_unavailable",
        SdkCacheOutcome.TransientFailure    => "transient_failure",
        _ => outcome.ToString()
    };

    public void Dispose() => _meter.Dispose();
}
