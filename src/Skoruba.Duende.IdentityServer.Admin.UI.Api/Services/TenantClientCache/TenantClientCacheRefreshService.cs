// Feature: tenant-client-cache-expansion, Task 9
//
// Background sweep that periodically rebuilds the tenant-client snapshot
// cache from source-of-truth (DB). Self-heals drift caused by missed CRUD
// invalidations (Redis down during a controller write, manual DB edits, …).
//
// Independent of TenantInfrastructure.MasterDb.TenantRegistryCacheRefreshService
// — the two services run in parallel, each on its own RefreshInterval, each
// fail-soft on its own. We deliberately do NOT inherit nor decorate the
// existing tenant registry refresh service (R8.9). This class lives in the
// Admin.UI.Api project so it can consume IClientService (BusinessLogic tier)
// without forcing TenantInfrastructure to depend on a higher layer.
//
// Lifecycle (mirrors TenantRegistryCacheRefreshService):
//
//   ExecuteAsync(stoppingToken)
//     ├─ if (!Options.Enabled) return                   ← R1.8 / R8.1
//     ├─ SweepAsync(stoppingToken)                      ← R8.2 immediate sweep
//     └─ while (!stoppingToken.IsCancellationRequested)
//          ├─ Task.Delay(RefreshInterval, stoppingToken)
//          └─ SweepAsync(stoppingToken)
//
// SweepAsync(ct):
//   1. Stopwatch.StartNew()
//   2. CreateScope ⇒ resolve scoped ITenantRepository / IClientService /
//      IClientTenantScopeResolver (each sweep gets a fresh DbContext).
//   3. tenants ← tenantRepo.GetTenantsAsync(null, ct)
//   4. foreach (active tenant)
//        ids ← clientService.ListClientPrimaryKeysForTenantAsync(...)
//        if (ids.Count > MaxClientsPerTenant) log Warning + trim       ← R8.4
//        foreach (id) → load ClientDto → resolve tenant scope →
//                       write snapshot if tenantKey ∈ resolvedKeys
//   5. Each tenant wrapped in try/catch — any failure emits Warning, the
//      sweep continues to the next tenant (R8.5).
//   6. finally:
//        emit Information sweep summary log                              ← R8.6
//        update refresh.sweep.duration_ms histogram                      ← R16
//        update refresh.last_completed_at observable gauge               ← R16.4
//
// Validates: Requirements 1.10, 8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 8.7, 8.8,
//            8.9, 14.4, 15.5, 16.4

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using TenantInfrastructure.MasterDb;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

internal sealed class TenantClientCacheRefreshService : BackgroundService
{
    /// <summary>Audit_Event name for per-tenant / per-client warnings.</summary>
    private const string EventTypeRefresh = "TenantClientCacheRefresh";

    /// <summary>Audit_Event name for the per-cycle summary log.</summary>
    private const string EventTypeRefreshCompleted = "TenantClientCacheRefreshCompleted";

    /// <summary>Subreason emitted when a tenant resolves to more clients than the cap.</summary>
    private const string SubreasonMaxExceeded = "MaxClientsPerTenantExceeded";

    /// <summary>Subreason emitted when sweep wall-clock &gt; RefreshInterval / 2.</summary>
    private const string SubreasonRefreshSweepTooLong = "RefreshSweepTooLong";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<TenantClientCacheOptions> _options;
    private readonly ILogger<TenantClientCacheRefreshService> _logger;
    private readonly ITenantClientCacheService _cache;
    private readonly TenantClientCacheMetrics _metrics;
    private readonly TimeProvider _time;

