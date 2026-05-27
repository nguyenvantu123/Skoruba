// Feature: tenant-client-cache-expansion, Task 10
//
// Synthetic ITenantRepository used by integration tests. Avoids seeding
// a real MasterDb (the EF-backed EfTenantRepository depends on a runtime
// connection string + SQL provider). All sweeps in the tenant-client-
// cache integration tests therefore run against an in-memory list — same
// shape the BackgroundRefresh tests need to exercise the 1000-tenant
// performance scenario without provisioning a database.

#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TenantInfrastructure.MasterDb;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;

internal sealed class InMemoryTenantRepository : ITenantRepository
{
    private readonly ConcurrentDictionary<int, TenantInfo> _byId = new();

    public TenantInfo Add(TenantInfo tenant)
    {
        _byId[tenant.Id] = tenant;
        return tenant;
    }

    public Task<IReadOnlyList<TenantInfo>> GetTenantsAsync(string? search, CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> snapshot = _byId.Values
            .OrderBy(t => t.Id)
            .ToArray();
        return Task.FromResult(snapshot);
    }

    public Task<TenantInfo?> GetByIdAsync(int id, CancellationToken ct)
        => Task.FromResult(_byId.TryGetValue(id, out var t) ? t : null);

    public Task<TenantInfo?> GetByKeyAsync(string tenantKey, CancellationToken ct)
        => Task.FromResult(_byId.Values.FirstOrDefault(t => string.Equals(t.TenantKey, tenantKey, System.StringComparison.OrdinalIgnoreCase)));

    public Task<TenantInfo> AddAsync(TenantInfo tenant, CancellationToken ct)
    {
        _byId[tenant.Id] = tenant;
        return Task.FromResult(tenant);
    }

    public Task UpdateAsync(TenantInfo tenant, CancellationToken ct)
    {
        _byId[tenant.Id] = tenant;
        return Task.CompletedTask;
    }
}
