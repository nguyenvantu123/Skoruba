using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityServer.Models;
using Microsoft.EntityFrameworkCore;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Constants;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

public interface IClientTenantRedirectResolver
{
    Task<string?> ResolveRedirectUrlAsync(Client? client, string? tenantKey, CancellationToken cancellationToken = default);

    Task<string?> ResolvePostLogoutRedirectUrlAsync(Client? client, string? tenantKey, CancellationToken cancellationToken = default);
}

internal sealed class ClientTenantRedirectResolver : IClientTenantRedirectResolver
{
    private const string TenantRedirectPairsPropertyKey = "skoruba_tenant_redirect_pairs";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IdentityServerConfigurationDbContext _configurationDbContext;

    public ClientTenantRedirectResolver(IdentityServerConfigurationDbContext configurationDbContext)
    {
        _configurationDbContext = configurationDbContext;
    }

    public async Task<string?> ResolveRedirectUrlAsync(Client? client, string? tenantKey, CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            return null;
        }

        var useTenantRedirectPairs = await GetUseTenantRedirectPairsAsync(client, cancellationToken);
        if (!useTenantRedirectPairs)
        {
            return GetFirstValidUrl(client.RedirectUris);
        }

        var mapping = await ResolveTenantMappingAsync(client, tenantKey, cancellationToken);
        return GetFirstValidUrl(mapping?.SignInCallbackUrl);
    }

    public async Task<string?> ResolvePostLogoutRedirectUrlAsync(Client? client, string? tenantKey, CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            return null;
        }

        var useTenantRedirectPairs = await GetUseTenantRedirectPairsAsync(client, cancellationToken);
        if (!useTenantRedirectPairs)
        {
            return GetFirstValidUrl(client.PostLogoutRedirectUris);
        }

        var mapping = await ResolveTenantMappingAsync(client, tenantKey, cancellationToken);
        return GetFirstValidUrl(mapping?.SignOutCallbackUrl);
    }

    private async Task<bool> GetUseTenantRedirectPairsAsync(Client client, CancellationToken cancellationToken)
    {
        return await _configurationDbContext.Clients
            .AsNoTracking()
            .Where(x => x.ClientId == client.ClientId)
            .Select(x => EF.Property<bool>(x, ClientTenantRedirectModeConsts.UseTenantRedirectPairsPropertyName))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<ClientTenantRedirectPair?> ResolveTenantMappingAsync(Client client, string? tenantKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            return null;
        }

        var normalizedTenantKey = tenantKey.Trim();

        var databaseMapping = await _configurationDbContext.ClientTenantRedirectUris
            .AsNoTracking()
            .Where(x => x.Client.ClientId == client.ClientId &&
                        x.TenantKey == normalizedTenantKey)
            .Select(x => new ClientTenantRedirectPair
            {
                TenantKey = x.TenantKey,
                SignInCallbackUrl = x.SignInCallbackUrl,
                SignOutCallbackUrl = x.SignOutCallbackUrl,
                CorsOrigin = x.CorsOrigin
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (databaseMapping != null)
        {
            return databaseMapping;
        }

        if (client.Properties == null ||
            !client.Properties.TryGetValue(TenantRedirectPairsPropertyKey, out var rawValue) ||
            string.IsNullOrWhiteSpace(rawValue))
        {
            return databaseMapping;
        }

        try
        {
            var pairs = JsonSerializer.Deserialize<List<ClientTenantRedirectPair>>(rawValue, SerializerOptions);
            var legacyPair = pairs?
                .FirstOrDefault(x => string.Equals(x.TenantKey?.Trim(), normalizedTenantKey, StringComparison.OrdinalIgnoreCase));
            if (legacyPair == null)
            {
                return null;
            }

            return new ClientTenantRedirectPair
            {
                TenantKey = legacyPair.TenantKey,
                SignInCallbackUrl = legacyPair.GetNormalizedSignInCallbackUrl(),
                SignOutCallbackUrl = legacyPair.GetNormalizedSignOutCallbackUrl(),
                CorsOrigin = legacyPair.CorsOrigin
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetFirstValidUrl(IEnumerable<string>? urls)
    {
        if (urls == null)
        {
            return null;
        }

        foreach (var url in urls)
        {
            if (TryValidateAbsoluteHttpUrl(url, out var validatedRedirectUrl))
            {
                return validatedRedirectUrl;
            }
        }

        return null;
    }

    private static string? GetFirstValidUrl(string? url)
    {
        return TryValidateAbsoluteHttpUrl(url, out var validatedRedirectUrl)
            ? validatedRedirectUrl
            : null;
    }

    private static bool TryValidateAbsoluteHttpUrl(string? redirectUrl, out string validatedRedirectUrl)
    {
        validatedRedirectUrl = null;

        if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        validatedRedirectUrl = redirectUrl?.Trim();
        return true;
    }

    private sealed class ClientTenantRedirectPair
    {
        public string? TenantKey { get; set; }
        public string? SignInCallbackUrl { get; set; }
        [JsonPropertyName("redirectUrl")]
        public string? LegacySignInCallbackUrl { get; set; }
        public string? SignOutCallbackUrl { get; set; }
        [JsonPropertyName("postLogoutRedirectUrl")]
        public string? LegacySignOutCallbackUrl { get; set; }
        public string? CorsOrigin { get; set; }

        public string? GetNormalizedSignInCallbackUrl()
        {
            return string.IsNullOrWhiteSpace(SignInCallbackUrl)
                ? LegacySignInCallbackUrl?.Trim()
                : SignInCallbackUrl.Trim();
        }

        public string? GetNormalizedSignOutCallbackUrl()
        {
            return string.IsNullOrWhiteSpace(SignOutCallbackUrl)
                ? LegacySignOutCallbackUrl?.Trim()
                : SignOutCallbackUrl.Trim();
        }
    }
}
