// Feature: tenant-client-cache-expansion, Task 10
//
// Performance budgets for the in-memory IDistributedCache code path:
//
//   * Read p99  ≤  5 ms  (R14.1)
//   * Write p99 ≤ 25 ms  (R14.2)
//   * Background sweep over 1 000 tenants × 50 clients < RefreshInterval / 2 (R14.4)
//
// These budgets are guideline ceilings; on slow CI hardware they should
// still pass comfortably with MemoryDistributedCache. The tests are
// tagged Performance via the xunit Trait so a future CI environment can
// skip them with `--filter Category!=Performance` if needed, but they
// MUST PASS by default in this environment per Task 10 guidance.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;
using TenantInfrastructure.MasterDb;
using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.TenantClientCache;

[Trait("Category", "Performance")]
public sealed class TenantClientCachePerformanceTests
{
    private static ClientDto MakeClient(int id, string clientId, string tenantKey)
    {
        var dto = new ClientDto
        {
            Id = id,
            ClientId = clientId,
            ClientName = clientId,
            Enabled = true,
            ProtocolType = "oidc",
            AllowedScopes = { "openid" },
            RedirectUris = { $"https://{clientId}/callback" },
        };
        dto.TenantRedirectPairs.Add(new ClientTenantRedirectPairDto
        {
            TenantKey = tenantKey,
            SignInCallbackUrl = $"https://{tenantKey}/callback",
            SignOutCallbackUrl = $"https://{tenantKey}/signout",
        });
        return dto;
    }

    private static double Percentile(IList<double> samples, double p)
    {
        if (samples.Count == 0) return 0;
        var sorted = samples.OrderBy(x => x).ToArray();
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        rank = Math.Clamp(rank, 0, sorted.Length - 1);
        return sorted[rank];
    }

    [Fact]
    public async Task Performance_Read_P99_Under_5ms_InMemoryCache()
    {
        using var host = TenantClientCacheTestHost.Create();
        var cache = host.TenantClientCache;
        var ct = CancellationToken.None;

        // Pre-warm: write a snapshot we can read in the loop.
        await cache.WriteSnapshotAsync(
            "perf",
            MakeClient(1, "perf-client", "perf"),
            ct);

        // Warm-up to amortise JIT.
        for (var i = 0; i < 50; i++)
        {
            _ = await cache.ReadSnapshotAsync("perf", "perf-client", ct);
        }

        const int Iterations = 1000;
        var samples = new List<double>(Iterations);
        for (var i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = await cache.ReadSnapshotAsync("perf", "perf-client", ct);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        var p99 = Percentile(samples, 99);
        p99.Should().BeLessThan(5.0,
            $"the in-memory read p99 must stay under 5 ms (observed {p99:F3} ms)");
    }

    [Fact]
    public async Task Performance_Write_P99_Under_25ms_InMemoryCache()
    {
        using var host = TenantClientCacheTestHost.Create();
        var cache = host.TenantClientCache;
        var ct = CancellationToken.None;

        var dto = MakeClient(1, "perf-client", "perf");

        // Warm-up.
        for (var i = 0; i < 50; i++)
        {
            await cache.WriteSnapshotAsync("perf", dto, ct);
        }

        const int Iterations = 1000;
        var samples = new List<double>(Iterations);
        for (var i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await cache.WriteSnapshotAsync("perf", dto, ct);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        var p99 = Percentile(samples, 99);
        p99.Should().BeLessThan(25.0,
            $"the in-memory write p99 must stay under 25 ms (observed {p99:F3} ms)");
    }

    [Fact]
    public async Task Performance_BackgroundSweep_1000Tenants_50Clients_Under_HalfRefreshInterval()
    {
        const int TenantCount = 1000;
        const int ClientsPerTenant = 50;
        var refreshInterval = TimeSpan.FromMinutes(5); // realistic operator pick
        var budget = refreshInterval / 2;              // R14.4

        using var host = TenantClientCacheTestHost.Create(b =>
        {
            b.Options.RefreshInterval = refreshInterval;
            b.Options.MaxClientsPerTenant = ClientsPerTenant;
            b.SeedTenants = repo =>
            {
                for (var i = 1; i <= TenantCount; i++)
                {
                    repo.Add(new TenantInfo
                    {
                        Id = i,
                        TenantKey = $"tenant-{i}",
                        DisplayName = $"Tenant {i}",
                        IsActive = true,
                    });
                }
            };
            b.SeedClients = store =>
            {
                var nextId = 1;
                for (var t = 1; t <= TenantCount; t++)
                {
                    var tenantKey = $"tenant-{t}";
                    for (var c = 1; c <= ClientsPerTenant; c++)
                    {
                        store.Seed(MakeClient(nextId++, $"client-{t}-{c}", tenantKey));
                    }
                }
            };
        });

        var sw = Stopwatch.StartNew();
        await host.BackgroundRefreshService.SweepAsync(CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(budget,
            $"sweep over {TenantCount}×{ClientsPerTenant} entries must finish under " +
            $"RefreshInterval/2 = {budget.TotalSeconds:F1}s (observed {sw.Elapsed.TotalSeconds:F2}s)");
    }
}
