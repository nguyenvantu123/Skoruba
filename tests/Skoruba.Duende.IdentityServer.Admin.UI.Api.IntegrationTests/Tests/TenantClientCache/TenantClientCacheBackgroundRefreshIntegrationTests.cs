// Feature: tenant-client-cache-expansion, Task 10
//
// E2E coverage of the TenantClientCacheRefreshService BackgroundService.
// We register the refresh service as a singleton (not as a HostedService)
// in the test fixture so each test can drive `SweepAsync` deterministically
// — equivalent to "wait for the periodic loop to run a sweep" but without
// the wall-clock dependency.
//
// Validates: Requirements 8.2, 8.3, 10.6

#nullable enable

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using TenantInfrastructure.MasterDb;
using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.TenantClientCache;

public sealed class TenantClientCacheBackgroundRefreshIntegrationTests
{
    private static ClientDto SeedClient(int id, string clientId, params string[] tenants)
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
        foreach (var t in tenants)
        {
            dto.TenantRedirectPairs.Add(new ClientTenantRedirectPairDto
            {
                TenantKey = t,
                SignInCallbackUrl = $"https://{t}/callback",
                SignOutCallbackUrl = $"https://{t}/signout",
            });
        }
        return dto;
    }

    private static TenantInfo Tenant(int id, string key) => new()
    {
        Id = id,
        TenantKey = key,
        DisplayName = key,
        IsActive = true,
    };

    [Fact]
    public async Task BackgroundRefresh_E2E_FreshSnapshotsAfterStartup()
    {
        // R8.2 / R8.3: the background sweep must populate snapshots for
        // every (tenant, client) tuple resolvable from the seeded data.
        using var host = TenantClientCacheTestHost.Create(b =>
        {
            b.SeedTenants = repo =>
            {
                repo.Add(Tenant(1, "branch-a"));
                repo.Add(Tenant(2, "branch-b"));
            };
            b.SeedClients = store =>
            {
                store.Seed(SeedClient(1, "shared-client", "branch-a", "branch-b"));
                store.Seed(SeedClient(2, "branch-only", "branch-b"));
            };
        });

        await host.BackgroundRefreshService.SweepAsync(CancellationToken.None);

        host.DistributedCache.Get("branch-a:clients:shared-client").Should().NotBeNull();
        host.DistributedCache.Get("branch-b:clients:shared-client").Should().NotBeNull();
        host.DistributedCache.Get("branch-b:clients:branch-only").Should().NotBeNull();

        // The sweep correctly skipped (branch-a, branch-only) because the
        // resolver does NOT include "branch-a" for the second client.
        host.DistributedCache.Get("branch-a:clients:branch-only").Should().BeNull();
    }

    [Fact]
    public async Task BackgroundRefresh_RedisDown_DoesNotCrashHost()
    {
        // R10.6: Redis exception inside the sweep must NOT propagate. The
        // refresh service emits Warning + completes the sweep; the host
        // remains responsive afterwards (we drive a follow-up CRUD call to
        // prove the in-process server didn't crash).
        using var host = TenantClientCacheTestHost.Create(b =>
        {
            b.UseThrowingCache = true;
            b.SeedTenants = repo => repo.Add(Tenant(1, "branch-a"));
            b.SeedClients = store => store.Seed(
                SeedClient(1, "client-a", "branch-a"));
        });
        host.ThrowingCache!.AlwaysThrow(() => new InvalidOperationException("redis offline"));

        Func<Task> run = async () =>
        {
            await host.BackgroundRefreshService.SweepAsync(CancellationToken.None);
        };
        await run.Should().NotThrowAsync(
            "the refresh service must contain Redis exceptions internally");

        // Host still responsive: drive a CRUD POST and verify HTTP success.
        host.ThrowingCache.ResetAlwaysThrow();
        var apiDto = new Dtos.Clients.ClientApiDto
        {
            ClientId = "after-failure",
            ClientName = "after-failure",
            ProtocolType = "oidc",
            AllowedScopes = { "openid" },
            RedirectUris = { "https://x/callback" },
        };
        var resp = await host.Client.PostAsJsonAsync("/api/Clients", apiDto);
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);

        // Sweep also emitted a sweep-completed summary log.
        host.LoggerProvider.Entries
            .Any(e => e.Fields.TryGetValue("EventType", out var v)
                      && (string?)v == "TenantClientCacheRefreshCompleted")
            .Should()
            .BeTrue();
    }
}
