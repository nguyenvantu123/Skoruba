// Feature: tenant-client-cache-public-read, Task 9
//
// SDK consumer implementation. See design.md section
// "TenantClientCacheClient implementation skeleton" for the verbatim
// reference. Constructor is `internal sealed` so the only entry point
// is the public <see cref="ITenantClientCacheClient"/> interface; DI
// wiring lives in
// <see cref="TenantClientCacheClientServiceCollectionExtensions"/>.
//
// Validates: Requirements 10.2, 10.3, 10.4, 10.6, 10.10, 10.11,
//            11.4, 11.5, 11.6, 11.7, 11.8, 11.9, 11.10, 11.12

#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client;

internal sealed class TenantClientCacheClient : ITenantClientCacheClient
{
    private const string ApiKeyHeader = "X-Tenant-Api-Key";
    private const string SnapshotLastWriteHeader = "X-Snapshot-Last-Write-Utc";
    private const string SnapshotVersionHeader = "X-Snapshot-Version";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptionsMonitor<TenantClientCacheClientOptions> _options;
    private readonly ILogger<TenantClientCacheClient> _logger;
    private readonly TenantClientCacheClientMetrics _metrics;
    private readonly TenantClientCacheClientRetryPolicy _retry;

    public TenantClientCacheClient(
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        IOptionsMonitor<TenantClientCacheClientOptions> options,
        ILogger<TenantClientCacheClient> logger,
        TenantClientCacheClientMetrics metrics,
        TenantClientCacheClientRetryPolicy retry)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
    }

    /// <inheritdoc />
    public Task<TenantClientSnapshotResult> GetClientAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken = default)
        => GetClientAsync(tenantKey, clientId, ifNoneMatch: null, cancellationToken);

    /// <inheritdoc />
    public async Task<TenantClientSnapshotResult> GetClientAsync(
        string tenantKey,
        string clientId,
        string? ifNoneMatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantKey);
        ArgumentNullException.ThrowIfNull(clientId);

        var sw = ValueStopwatch.StartNew();
        var nt = tenantKey.Trim().ToLowerInvariant();
        var nc = clientId.Trim();
        var opts = _options.CurrentValue;
        var cacheKey = (nt, nc);

        // R11.7 + R11.8 — local cache lookup; skipped when caller supplies
        // an explicit If-None-Match header (force-revalidate path).
        if (opts.EnableInMemoryCaching
            && ifNoneMatch is null
            && _memoryCache.TryGetValue<TenantClientCacheClientCacheEntry>(cacheKey, out var hit)
            && hit is not null)
        {
            _metrics.HitLocal();
            _metrics.RecordDuration(sw.GetElapsedMs(), SdkCacheOutcome.Hit);
            _logger.LogInformation(
                "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} source=local durationMs={DurationMs}",
                "TenantClientCacheClient.HitLocal", nt, nc, "Hit", sw.GetElapsedMs());
            return new TenantClientSnapshotResult(
                hit.Snapshot, hit.Etag, hit.LastWriteUtc, hit.Version,
                SdkCacheOutcome.Hit, RetryAfter: null);
        }

        // R10.6 — never instantiate HttpClient directly; defer to the
        // factory so handler lifetimes are managed for us.
        var http = _httpClientFactory.CreateClient(
            TenantClientCacheClientServiceCollectionExtensions.HttpClientName);

        // R11.9 — auto-revalidate by re-using a cached ETag when the
        // local entry has expired (TTL elapsed, entry evicted) but a
        // prior copy is still around.
        var revalidationEtag = ifNoneMatch
            ?? (_memoryCache.TryGetValue<TenantClientCacheClientCacheEntry>(cacheKey, out var stale) && stale is not null
                ? stale.Etag
                : null);

        var attempt = 0;
        Exception? lastException = null;
        HttpResponseMessage? response = null;
        var requestPath = BuildRequestPath(nt, nc);

        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, requestPath);
            req.Headers.TryAddWithoutValidation(ApiKeyHeader, opts.ApiKey);
            if (!string.IsNullOrEmpty(revalidationEtag))
                req.Headers.TryAddWithoutValidation("If-None-Match", revalidationEtag);

            try
            {
                response = await http.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (_retry.ShouldRetry(response.StatusCode, attempt, opts.MaxRetryAttempts))
                {
                    _metrics.RetryAttempted();
                    var status = response.StatusCode;
                    response.Dispose();
                    response = null;

                    var delay = _retry.NextDelay(attempt, opts.RetryBaseDelay);
                    _logger.LogDebug(
                        "{EventType} tenantKey={TenantKey} clientId={ClientId} httpStatus={HttpStatus} retryAttempt={RetryAttempt} delayMs={DelayMs}",
                        "TenantClientCacheClient.RetryScheduled", nt, nc, (int)status, attempt + 1, delay.TotalMilliseconds);

                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    attempt++;
                    continue;
                }
                break; // success or non-retriable
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // R11.5 — caller-driven cancellation must surface to caller.
                throw;
            }
            catch (Exception ex) when (TenantClientCacheClientRetryPolicy.IsTransientNetworkException(ex))
            {
                lastException = ex;
                if (attempt >= opts.MaxRetryAttempts)
                    break;

                _metrics.RetryAttempted();
                var delay = _retry.NextDelay(attempt, opts.RetryBaseDelay);
                _logger.LogDebug(ex,
                    "{EventType} tenantKey={TenantKey} clientId={ClientId} retryAttempt={RetryAttempt} delayMs={DelayMs}",
                    "TenantClientCacheClient.TransientFailure", nt, nc, attempt + 1, delay.TotalMilliseconds);

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
        }

        try
        {
            return await TranslateAsync(
                response, lastException, nt, nc, cacheKey, opts, sw, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// Decode the HTTP response (or terminal exception) into the public
    /// <see cref="TenantClientSnapshotResult"/> shape.
    /// </summary>
    private async Task<TenantClientSnapshotResult> TranslateAsync(
        HttpResponseMessage? resp,
        Exception? lastException,
        string nt,
        string nc,
        (string, string) key,
        TenantClientCacheClientOptions opts,
        ValueStopwatch sw,
        CancellationToken cancellationToken)
    {
        if (resp is null)
        {
            _metrics.TransientFailure();
            _metrics.RecordDuration(sw.GetElapsedMs(), SdkCacheOutcome.TransientFailure);
            _logger.LogWarning(lastException,
                "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} durationMs={DurationMs}",
                "TenantClientCacheClient.TransientFailure", nt, nc, "TransientFailure", sw.GetElapsedMs());
            return new TenantClientSnapshotResult(
                Snapshot: null, Etag: null, LastWriteUtc: null, Version: null,
                Outcome: SdkCacheOutcome.TransientFailure, RetryAfter: null);
        }

        var status = (int)resp.StatusCode;
        switch (status)
        {
            case 200:
            {
                var snapshot = await resp.Content
                    .ReadFromJsonAsync<PublicClientSnapshot>(cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "TenantClientCacheClient: 200 response body deserialised to null.");

                var etag = resp.Headers.ETag?.Tag;
                var lastWrite = TryParseDate(resp.Headers, SnapshotLastWriteHeader);
                var version = TryParseInt(resp.Headers, SnapshotVersionHeader);
                var maxAge = resp.Headers.CacheControl?.MaxAge ?? TimeSpan.Zero;

                // R11.6 — TTL = min(server max-age, MaxClientCacheTtl).
                // TTL=0 disables local caching for this entry.
                var ttl = TimeSpan.FromTicks(Math.Min(maxAge.Ticks, opts.MaxClientCacheTtl.Ticks));
                if (opts.EnableInMemoryCaching && ttl > TimeSpan.Zero)
                {
                    _memoryCache.Set(
                        key,
                        new TenantClientCacheClientCacheEntry(snapshot, etag, lastWrite, version),
                        ttl);
                }

                _metrics.HitRemote();
                _metrics.RecordDuration(sw.GetElapsedMs(), SdkCacheOutcome.Miss);
                _logger.LogInformation(
                    "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} httpStatus={HttpStatus} ttlSeconds={TtlSeconds} durationMs={DurationMs}",
                    "TenantClientCacheClient.Miss", nt, nc, "Miss", status,
                    ttl.TotalSeconds, sw.GetElapsedMs());

                return new TenantClientSnapshotResult(
                    snapshot, etag, lastWrite, version, SdkCacheOutcome.Miss, RetryAfter: null);
            }

            case 304:
            {
                // R11.9 — surface previously cached snapshot if any.
                _memoryCache.TryGetValue<TenantClientCacheClientCacheEntry>(key, out var cached);

                var maxAge = resp.Headers.CacheControl?.MaxAge ?? TimeSpan.Zero;
                if (cached is not null && opts.EnableInMemoryCaching && maxAge > TimeSpan.Zero)
                {
                    var refreshTtl = TimeSpan.FromTicks(
                        Math.Min(maxAge.Ticks, opts.MaxClientCacheTtl.Ticks));
                    if (refreshTtl > TimeSpan.Zero)
                        _memoryCache.Set(key, cached, refreshTtl);
                }

                _metrics.NotModified();
                _metrics.RecordDuration(sw.GetElapsedMs(), SdkCacheOutcome.NotModified);
                _logger.LogInformation(
                    "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} httpStatus={HttpStatus} durationMs={DurationMs}",
                    "TenantClientCacheClient.NotModified", nt, nc, "NotModified", status, sw.GetElapsedMs());

                return new TenantClientSnapshotResult(
                    cached?.Snapshot, cached?.Etag, cached?.LastWriteUtc, cached?.Version,
                    SdkCacheOutcome.NotModified, RetryAfter: null);
            }

            case 401:
                // R3.1 / R3.2 — clear cache for this key so we don't
                // leak a stale snapshot after a key revocation.
                _memoryCache.Remove(key);
                _metrics.Unauthorized();
                _metrics.RecordDuration(sw.GetElapsedMs(), SdkCacheOutcome.Unauthorized);
                _logger.LogWarning(
                    "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} httpStatus={HttpStatus} durationMs={DurationMs}",
                    "TenantClientCacheClient.Unauthorized", nt, nc, "Unauthorized", status, sw.GetElapsedMs());
                return Empty(SdkCacheOutcome.Unauthorized, resp);

            case 404:
                _memoryCache.Remove(key);
                _metrics.Miss();
                _metrics.RecordDuration(sw.GetElapsedMs(), SdkCacheOutcome.NotFound);
                _logger.LogInformation(
                    "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} httpStatus={HttpStatus} durationMs={DurationMs}",
                    "TenantClientCacheClient.NotFound", nt, nc, "NotFound", status, sw.GetElapsedMs());
                return Empty(SdkCacheOutcome.NotFound, resp);

            case 429:
                _metrics.RateLimited();
                _metrics.RecordDuration(sw.GetElapsedMs(), SdkCacheOutcome.RateLimited);
                _logger.LogWarning(
                    "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} httpStatus={HttpStatus} durationMs={DurationMs}",
                    "TenantClientCacheClient.RateLimited", nt, nc, "RateLimited", status, sw.GetElapsedMs());
                return Empty(SdkCacheOutcome.RateLimited, resp);

            case 503:
                _metrics.ServiceUnavailable();
                _metrics.RecordDuration(sw.GetElapsedMs(), SdkCacheOutcome.ServiceUnavailable);
                _logger.LogError(
                    "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} httpStatus={HttpStatus} durationMs={DurationMs}",
                    "TenantClientCacheClient.ServiceUnavailable", nt, nc, "ServiceUnavailable", status, sw.GetElapsedMs());
                return Empty(SdkCacheOutcome.ServiceUnavailable, resp);

            default:
                // Unknown 4xx/5xx (including 400 invalid_*_id and 5xx
                // exhausted retries that landed on a non-retriable code).
                // Fold into TransientFailure so the caller has a single
                // bucket to "treat as fail-soft".
                _metrics.TransientFailure();
                _metrics.RecordDuration(sw.GetElapsedMs(), SdkCacheOutcome.TransientFailure);
                _logger.LogWarning(
                    "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} httpStatus={HttpStatus} durationMs={DurationMs}",
                    "TenantClientCacheClient.TransientFailure", nt, nc, "TransientFailure", status, sw.GetElapsedMs());
                return Empty(SdkCacheOutcome.TransientFailure, resp);
        }
    }

    private static string BuildRequestPath(string nt, string nc) =>
        $"api/public/tenants/{Uri.EscapeDataString(nt)}/clients/{Uri.EscapeDataString(nc)}";

    private static TenantClientSnapshotResult Empty(
        SdkCacheOutcome outcome,
        HttpResponseMessage resp)
    {
        // R11.4 — surface Retry-After when present (Delta or Date forms);
        // the SDK does not auto-wait, it lets the caller schedule the
        // retry on its own terms.
        TimeSpan? retryAfter = null;
        var retryHeader = resp.Headers.RetryAfter;
        if (retryHeader is { Delta: { } d })
        {
            retryAfter = d;
        }
        else if (retryHeader is { Date: { } dt })
        {
            var diff = dt - DateTimeOffset.UtcNow;
            retryAfter = diff < TimeSpan.Zero ? TimeSpan.Zero : diff;
        }

        return new TenantClientSnapshotResult(
            Snapshot: null,
            Etag: null,
            LastWriteUtc: null,
            Version: null,
            Outcome: outcome,
            RetryAfter: retryAfter);
    }

    private static DateTimeOffset? TryParseDate(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
            return null;
        foreach (var v in values)
        {
            if (DateTimeOffset.TryParse(
                    v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }
        return null;
    }

    private static int? TryParseInt(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
            return null;
        foreach (var v in values)
        {
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    /// <summary>
    /// Lightweight stopwatch — mirrors the helper inside the server-side
    /// <c>TenantClientCacheService</c> so we do not allocate a
    /// <see cref="Stopwatch"/> per request.
    /// </summary>
    private readonly struct ValueStopwatch
    {
        private static readonly double TimestampToMilliseconds =
            1000.0 / Stopwatch.Frequency;

        private readonly long _start;

        private ValueStopwatch(long start) => _start = start;

        public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

        public double GetElapsedMs() =>
            (Stopwatch.GetTimestamp() - _start) * TimestampToMilliseconds;
    }
}