    public TenantClientCacheRefreshService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<TenantClientCacheOptions> options,
        ILogger<TenantClientCacheRefreshService> logger,
        ITenantClientCacheService cache,
        TenantClientCacheMetrics metrics)
        : this(scopeFactory, options, logger, cache, metrics, TimeProvider.System)
    {
    }

    public TenantClientCacheRefreshService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<TenantClientCacheOptions> options,
        ILogger<TenantClientCacheRefreshService> logger,
        ITenantClientCacheService cache,
        TenantClientCacheMetrics metrics,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // R1.8 / R8.1: when the feature flag is off, the BackgroundService
        // does NOTHING — no sweep, no metrics update, no log noise. The
        // host is still expected to register us; flipping the flag must
        // not require a redeploy.
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        // Single Information log on first start (R1.10). The logged values
        // are the bound options at startup; subsequent refreshes can pick
        // up changes via IOptionsMonitor.CurrentValue.
        var snapshot = _options.CurrentValue;
        _logger.LogInformation(
            "TenantClientCacheRefreshServiceStarted RefreshInterval={RefreshInterval} AbsoluteTtl={AbsoluteTtl} SlidingTtl={SlidingTtl} WriteTimeoutMs={WriteTimeoutMs} MaxClientsPerTenant={MaxClientsPerTenant}",
            snapshot.RefreshInterval,
            snapshot.AbsoluteTtl,
            snapshot.SlidingTtl,
            snapshot.WriteTimeoutMs,
            snapshot.MaxClientsPerTenant);

        // R8.2: immediate sweep on startup, BEFORE entering the periodic
        // loop, so freshly started hosts do not run for an hour with a
        // cold cache.
        await SweepAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Every iteration re-reads CurrentValue so an operator who
                // shortens RefreshInterval at runtime can see it take effect
                // on the NEXT delay (vs only after a restart).
                await Task.Delay(_options.CurrentValue.RefreshInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // R8.7: clean exit on host shutdown.
                break;
            }

            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One sweep cycle. Internal so unit tests can drive a single sweep
    /// deterministically without standing up a Task.Delay loop.
    /// </summary>
    /// <remarks>
    /// Never throws — every per-tenant + per-client error is caught and
    /// logged; cancellation propagates only via <paramref name="ct"/> being
    /// observed by the in-flight <c>await</c> (R8.7).
    /// </remarks>
    internal async Task SweepAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tenantsSwept = 0;
        var clientsWritten = 0;
        var writeFailures = 0;

        try
        {
            // Each sweep gets a fresh scope so a stale DbContext from a
            // previous cycle cannot bleed across.
            using var scope = _scopeFactory.CreateScope();

            ITenantRepository tenantRepo;
            IClientService clientService;
            IClientTenantScopeResolver resolver;

            try
            {
                tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                clientService = scope.ServiceProvider.GetRequiredService<IClientService>();
                resolver = scope.ServiceProvider.GetRequiredService<IClientTenantScopeResolver>();
            }
            catch (Exception ex)
            {
                // Misconfiguration (a required service is missing). Log
                // and bail out of this cycle — the next cycle will retry.
                _logger.LogWarning(
                    ex,
                    "Tenant client cache refresh could not resolve required scoped services; skipping sweep.");
                return;
            }

            IReadOnlyList<TenantInfo> tenants;
            try
            {
                tenants = await tenantRepo.GetTenantsAsync(null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Tenant client cache refresh failed to enumerate tenants.");
                return;
            }

            foreach (var tenant in tenants)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (tenant is null || !tenant.IsActive || string.IsNullOrWhiteSpace(tenant.TenantKey))
                {
                    continue;
                }

                tenantsSwept++;

                try
                {
                    var (written, failed) = await SweepTenantAsync(
                        tenant,
                        clientService,
                        resolver,
                        ct).ConfigureAwait(false);
                    clientsWritten += written;
                    writeFailures += failed;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // R8.5 / R10.6: a Redis or DB exception on one tenant
                    // must not crash the sweep for the next tenant. We
                    // count the whole tenant as one transient failure.
                    writeFailures++;
                    _logger.LogWarning(
                        ex,
                        "{EventType} tenant={TenantKey} outcome={Outcome} subreason={Subreason}",
                        EventTypeRefresh,
                        tenant.TenantKey,
                        TenantClientCacheMetrics.FormatOutcome(Cache_Outcome.WriteFailedTransient),
                        "TenantSweepFailed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // R8.7: host shutdown. Debug log only — neither failure nor
            // success; the metrics gauge is still updated below so any
            // dashboard sees the time of the last *partial* sweep.
            _logger.LogDebug(
                "Tenant client cache refresh canceled because the host is shutting down.");
        }
        catch (Exception ex)
        {
            // Catch-all so the BackgroundService loop never propagates.
            _logger.LogWarning(ex, "Tenant client cache refresh sweep failed.");
        }
        finally
        {
            sw.Stop();
            var durationMs = sw.Elapsed.TotalMilliseconds;
            _metrics.RecordRefreshSweepDuration(durationMs);
            _metrics.SetLastSweepCompletedAt(_time.GetUtcNow().ToUnixTimeSeconds());

            // R14.4: a sweep that overruns half its interval is a soft
            // performance regression — surface as Warning so dashboards
            // can alert before the *next* cycle overlaps.
            var refreshInterval = _options.CurrentValue.RefreshInterval;
            if (refreshInterval > TimeSpan.Zero
                && sw.Elapsed > TimeSpan.FromTicks(refreshInterval.Ticks / 2))
            {
                _logger.LogWarning(
                    "{EventType} outcome={Outcome} subreason={Subreason} durationMs={DurationMs} refreshIntervalMs={RefreshIntervalMs}",
                    EventTypeRefresh,
                    TenantClientCacheMetrics.FormatOutcome(Cache_Outcome.WriteFailedTransient),
                    SubreasonRefreshSweepTooLong,
                    durationMs,
                    refreshInterval.TotalMilliseconds);
            }

            // R8.6: single Information summary per cycle. We emit this
            // even on cancellation so observers can see the partial work
            // a shutting-down host completed.
            _logger.LogInformation(
                "{EventType} TenantsSwept={TenantsSwept} ClientsWritten={ClientsWritten} WriteFailures={WriteFailures} DurationMs={DurationMs}",
                EventTypeRefreshCompleted,
                tenantsSwept,
                clientsWritten,
                writeFailures,
                durationMs);
        }
    }

    /// <summary>
    /// Process a single tenant. Returns (written, failed) so the caller
    /// can aggregate counts for the sweep summary log.
    /// </summary>
    private async Task<(int written, int failed)> SweepTenantAsync(
        TenantInfo tenant,
        IClientService clientService,
        IClientTenantScopeResolver resolver,
        CancellationToken ct)
    {
        var max = _options.CurrentValue.MaxClientsPerTenant;
        IReadOnlyList<int> ids = await clientService
            .ListClientPrimaryKeysForTenantAsync(tenant.TenantKey, max, ct)
            .ConfigureAwait(false);

        // R8.4: the repository returns up to max+1 entries so callers can
        // detect overflow. When observed, log Warning naming the count and
        // the configured cap, then trim to `max` and proceed.
        if (ids.Count > max)
        {
            _logger.LogWarning(
                "{EventType} tenant={TenantKey} outcome={Outcome} subreason={Subreason} observedCount={ObservedCount} cap={Cap}",
                EventTypeRefresh,
                tenant.TenantKey,
                TenantClientCacheMetrics.FormatOutcome(Cache_Outcome.WriteSkippedDisabled),
                SubreasonMaxExceeded,
                ids.Count,
                max);
            ids = ids.Take(max).ToArray();
        }

        var written = 0;
        var failed = 0;

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                ClientDto client;
                try
                {
                    client = await clientService.GetClientAsync(id).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // R15.5: a DB error loading a single client must NOT
                    // result in a partial / null snapshot being cached.
                    // Skip the id, continue with the next.
                    failed++;
                    _logger.LogWarning(
                        ex,
                        "{EventType} tenant={TenantKey} clientPrimaryKey={ClientPrimaryKey} outcome={Outcome} subreason={Subreason}",
                        EventTypeRefresh,
                        tenant.TenantKey,
                        id,
                        TenantClientCacheMetrics.FormatOutcome(Cache_Outcome.WriteFailedTransient),
                        "ClientLoadFailed");
                    continue;
                }

                if (client is null)
                {
                    continue;
                }

                var resolvedKeys = await resolver
                    .ResolveTenantKeysAsync(client, ct)
                    .ConfigureAwait(false);

                if (!ContainsTenant(resolvedKeys, tenant.TenantKey))
                {
                    // Drift: the client was previously mapped to this
                    // tenant but is no longer. Skip writing — the
                    // controller invalidate path is responsible for
                    // removing the snapshot at the moment of CRUD.
                    continue;
                }

                await _cache.WriteSnapshotAsync(tenant.TenantKey, client, ct).ConfigureAwait(false);
                written++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Defensive — any other unexpected exception per-id is
                // contained here so the sweep continues. The cache
                // service itself is already fail-soft, so reaching this
                // catch is unusual.
                failed++;
                _logger.LogWarning(
                    ex,
                    "{EventType} tenant={TenantKey} clientPrimaryKey={ClientPrimaryKey} outcome={Outcome}",
                    EventTypeRefresh,
                    tenant.TenantKey,
                    id,
                    TenantClientCacheMetrics.FormatOutcome(Cache_Outcome.WriteFailedTransient));
            }
        }

        return (written, failed);
    }

    private static bool ContainsTenant(IReadOnlyList<string> keys, string tenantKey)
    {
        if (keys is null || keys.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < keys.Count; i++)
        {
            if (string.Equals(keys[i], tenantKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
