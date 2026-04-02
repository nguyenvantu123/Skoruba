using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.Wiring;
using static TenantInfrastructure.MasterDb.DistributedTenantRegistryCache;

namespace TenantInfrastructure.MasterDb;

public sealed class TenantRegistryCacheRefreshService : BackgroundService
{
    private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ITenantRegistryCache _tenantRegistryCache;
    private readonly TenantInfrastructureOptions _options;
    private readonly ILogger<TenantRegistryCacheRefreshService> _logger;

    public TenantRegistryCacheRefreshService(
        IServiceScopeFactory serviceScopeFactory,
        ITenantRegistryCache tenantRegistryCache,
        TenantInfrastructureOptions options,
        ILogger<TenantRegistryCacheRefreshService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _tenantRegistryCache = tenantRegistryCache;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshCacheAsync(stoppingToken);

        var refreshInterval = GetRefreshInterval();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(refreshInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RefreshCacheAsync(stoppingToken);
        }
    }

    private async Task RefreshCacheAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
            var tenants = await repository.GetTenantsAsync(null, stoppingToken);

            foreach (var tenant in tenants)
            {
                _tenantRegistryCache.SetTenant(tenant);
            }

            var publicTenantNames = tenants
                .Where(x => x.IsActive)
                .Select(x => new PublicTenantCacheData { DisplayName = x.DisplayName, LogoUrl = x.LogoUrl })
                .ToList();

            _tenantRegistryCache.SetPublicTenantNames(publicTenantNames);

            _logger.LogInformation(
                "Refreshed tenant registry cache for {TenantCount} tenant(s).",
                tenants.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Tenant registry cache refresh was canceled because the host is shutting down.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant registry cache refresh failed.");
        }
    }

    private TimeSpan GetRefreshInterval()
    {
        return _options.TenantCacheRefreshInterval <= TimeSpan.Zero
            ? TimeSpan.FromHours(1)
            : _options.TenantCacheRefreshInterval < MinimumRefreshInterval
                ? MinimumRefreshInterval
                : _options.TenantCacheRefreshInterval;
    }
}
