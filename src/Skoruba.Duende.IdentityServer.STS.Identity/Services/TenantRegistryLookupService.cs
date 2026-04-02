using Skoruba.Duende.IdentityServer.STS.Identity.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.MasterDb;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

public interface ITenantRegistryLookupService
{
    Task<TenantRegistryLookupResultDto?> GetTenantAsync(string tenantKey, string? serviceName, CancellationToken cancellationToken);
}

public sealed class TenantRegistryLookupService : ITenantRegistryLookupService
{
    private const string DefaultServiceKey = "BlazorApiUser";
    private readonly ITenantRepository _tenantRepository;
    private readonly ITenantRegistryCache _tenantRegistryCache;

    public TenantRegistryLookupService(ITenantRepository tenantRepository, ITenantRegistryCache tenantRegistryCache)
    {
        _tenantRepository = tenantRepository;
        _tenantRegistryCache = tenantRegistryCache;
    }

    public async Task<TenantRegistryLookupResultDto?> GetTenantAsync(string tenantKey, string? serviceName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            return null;
        }

        var normalizedTenantKey = tenantKey.Trim();
        var normalizedServiceName = string.IsNullOrWhiteSpace(serviceName) ? null : serviceName.Trim();

        var cachedTenant = _tenantRegistryCache.TryGetTenant(normalizedTenantKey);
        if (cachedTenant != null)
        {
            return new TenantRegistryLookupResultDto
            {
                TenantId = cachedTenant.Id.ToString(),
                Identifier = cachedTenant.TenantKey,
                Name = cachedTenant.DisplayName,
                SecretName = ResolveSecretNameFromCache(normalizedTenantKey, normalizedServiceName, cachedTenant.ConnectionSecrets),
                ConnectionSecrets = new Dictionary<string, string>(cachedTenant.ConnectionSecrets, StringComparer.OrdinalIgnoreCase),
                IsActive = cachedTenant.IsActive
            };
        }

        // The tenant registry lookup should not be tied to an upstream HTTP abort token.
        // The repository already enforces its own short timeout for MasterDb access.
        var tenant = await _tenantRepository.GetByKeyAsync(normalizedTenantKey, CancellationToken.None);
        if (tenant == null)
        {
            return null;
        }

        _tenantRegistryCache.SetTenant(tenant);

        return new TenantRegistryLookupResultDto
        {
            TenantId = tenant.Id.ToString(),
            Identifier = tenant.TenantKey,
            Name = tenant.DisplayName,
            SecretName = ResolveSecretName(tenant.ConnectionSecrets, normalizedServiceName),
            ConnectionSecrets = new Dictionary<string, string>(tenant.ConnectionSecrets, StringComparer.OrdinalIgnoreCase),
            IsActive = tenant.IsActive
        };
    }

    private string ResolveSecretNameFromCache(string tenantKey, string? serviceName, IReadOnlyDictionary<string, string>? connectionSecrets)
    {
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var cachedSecret = _tenantRegistryCache.TryGetSecretName(tenantKey, serviceName);
            if (!string.IsNullOrWhiteSpace(cachedSecret))
            {
                return cachedSecret;
            }
        }

        return ResolveSecretName(connectionSecrets, serviceName);
    }

    private static string ResolveSecretName(IReadOnlyDictionary<string, string>? connectionSecrets, string? serviceName)
    {
        if (connectionSecrets == null || connectionSecrets.Count == 0)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            return connectionSecrets.TryGetValue(serviceName, out var serviceSecret) && !string.IsNullOrWhiteSpace(serviceSecret)
                ? serviceSecret
                : string.Empty;
        }

        if (connectionSecrets.TryGetValue(DefaultServiceKey, out var defaultSecret) && !string.IsNullOrWhiteSpace(defaultSecret))
        {
            return defaultSecret;
        }

        return connectionSecrets.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
