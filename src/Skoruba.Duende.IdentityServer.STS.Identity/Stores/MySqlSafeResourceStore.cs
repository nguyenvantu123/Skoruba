using Duende.IdentityServer.EntityFramework.Mappers;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Microsoft.EntityFrameworkCore;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Stores;

public sealed class MySqlSafeResourceStore : IResourceStore
{
    private readonly IResourceStore _inner;
    private readonly IdentityServerConfigurationDbContext _db;

    public MySqlSafeResourceStore(IResourceStore inner, IdentityServerConfigurationDbContext db)
    {
        _inner = inner;
        _db = db;
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
            var entities = await _db.IdentityResources
                .AsNoTracking()
                .Where(x => x.Enabled && x.Name == name)
                .Include(x => x.UserClaims)
                .Include(x => x.Properties)
                .ToListAsync();

            result.AddRange(entities.Select(e => e.ToModel()));
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
            var entities = await _db.ApiScopes
                .AsNoTracking()
                .Where(x => x.Enabled && x.Name == name)
                .Include(x => x.UserClaims)
                .Include(x => x.Properties)
                .ToListAsync();

            result.AddRange(entities.Select(e => e.ToModel()));
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
            var entities = await _db.ApiResources
                .AsNoTracking()
                .Where(r => r.Enabled && r.Name == name)
                .Include(r => r.Secrets)
                .Include(r => r.Properties)
                .Include(r => r.UserClaims)
                // Duende entity có navigation Scopes, nhưng Skoruba cũng có bảng ApiResourceScopes.
                // Include cả 2 để chắc chắn ToModel có đủ scopes.
                .Include(r => r.Scopes)
                .ToListAsync();

            result.AddRange(entities.Select(e => e.ToModel()));
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
            // ✅ Tránh IN (@names): query theo từng scopeName
            // ✅ Dùng bảng join ApiResourceScopes của Skoruba để match scope
            var entities = await _db.ApiResources
                .AsNoTracking()
                .Where(r => r.Enabled && _db.ApiResourceScopes.Any(j => j.ApiResourceId == r.Id && j.Scope == scopeName))
                .Include(r => r.Secrets)
                .Include(r => r.Properties)
                .Include(r => r.UserClaims)
                .Include(r => r.Scopes)
                .ToListAsync();

            result.AddRange(entities.Select(e => e.ToModel()));
        }

        return result
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public async Task<Resources> GetAllResourcesAsync()
    {
        // Tránh mọi Contains(list) => load enabled từng loại
        var identityEntities = await _db.IdentityResources
            .AsNoTracking()
            .Where(x => x.Enabled)
            .Include(x => x.UserClaims)
            .Include(x => x.Properties)
            .ToListAsync();

        var apiScopeEntities = await _db.ApiScopes
            .AsNoTracking()
            .Where(x => x.Enabled)
            .Include(x => x.UserClaims)
            .Include(x => x.Properties)
            .ToListAsync();

        var apiResourceEntities = await _db.ApiResources
            .AsNoTracking()
            .Where(x => x.Enabled)
            .Include(x => x.Secrets)
            .Include(x => x.Properties)
            .Include(x => x.UserClaims)
            .Include(x => x.Scopes)
            .ToListAsync();

        var identityResources = identityEntities.Select(e => e.ToModel()).ToArray();
        var apiScopes = apiScopeEntities.Select(e => e.ToModel()).ToArray();
        var apiResources = apiResourceEntities.Select(e => e.ToModel()).ToArray();

        return new Resources(identityResources, apiResources, apiScopes);
    }
}
