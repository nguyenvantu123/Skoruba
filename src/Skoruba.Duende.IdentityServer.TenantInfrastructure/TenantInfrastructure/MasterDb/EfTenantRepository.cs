using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace TenantInfrastructure.MasterDb;

public sealed class EfTenantRepository : ITenantRepository
{
    private readonly IDbContextFactory<MasterDbContext> _factory;
    private readonly ILogger<EfTenantRepository> _logger;
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(10);

    public EfTenantRepository(
        IDbContextFactory<MasterDbContext> factory,
        ILogger<EfTenantRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TenantInfo>> GetTenantsAsync(string? search, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var query = db.Tenants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x =>
                x.TenantKey.Contains(term) ||
                x.DisplayName.Contains(term));
        }

        return await query
            .OrderBy(x => x.TenantKey)
            .ToListAsync(ct);
    }

    public async Task<TenantInfo?> GetByIdAsync(int id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<TenantInfo?> GetByKeyAsync(string tenantKey, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(LookupTimeout);

        try
        {
            await using var db = await _factory.CreateDbContextAsync(timeoutCts.Token);
            return await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantKey == tenantKey, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "Tenant lookup timed out after {TimeoutSeconds}s for tenant key '{TenantKey}'. Check MasterDb connectivity and ConnectionStrings:MasterDb.",
                LookupTimeout.TotalSeconds,
                tenantKey);

            throw new TimeoutException(
                $"Tenant lookup timed out for tenant '{tenantKey}'. Check MasterDb connectivity and ConnectionStrings:MasterDb.",
                ex);
        }
    }

    public async Task<TenantInfo> AddAsync(TenantInfo tenant, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return tenant;
    }

    public async Task UpdateAsync(TenantInfo tenant, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Tenants.Update(tenant);
        await db.SaveChangesAsync(ct);
    }
}
