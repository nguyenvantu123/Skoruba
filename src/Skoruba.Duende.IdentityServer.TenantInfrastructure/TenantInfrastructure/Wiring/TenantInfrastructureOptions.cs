using TenantInfrastructure.Resolution;

namespace TenantInfrastructure.Wiring;

public sealed class TenantInfrastructureOptions
{
    public string MasterConnectionString { get; set; } = default!;
    public bool ApplyMasterDbMigrations { get; set; }
    public bool AllowMasterDbAutoMigration { get; set; } = true;
    public TenantResolutionOptions Resolution { get; set; } = new();

    public TimeSpan TenantCacheAbsolute { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan TenantCacheSliding { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan TenantCacheRefreshInterval { get; set; } = TimeSpan.FromHours(1);
    public string RedisConnectionString { get; set; } = string.Empty;
    public string RedisInstanceName { get; set; } = "tenant-registry:";

    public bool AllowMissingTenant { get; set; } = true;
    public string[] SkipTenantResolutionHosts { get; set; } = new[] { "localhost", "127.0.0.1" };
}
