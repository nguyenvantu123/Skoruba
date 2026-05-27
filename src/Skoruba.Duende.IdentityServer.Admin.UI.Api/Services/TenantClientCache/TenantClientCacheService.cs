// Feature: tenant-client-cache-expansion, Task 5
//
// Default implementation of ITenantClientCacheService. Singleton — holds
// no per-tenant state; each call composes its own DistributedCacheEntryOptions
// and linked CancellationTokenSource.
//
// Contract pinned by tasks.md Task 5 (verbatim summary):
//
//   * Cache key: PerClientKey / PerTenantListKey, both case-folded on the
//     tenant key. The runtime Redis InstanceName "tenant-registry:" is
//     applied by the caching infrastructure — DO NOT prepend it here.
//
//   * Validation: tenantKey/clientId null/empty/whitespace ⇒ ArgumentException
//     before touching IDistributedCache. clientId == "__list__" is rejected
//     to avoid colliding with the per-tenant list key.
//
//   * Write path: Options.Enabled == false ⇒ Debug log Outcome=WriteSkippedDisabled,
//     return. Else build envelope (Version=1), serialize, 256 KiB size guard,
//     compose DistributedCacheEntryOptions, run under linked CTS bounded by
//     WriteTimeoutMs. Catch all ⇒ Warning + write.failure metric + NO rethrow.
//     OperationCanceledException whose root is the caller token logs Debug
//     and does NOT count as failure (cancellation is expected).
//
//   * Read path: corrupt / null ⇒ Cache_Outcome.Miss (Debug), Version > 1 ⇒
//     Cache_Outcome.Stale (Debug, returns null), valid ⇒ Cache_Outcome.Hit (Debug).
//
//   * Invalidate path: idempotent (R6.7); RemoveAsync of a missing key is
//     still success.
//
// Validates: Requirements 1.7, 1.8, 2.6, 3.1, 3.2, 3.3, 3.4, 3.5, 3.7, 3.8,
//            5.4, 5.5, 6.7, 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7, 10.1, 10.2,
//            10.3, 10.4, 12.4

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

internal sealed class TenantClientCacheService : ITenantClientCacheService
{
    /// <summary>Logical key suffix for the per-tenant list helper key.</summary>
    /// <remarks>
    /// Picked deliberately to be a value Duende will never accept as a real
    /// <c>clientId</c>; the constructor / write entry-points reject this
    /// suffix as a clientId argument so the list key cannot be overwritten
    /// by a malicious or misconfigured client (see <see cref="ListSuffix"/>
    /// guards in <c>WriteSnapshotAsync</c> / <c>InvalidateSnapshotAsync</c>).
    /// </remarks>
    internal const string ListSuffix = "__list__";

    /// <summary>Snapshot envelope schema version this writer emits.</summary>
    internal const int CurrentSnapshotVersion = 1;

    /// <summary>Audit_Event name used in structured log templates.</summary>
    private const string EventTypeWrite = "TenantClientCacheWrite";
    private const string EventTypeRead = "TenantClientCacheRead";
    private const string EventTypeInvalidate = "TenantClientCacheInvalidate";

    private readonly IDistributedCache _cache;
    private readonly IOptionsMonitor<TenantClientCacheOptions> _options;
    private readonly ILogger<TenantClientCacheService> _logger;
    private readonly TenantClientCacheMetrics _metrics;
    private readonly TimeProvider _time;

    public TenantClientCacheService(
        IDistributedCache cache,
        IOptionsMonitor<TenantClientCacheOptions> options,
        ILogger<TenantClientCacheService> logger,
        TenantClientCacheMetrics metrics)
        : this(cache, options, logger, metrics, TimeProvider.System)
    {
    }

    public TenantClientCacheService(
        IDistributedCache cache,
        IOptionsMonitor<TenantClientCacheOptions> options,
        ILogger<TenantClientCacheService> logger,
        TenantClientCacheMetrics metrics,
        TimeProvider timeProvider)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    // ===== Cache key composition =====================================

