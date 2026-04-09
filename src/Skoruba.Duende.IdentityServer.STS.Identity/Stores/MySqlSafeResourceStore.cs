using Duende.IdentityServer.EntityFramework.Mappers;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Stores;

public sealed class MySqlSafeResourceStore : IResourceStore
{
    private readonly IdentityServerConfigurationDbContext _db;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan ResourceCacheDuration = TimeSpan.FromMinutes(5);

    public MySqlSafeResourceStore(IResourceStore inner, IdentityServerConfigurationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private static string[] Normalize(IEnumerable<string> names)
        => names?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
           ?? Array.Empty<string>();

    public async Task<IEnumerable<IdentityResource>> FindIdentityResourcesByScopeNameAsync(IEnumerable<string> scopeNames)
    {
        var names = Normalize(scopeNames);
        if (names.Length == 0) return Array.Empty<IdentityResource>();

        var result = new List<IdentityResource>();

        foreach (var name in names)
        {
            var resources = await _cache.GetOrCreateAsync(GetCacheKey("identity-resource", name), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ResourceCacheDuration;

                return await _db.IdentityResources
                    .AsNoTracking()
                    .Where(x => x.Enabled && x.Name == name)
                    .Include(x => x.UserClaims)
                    .Include(x => x.Properties)
                    .Select(e => e.ToModel())
                    .ToListAsync();
            });

            result.AddRange(resources ?? Enumerable.Empty<IdentityResource>());
        }

        return result
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public async Task<IEnumerable<ApiScope>> FindApiScopesByNameAsync(IEnumerable<string> scopeNames)
    {
        var names = Normalize(scopeNames);
        if (names.Length == 0) return Array.Empty<ApiScope>();

        var result = new List<ApiScope>();

        foreach (var name in names)
        {
            var scopes = await _cache.GetOrCreateAsync(GetCacheKey("api-scope", name), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ResourceCacheDuration;

                return await _db.ApiScopes
                    .AsNoTracking()
                    .Where(x => x.Enabled && x.Name == name)
                    .Include(x => x.UserClaims)
                    .Include(x => x.Properties)
                    .Select(e => e.ToModel())
                    .ToListAsync();
            });

            result.AddRange(scopes ?? Enumerable.Empty<ApiScope>());
        }

        return result
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public async Task<IEnumerable<ApiResource>> FindApiResourcesByNameAsync(IEnumerable<string> apiResourceNames)
    {
        var names = Normalize(apiResourceNames);
        if (names.Length == 0) return Array.Empty<ApiResource>();

        var result = new List<ApiResource>();

        foreach (var name in names)
        {
            var resources = await _cache.GetOrCreateAsync(GetCacheKey("api-resource-by-name", name), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ResourceCacheDuration;

                return await _db.ApiResources
                    .AsNoTracking()
                    .Where(r => r.Enabled && r.Name == name)
                    .Include(r => r.Secrets)
                    .Include(r => r.Properties)
                    .Include(r => r.UserClaims)
                    .Include(r => r.Scopes)
                    .Select(e => e.ToModel())
                    .ToListAsync();
            });

            result.AddRange(resources ?? Enumerable.Empty<ApiResource>());
        }

        return result
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public async Task<IEnumerable<ApiResource>> FindApiResourcesByScopeNameAsync(IEnumerable<string> scopeNames)
    {
        var names = Normalize(scopeNames);
        if (names.Length == 0) return Array.Empty<ApiResource>();

        var result = new List<ApiResource>();

        foreach (var scopeName in names)
        {
            var resources = await _cache.GetOrCreateAsync(GetCacheKey("api-resource-by-scope", scopeName), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ResourceCacheDuration;

                return await _db.ApiResources
                    .AsNoTracking()
                    .Where(r => r.Enabled && _db.ApiResourceScopes.Any(j => j.ApiResourceId == r.Id && j.Scope == scopeName))
                    .Include(r => r.Secrets)
                    .Include(r => r.Properties)
                    .Include(r => r.UserClaims)
                    .Include(r => r.Scopes)
                    .Select(e => e.ToModel())
                    .ToListAsync();
            });

            result.AddRange(resources ?? Enumerable.Empty<ApiResource>());
        }

        return result
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public async Task<Resources> GetAllResourcesAsync()
    {
        var resources = await _cache.GetOrCreateAsync("identityserver:resources:all", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ResourceCacheDuration;

            var identityResources = await _db.IdentityResources
                .AsNoTracking()
                .Where(x => x.Enabled)
                .Include(x => x.UserClaims)
                .Include(x => x.Properties)
                .Select(e => e.ToModel())
                .ToArrayAsync();

            var apiScopes = await _db.ApiScopes
                .AsNoTracking()
                .Where(x => x.Enabled)
                .Include(x => x.UserClaims)
                .Include(x => x.Properties)
                .Select(e => e.ToModel())
                .ToArrayAsync();

            var apiResources = await _db.ApiResources
                .AsNoTracking()
                .Where(x => x.Enabled)
                .Include(x => x.Secrets)
                .Include(x => x.Properties)
                .Include(x => x.UserClaims)
                .Include(x => x.Scopes)
                .Select(e => e.ToModel())
                .ToArrayAsync();

            return new Resources(identityResources, apiResources, apiScopes);
        });

        return resources ?? new Resources(Array.Empty<IdentityResource>(), Array.Empty<ApiResource>(), Array.Empty<ApiScope>());
    }

    private static string GetCacheKey(string prefix, string value)
        => $"identityserver:{prefix}:{value.Trim().ToLowerInvariant()}";
}
