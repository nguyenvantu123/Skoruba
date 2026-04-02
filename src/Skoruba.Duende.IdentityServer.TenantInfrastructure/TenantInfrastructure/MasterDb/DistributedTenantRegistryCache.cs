using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using TenantInfrastructure.Abstractions;

namespace TenantInfrastructure.MasterDb;

public sealed class DistributedTenantRegistryCache : ITenantRegistryCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly DistributedCacheEntryOptions PersistentCacheOptions = new();

    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedTenantRegistryCache> _logger;

    public DistributedTenantRegistryCache(
        IDistributedCache cache,
        ILogger<DistributedTenantRegistryCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public TenantInfo? TryGetTenant(string tenantKey)
    {
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            return null;
        }

        try
        {
            var payload = _cache.GetString(GetTenantKey(tenantKey));
            return string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize<TenantCachePayload>(payload, SerializerOptions)?.ToTenantInfo();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read tenant registry cache for tenant '{TenantKey}'.", tenantKey);
            return null;
        }
    }

    public string? TryGetSecretName(string tenantKey, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(tenantKey) || string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        try
        {
            return _cache.GetString(GetServiceSecretKey(tenantKey, serviceName));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read tenant service secret cache for tenant '{TenantKey}' and service '{ServiceName}'.",
                tenantKey,
                serviceName);
            return null;
        }
    }

    public IReadOnlyList<PublicTenantCacheData>? TryGetPublicTenantNames()
    {
        try
        {
            var payload = _cache.GetString(GetPublicTenantNamesKey());
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            return JsonSerializer.Deserialize<List<PublicTenantCacheData>>(payload, SerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cached public tenant names.");
            return null;
        }
    }

    public void SetTenant(TenantInfo tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (string.IsNullOrWhiteSpace(tenant.TenantKey))
        {
            return;
        }

        var tenantKey = tenant.TenantKey.Trim();

        try
        {
            var payload = JsonSerializer.Serialize(TenantCachePayload.FromTenantInfo(tenant), SerializerOptions);
            _cache.SetString(GetTenantKey(tenantKey), payload, PersistentCacheOptions);

            foreach (var pair in tenant.ConnectionSecrets)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                _cache.SetString(GetServiceSecretKey(tenantKey, pair.Key), pair.Value.Trim(), PersistentCacheOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write tenant registry cache for tenant '{TenantKey}'.", tenantKey);
        }
    }

    public void SetPublicTenantNames(IEnumerable<PublicTenantCacheData> tenantNames)
    {
        ArgumentNullException.ThrowIfNull(tenantNames);

        try
        {
            var normalizedNames = tenantNames
                 .Where(t => !string.IsNullOrWhiteSpace(t.DisplayName))
                 .Select(t => new PublicTenantCacheData { DisplayName = t.DisplayName.Trim(), LogoUrl = t.LogoUrl?.Trim() ?? string.Empty })
                 .ToList();

            var payload = JsonSerializer.Serialize(normalizedNames, SerializerOptions);
            _cache.SetString(GetPublicTenantNamesKey(), payload, PersistentCacheOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write cached public tenant names.");
        }
    }

    private static string GetTenantKey(string tenantKey)
        => $"tenant:{tenantKey.Trim()}";

    private static string GetServiceSecretKey(string tenantKey, string serviceName)
        => $"tenant:{tenantKey.Trim()}:service:{serviceName.Trim()}:secret";

    private static string GetPublicTenantNamesKey()
        => "tenant:public:names";

    private sealed class TenantCachePayload
    {
        public int Id { get; set; }
        public string TenantKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string ConnectionSecretsJson { get; set; } = "{}";
        public string? RedirectUrl { get; set; }
        public string? LogoUrl { get; set; }
        public DateTime CreatedUtc { get; set; }

        public static TenantCachePayload FromTenantInfo(TenantInfo tenant)
        {
            return new TenantCachePayload
            {
                Id = tenant.Id,
                TenantKey = tenant.TenantKey,
                DisplayName = tenant.DisplayName,
                IsActive = tenant.IsActive,
                ConnectionSecretsJson = tenant.ConnectionSecretsJson,
                RedirectUrl = tenant.RedirectUrl,
                LogoUrl = tenant.LogoUrl,
                CreatedUtc = tenant.CreatedUtc
            };
        }

        public TenantInfo ToTenantInfo()
        {
            return new TenantInfo
            {
                Id = Id,
                TenantKey = TenantKey,
                DisplayName = DisplayName,
                IsActive = IsActive,
                ConnectionSecretsJson = ConnectionSecretsJson,
                RedirectUrl = RedirectUrl,
                LogoUrl = LogoUrl,
                CreatedUtc = CreatedUtc
            };
        }
    }

    public class PublicTenantCacheData()
    {

        public string DisplayName { get; set; } = string.Empty;

        public string? LogoUrl { get; set; }
    };
}