    /// <summary>
    /// Logical per-client cache key. The Redis InstanceName prefix
    /// (<c>tenant-registry:</c>) is appended automatically by the
    /// StackExchange.Redis distributed-cache wrapper at runtime — DO NOT
    /// add it here.
    /// </summary>
    internal static string PerClientKey(string tenantKey, string clientId)
        => $"{tenantKey.Trim().ToLowerInvariant()}:clients:{clientId.Trim()}";

    /// <summary>
    /// Logical per-tenant list key. Uses the <c>__list__</c> suffix to
    /// avoid colliding with a real <c>clientId == "list"</c>.
    /// </summary>
    internal static string PerTenantListKey(string tenantKey)
        => $"{tenantKey.Trim().ToLowerInvariant()}:clients:{ListSuffix}";

    // ===== Read =======================================================

    public async Task<ClientCacheSnapshotEnvelope?> ReadSnapshotAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken)
    {
        ValidateTenantKey(tenantKey);
        ValidateClientId(clientId);

        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            // Reads on a disabled cache produce a deterministic miss. We
            // emit the same Debug audit shape as writes so dashboards
            // reading the log stream can correlate both directions.
            EmitAudit(
                LogLevel.Debug,
                EventTypeRead,
                tenantKey,
                clientId,
                Cache_Outcome.WriteSkippedDisabled,
                durationMs: 0,
                snapshotVersion: null,
                exception: null);
            return null;
        }

        var key = PerClientKey(tenantKey, clientId);
        var sw = ValueStopwatch.StartNew();

        byte[]? payload;
        using var linked = CreateLinkedCts(cancellationToken, options.WriteTimeoutMs);
        try
        {
            payload = await _cache.GetAsync(key, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-driven cancellation. Not a failure.
            EmitAudit(
                LogLevel.Debug,
                EventTypeRead,
                tenantKey,
                clientId,
                Cache_Outcome.Miss,
                sw.GetElapsedMs(),
                snapshotVersion: null,
                exception: oce);
            return null;
        }
        catch (Exception ex)
        {
            // Read failures degrade to Miss — out-of-scope phase has no
            // consumer that can act on a "transient read failure" signal,
            // and treating the entry as missing is the safe behaviour.
            EmitAudit(
                LogLevel.Warning,
                EventTypeRead,
                tenantKey,
                clientId,
                Cache_Outcome.Miss,
                sw.GetElapsedMs(),
                snapshotVersion: null,
                exception: ex);
            _metrics.RecordRead(NormalizeTenantKey(tenantKey), Cache_Outcome.Miss);
            return null;
        }

        if (payload is null || payload.Length == 0)
        {
            EmitAudit(
                LogLevel.Debug,
                EventTypeRead,
                tenantKey,
                clientId,
                Cache_Outcome.Miss,
                sw.GetElapsedMs(),
                snapshotVersion: null,
                exception: null);
            _metrics.RecordRead(NormalizeTenantKey(tenantKey), Cache_Outcome.Miss);
            return null;
        }

        var envelope = ClientCacheSnapshotSerializer.TryDeserialize(payload, out var failureReason);
        if (envelope is null)
        {
            // Corrupt payload (truncation race, partial write, schema
            // mismatch). Treat as Miss per R10.4.
            EmitAudit(
                LogLevel.Debug,
                EventTypeRead,
                tenantKey,
                clientId,
                Cache_Outcome.Miss,
                sw.GetElapsedMs(),
                snapshotVersion: null,
                exception: null,
                subreason: failureReason ?? "corrupt");
            _metrics.RecordRead(NormalizeTenantKey(tenantKey), Cache_Outcome.Miss);
            return null;
        }

        if (envelope.Version > CurrentSnapshotVersion)
        {
            // A future writer wrote a newer schema. Surface as Stale per
            // R2.8 and treat as Miss for downstream consumers.
            EmitAudit(
                LogLevel.Debug,
                EventTypeRead,
                tenantKey,
                clientId,
                Cache_Outcome.Stale,
                sw.GetElapsedMs(),
                snapshotVersion: envelope.Version,
                exception: null,
                subreason: "future_version");
            _metrics.RecordRead(NormalizeTenantKey(tenantKey), Cache_Outcome.Stale);
            return null;
        }

        EmitAudit(
            LogLevel.Debug,
            EventTypeRead,
            tenantKey,
            clientId,
            Cache_Outcome.Hit,
            sw.GetElapsedMs(),
            snapshotVersion: envelope.Version,
            exception: null);
        _metrics.RecordRead(NormalizeTenantKey(tenantKey), Cache_Outcome.Hit);
        return envelope;
    }

    // ===== Write ======================================================

    public Task WriteSnapshotAsync(
        string tenantKey,
        ClientDto client,
        CancellationToken cancellationToken)
    {
        ValidateTenantKey(tenantKey);
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }
        ValidateClientId(client.ClientId);

        return WriteSnapshotCoreAsync(tenantKey, client, cancellationToken);
    }

    public async Task WriteSnapshotsAsync(
        IReadOnlyCollection<string> tenantKeys,
        ClientDto client,
        CancellationToken cancellationToken)
    {
        if (tenantKeys is null)
        {
            throw new ArgumentNullException(nameof(tenantKeys));
        }
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }
        ValidateClientId(client.ClientId);

        // Sequential foreach is acceptable per design ("≤ 50 tenants per
        // client typical"). Each call self-contained; one tenant's failure
        // does not affect the others (the call is fail-soft internally).
        foreach (var tenantKey in tenantKeys)
        {
            ValidateTenantKey(tenantKey);
            await WriteSnapshotCoreAsync(tenantKey, client, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteSnapshotCoreAsync(
        string tenantKey,
        ClientDto client,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            // R1.7: do not touch IDistributedCache when disabled.
            EmitAudit(
                LogLevel.Debug,
                EventTypeWrite,
                tenantKey,
                client.ClientId,
                Cache_Outcome.WriteSkippedDisabled,
                durationMs: 0,
                snapshotVersion: null,
                exception: null);
            return;
        }

        var sw = ValueStopwatch.StartNew();
        var lastWriteUtc = _time.GetUtcNow().UtcDateTime;

        byte[] payload;
        try
        {
            var snapshot = ClientCacheSnapshotMapper.Map(client, lastWriteUtc);
            var envelope = new ClientCacheSnapshotEnvelope
            {
                Version = CurrentSnapshotVersion,
                TenantKey = NormalizeTenantKey(tenantKey),
                ClientId = client.ClientId.Trim(),
                LastWriteUtc = lastWriteUtc,
                Data = snapshot,
            };
            payload = ClientCacheSnapshotSerializer.Serialize(envelope);
        }
        catch (Exception ex)
        {
            // Defensive: serializer / mapper threw before we hit the wire.
            // Treat as transient failure (caller still gets fail-soft
            // semantics, no exception propagation).
            EmitAudit(
                LogLevel.Warning,
                EventTypeWrite,
                tenantKey,
                client.ClientId,
                Cache_Outcome.WriteFailedTransient,
                sw.GetElapsedMs(),
                snapshotVersion: CurrentSnapshotVersion,
                exception: ex,
                subreason: "serialize_failed");
            _metrics.RecordWrite(NormalizeTenantKey(tenantKey), Cache_Outcome.WriteFailedTransient);
            return;
        }

        if (!ClientCacheSnapshotSizeGuard.IsWithinLimit(payload))
        {
            // R2.6: oversize snapshots are rejected without writing.
            EmitAudit(
                LogLevel.Warning,
                EventTypeWrite,
                tenantKey,
                client.ClientId,
                Cache_Outcome.WriteFailedTransient,
                sw.GetElapsedMs(),
                snapshotVersion: CurrentSnapshotVersion,
                exception: null,
                subreason: "oversize",
                payloadBytes: payload.Length);
            _metrics.RecordWrite(NormalizeTenantKey(tenantKey), Cache_Outcome.WriteFailedTransient);
            return;
        }

        var entryOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.AbsoluteTtl,
        };
        if (options.SlidingTtl is { } sliding)
        {
            entryOptions.SlidingExpiration = sliding;
        }

        var key = PerClientKey(tenantKey, client.ClientId);
        using var linked = CreateLinkedCts(cancellationToken, options.WriteTimeoutMs);

        try
        {
            await _cache.SetAsync(key, payload, entryOptions, linked.Token).ConfigureAwait(false);
            EmitAudit(
                LogLevel.Information,
                EventTypeWrite,
                tenantKey,
                client.ClientId,
                Cache_Outcome.WriteSucceeded,
                sw.GetElapsedMs(),
                snapshotVersion: CurrentSnapshotVersion,
                exception: null);
            _metrics.RecordWrite(NormalizeTenantKey(tenantKey), Cache_Outcome.WriteSucceeded);
        }
        catch (OperationCanceledException oce) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled (request aborted, host shutting down). Not
            // an error condition — Debug log, no failure metric (R10.x).
            EmitAudit(
                LogLevel.Debug,
                EventTypeWrite,
                tenantKey,
                client.ClientId,
                Cache_Outcome.WriteFailedTransient,
                sw.GetElapsedMs(),
                snapshotVersion: CurrentSnapshotVersion,
                exception: oce,
                subreason: "caller_cancelled");
        }
        catch (OperationCanceledException oce)
        {
            // Internal timeout (linked CTS expired). Distinct from caller
            // cancellation; surfaces in the metric as a transient failure.
            EmitAudit(
                LogLevel.Warning,
                EventTypeWrite,
                tenantKey,
                client.ClientId,
                Cache_Outcome.WriteFailedTransient,
                sw.GetElapsedMs(),
                snapshotVersion: CurrentSnapshotVersion,
                exception: oce,
                subreason: "write_timeout");
            _metrics.RecordWrite(NormalizeTenantKey(tenantKey), Cache_Outcome.WriteFailedTransient);
        }
        catch (Exception ex)
        {
            EmitAudit(
                LogLevel.Warning,
                EventTypeWrite,
                tenantKey,
                client.ClientId,
                Cache_Outcome.WriteFailedTransient,
                sw.GetElapsedMs(),
                snapshotVersion: CurrentSnapshotVersion,
                exception: ex);
            _metrics.RecordWrite(NormalizeTenantKey(tenantKey), Cache_Outcome.WriteFailedTransient);
        }
    }

    // ===== Invalidate =================================================

    public Task InvalidateSnapshotAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken)
    {
        ValidateTenantKey(tenantKey);
        ValidateClientId(clientId);
        return InvalidateSnapshotCoreAsync(tenantKey, clientId, cancellationToken);
    }

    public async Task InvalidateSnapshotsAsync(
        IReadOnlyCollection<string> tenantKeys,
        string clientId,
        CancellationToken cancellationToken)
    {
        if (tenantKeys is null)
        {
            throw new ArgumentNullException(nameof(tenantKeys));
        }
        ValidateClientId(clientId);

        foreach (var tenantKey in tenantKeys)
        {
            ValidateTenantKey(tenantKey);
            await InvalidateSnapshotCoreAsync(tenantKey, clientId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InvalidateSnapshotCoreAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            EmitAudit(
                LogLevel.Debug,
                EventTypeInvalidate,
                tenantKey,
                clientId,
                Cache_Outcome.WriteSkippedDisabled,
                durationMs: 0,
                snapshotVersion: null,
                exception: null);
            return;
        }

        var sw = ValueStopwatch.StartNew();
        var key = PerClientKey(tenantKey, clientId);

        using var linked = CreateLinkedCts(cancellationToken, options.WriteTimeoutMs);
        try
        {
            await _cache.RemoveAsync(key, linked.Token).ConfigureAwait(false);
            EmitAudit(
                LogLevel.Information,
                EventTypeInvalidate,
                tenantKey,
                clientId,
                Cache_Outcome.InvalidateSucceeded,
                sw.GetElapsedMs(),
                snapshotVersion: null,
                exception: null);
            _metrics.RecordInvalidate(NormalizeTenantKey(tenantKey), Cache_Outcome.InvalidateSucceeded);
        }
        catch (OperationCanceledException oce) when (cancellationToken.IsCancellationRequested)
        {
            EmitAudit(
                LogLevel.Debug,
                EventTypeInvalidate,
                tenantKey,
                clientId,
                Cache_Outcome.InvalidateFailedTransient,
                sw.GetElapsedMs(),
                snapshotVersion: null,
                exception: oce,
                subreason: "caller_cancelled");
        }
        catch (OperationCanceledException oce)
        {
            EmitAudit(
                LogLevel.Warning,
                EventTypeInvalidate,
                tenantKey,
                clientId,
                Cache_Outcome.InvalidateFailedTransient,
                sw.GetElapsedMs(),
                snapshotVersion: null,
                exception: oce,
                subreason: "write_timeout");
            _metrics.RecordInvalidate(NormalizeTenantKey(tenantKey), Cache_Outcome.InvalidateFailedTransient);
        }
        catch (Exception ex)
        {
            EmitAudit(
                LogLevel.Warning,
                EventTypeInvalidate,
                tenantKey,
                clientId,
                Cache_Outcome.InvalidateFailedTransient,
                sw.GetElapsedMs(),
                snapshotVersion: null,
                exception: ex);
            _metrics.RecordInvalidate(NormalizeTenantKey(tenantKey), Cache_Outcome.InvalidateFailedTransient);
        }
    }

    // ===== Helpers ====================================================

    private static void ValidateTenantKey(string tenantKey)
    {
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            throw new ArgumentException(
                "tenantKey must be non-empty after trimming.",
                nameof(tenantKey));
        }
    }

    private static void ValidateClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException(
                "clientId must be non-empty after trimming.",
                nameof(clientId));
        }

        // R3.5 / Risks-table mitigation: refuse the reserved suffix so it
        // cannot be used to overwrite the per-tenant list key.
        if (string.Equals(clientId.Trim(), ListSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"clientId '{ListSuffix}' is reserved by the tenant client cache.",
                nameof(clientId));
        }
    }

    private static string NormalizeTenantKey(string tenantKey)
        => tenantKey.Trim().ToLowerInvariant();

    private CancellationTokenSource CreateLinkedCts(CancellationToken caller, int writeTimeoutMs)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller);
        if (writeTimeoutMs > 0)
        {
            linked.CancelAfter(writeTimeoutMs);
        }
        return linked;
    }

    /// <summary>
    /// Emit a single Audit_Event log entry with the full structured field
    /// set. Centralised so the field shape stays uniform across paths.
    /// </summary>
    /// <remarks>
    /// We intentionally do NOT log the raw cache key, the raw exception,
    /// or the snapshot body (R13.3, R13.4, R13.7).
    /// </remarks>
    private void EmitAudit(
        LogLevel level,
        string eventType,
        string tenantKey,
        string? clientId,
        Cache_Outcome outcome,
        double durationMs,
        int? snapshotVersion,
        Exception? exception,
        string? subreason = null,
        int? payloadBytes = null)
    {
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        var sanitizedMessage = exception is null
            ? null
            : LogRedaction.SanitizeExceptionMessage(exception);
        var correlationId = LogRedaction.CurrentCorrelationId();
        var outcomeTag = TenantClientCacheMetrics.FormatOutcome(outcome);
        var normalizedTenantKey = NormalizeTenantKey(tenantKey);

        // Use a fixed structured-template message: the property names
        // (EventType, TenantKey, ...) become the structured field names
        // that downstream sinks (Serilog) emit verbatim.
        _logger.Log(
            level,
            "{EventType} tenant={TenantKey} client={ClientId} outcome={Outcome} version={SnapshotVersion} durationMs={DurationMs} corr={CorrelationId} subreason={Subreason} payloadBytes={PayloadBytes} exMessage={ExceptionMessage}",
            eventType,
            normalizedTenantKey,
            clientId ?? string.Empty,
            outcomeTag,
            snapshotVersion,
            durationMs,
            correlationId,
            subreason,
            payloadBytes,
            sanitizedMessage);
    }

    /// <summary>
    /// Lightweight stopwatch that avoids allocating a <see cref="Stopwatch"/>
    /// per call. Resolution: <see cref="Stopwatch.GetTimestamp"/>.
    /// </summary>
    private readonly struct ValueStopwatch
    {
        private static readonly double TimestampToMilliseconds =
            1000.0 / Stopwatch.Frequency;

        private readonly long _start;

        private ValueStopwatch(long start) => _start = start;

        public static ValueStopwatch StartNew() =>
            new(Stopwatch.GetTimestamp());

        public double GetElapsedMs()
        {
            var elapsed = Stopwatch.GetTimestamp() - _start;
            return elapsed * TimestampToMilliseconds;
        }
    }
}
