using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Admin.Storage.Entities.Configuration;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql.DesignTime;

const string PropertyKey = "skoruba_tenant_redirect_pairs";
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

var factory = new IdentityServerConfigurationDbContextFactory();
await using var dbContext = factory.CreateDbContext(args);

var existingPairs = await dbContext.ClientTenantRedirectUris
    .ToListAsync();

var existingMap = existingPairs.ToDictionary(
    x => $"{x.ClientId}|{x.TenantKey}",
    StringComparer.OrdinalIgnoreCase);

var propertyRows = await dbContext.ClientProperties
    .Where(x => x.Key == PropertyKey)
    .Select(x => new { x.ClientId, x.Value })
    .ToListAsync();

var created = 0;
var updated = 0;
var skipped = 0;

foreach (var propertyRow in propertyRows)
{
    if (string.IsNullOrWhiteSpace(propertyRow.Value))
    {
        continue;
    }

    List<TenantRedirectPairPayload>? pairs;
    try
    {
        pairs = JsonSerializer.Deserialize<List<TenantRedirectPairPayload>>(propertyRow.Value, jsonOptions);
    }
    catch (JsonException)
    {
        skipped++;
        continue;
    }

    foreach (var pair in pairs ?? Enumerable.Empty<TenantRedirectPairPayload>())
    {
        var tenantKey = pair.TenantKey?.Trim();
        var redirectUrl = pair.RedirectUrl?.Trim();

        if (string.IsNullOrWhiteSpace(tenantKey) || string.IsNullOrWhiteSpace(redirectUrl))
        {
            continue;
        }

        var mapKey = $"{propertyRow.ClientId}|{tenantKey}";
        if (existingMap.TryGetValue(mapKey, out var existing))
        {
            if (!string.Equals(existing.RedirectUrl, redirectUrl, StringComparison.OrdinalIgnoreCase))
            {
                existing.RedirectUrl = redirectUrl;
                updated++;
            }

            continue;
        }

        var entity = new ClientTenantRedirectUri
        {
            ClientId = propertyRow.ClientId,
            TenantKey = tenantKey,
            RedirectUrl = redirectUrl
        };

        dbContext.ClientTenantRedirectUris.Add(entity);
        existingMap[mapKey] = entity;
        created++;
    }
}

await dbContext.SaveChangesAsync();

Console.WriteLine($"Tenant redirect backfill complete. Created={created}; Updated={updated}; SkippedInvalidJson={skipped}; SourceRows={propertyRows.Count}; CurrentRows={existingMap.Count}");

internal sealed class TenantRedirectPairPayload
{
    public string? TenantKey { get; set; }

    public string? RedirectUrl { get; set; }
}
