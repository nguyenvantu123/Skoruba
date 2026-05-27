// Feature: tenant-client-cache-expansion, Task 10
//
// In-memory IClientService double used by the integration tests. The hard
// rule from AGENTS.md is "integration tests run against the in-process
// WebApplicationFactory" — this double satisfies that contract by replacing
// the EF-backed implementation while the rest of the controller pipeline
// (model binding, routing, authentication, ITenantClientCacheService,
// IClientScopeCacheService, IClientTenantScopeResolver) is wired live.
//
// The test plan (Task 10 in tasks.md) explicitly recommends a fast in-memory
// ITenantRepository test double instead of seeding a real database — the
// same reasoning applies to IClientService for the CRUD flows we drive
// from the HTTP layer.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Dtos.Common;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;

internal sealed class InMemoryClientStore : IClientService
{
    private readonly ConcurrentDictionary<int, ClientDto> _byId = new();
    private int _nextId;

    /// <summary>Direct access for tests to seed/inspect the in-memory store.</summary>
    public IReadOnlyDictionary<int, ClientDto> Snapshot => _byId;

    public ClientDto Seed(ClientDto client)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        var id = client.Id != 0 ? client.Id : Interlocked.Increment(ref _nextId);
        client.Id = id;
        var clone = Clone(client);
        _byId[id] = clone;
        if (id > _nextId)
        {
            Interlocked.Exchange(ref _nextId, id);
        }
        return clone;
    }

    public Task<int> AddClientAsync(ClientDto client)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        var id = Interlocked.Increment(ref _nextId);
        client.Id = id;
        _byId[id] = Clone(client);
        return Task.FromResult(id);
    }

    public Task<int> UpdateClientAsync(
        ClientDto client,
        bool updateClientClaims = false,
        bool updateClientProperties = false)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        _byId[client.Id] = Clone(client);
        return Task.FromResult(client.Id);
    }

    public Task<int> RemoveClientAsync(ClientDto client)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        _byId.TryRemove(client.Id, out _);
        return Task.FromResult(client.Id);
    }

    public Task<int> CloneClientAsync(ClientCloneDto client)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (!_byId.TryGetValue(client.Id, out var source))
        {
            throw new InvalidOperationException($"Client {client.Id} not found.");
        }
        var clone = Clone(source);
        clone.Id = Interlocked.Increment(ref _nextId);
        clone.ClientId = client.ClientId;
        clone.ClientName = client.ClientName;
        _byId[clone.Id] = clone;
        return Task.FromResult(clone.Id);
    }

    public Task<bool> CanInsertClientAsync(ClientDto client, bool isCloned = false)
        => Task.FromResult(!_byId.Values.Any(c => string.Equals(c.ClientId, client.ClientId, StringComparison.Ordinal)));

    public Task<ClientDto> GetClientAsync(int clientId)
    {
        if (!_byId.TryGetValue(clientId, out var dto))
        {
            throw new InvalidOperationException($"Client {clientId} not found.");
        }
        return Task.FromResult(Clone(dto));
    }

    public Task<IReadOnlyList<int>> ListClientPrimaryKeysForTenantAsync(
        string tenantKey,
        int max,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantKey))
            throw new ArgumentException("tenantKey required", nameof(tenantKey));
        if (max <= 0)
            throw new ArgumentOutOfRangeException(nameof(max));

        var normalized = tenantKey.Trim();
        var ids = _byId.Values
            .Where(c => c.TenantRedirectPairs != null
                        && c.TenantRedirectPairs.Any(p =>
                            string.Equals(p.TenantKey, normalized, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Id)
            .OrderBy(id => id)
            .Take(max + 1)
            .ToArray();
        return Task.FromResult<IReadOnlyList<int>>(ids);
    }

    public Task<ClientsDto> GetClientsAsync(string search, int page = 1, int pageSize = 10)
    {
        var dto = new ClientsDto
        {
            PageSize = pageSize,
            TotalCount = _byId.Count,
        };
        return Task.FromResult(dto);
    }

    // ----- Methods unused by the cache-integration tests below -----

    public ClientDto BuildClientViewModel(ClientDto client = null) => client ?? new ClientDto();

    public ClientSecretsDto BuildClientSecretsViewModel(ClientSecretsDto clientSecrets) => clientSecrets;

    public ClientCloneDto BuildClientCloneViewModel(int id, ClientDto clientDto) => new() { Id = id };

    public Task<List<string>> GetScopesAsync(string scope, int limit = 0, bool excludeIdentityResources = false, bool excludeApiScopes = false)
        => Task.FromResult(new List<string>());

    public List<SelectItemDto> GetGrantTypes(string grant, bool includeObsoleteGrants, int limit = 0) => new();
    public List<SelectItemDto> GetAccessTokenTypes() => new();
    public List<SelectItemDto> GetTokenExpirations() => new();
    public List<SelectItemDto> GetTokenUsage() => new();
    public List<SelectItemDto> GetHashTypes() => new();
    public List<SelectItemDto> GetSecretTypes() => new();
    public List<string> GetStandardClaims(string claim, int limit = 0) => new();

    public Task<int> AddClientSecretAsync(ClientSecretsDto clientSecret) => Task.FromResult(0);
    public Task<int> DeleteClientSecretAsync(ClientSecretsDto clientSecret) => Task.FromResult(0);
    public Task<ClientSecretsDto> GetClientSecretsAsync(int clientId, int page = 1, int pageSize = 10) => Task.FromResult(new ClientSecretsDto());
    public Task<ClientSecretsDto> GetClientSecretAsync(int clientSecretId) => Task.FromResult(new ClientSecretsDto());

    public Task<ClientClaimsDto> GetClientClaimsAsync(int clientId, int page = 1, int pageSize = 10) => Task.FromResult(new ClientClaimsDto());
    public Task<ClientPropertiesDto> GetClientPropertiesAsync(int clientId, int page = 1, int pageSize = 10) => Task.FromResult(new ClientPropertiesDto());
    public Task<ClientClaimsDto> GetClientClaimAsync(int clientClaimId) => Task.FromResult(new ClientClaimsDto());
    public Task<ClientPropertiesDto> GetClientPropertyAsync(int clientPropertyId) => Task.FromResult(new ClientPropertiesDto());

    public Task<int> AddClientClaimAsync(ClientClaimsDto clientClaim) => Task.FromResult(0);
    public Task<int> AddClientPropertyAsync(ClientPropertiesDto clientProperties) => Task.FromResult(0);
    public Task<int> DeleteClientClaimAsync(ClientClaimsDto clientClaim) => Task.FromResult(0);
    public Task<int> DeleteClientPropertyAsync(ClientPropertiesDto clientProperty) => Task.FromResult(0);

    public List<string> GetSigningAlgorithms(string algorithm, int limit = 0) => new();
    public List<SelectItemDto> GetProtocolTypes() => new();
    public List<SelectItemDto> GetDPoPValidationModes() => new();

    private static ClientDto Clone(ClientDto src)
    {
        // Shallow-clone is enough for the cache feature: the cache service
        // serializes the DTO immediately, so the test doesn't need a deep
        // clone of every collection.
        var copy = new ClientDto
        {
            Id = src.Id,
            ClientId = src.ClientId,
            ClientName = src.ClientName,
            Description = src.Description,
            Enabled = src.Enabled,
            EnableLocalLogin = src.EnableLocalLogin,
            ProtocolType = src.ProtocolType ?? "oidc",
            AccessTokenLifetime = src.AccessTokenLifetime,
            IdentityTokenLifetime = src.IdentityTokenLifetime,
            AbsoluteRefreshTokenLifetime = src.AbsoluteRefreshTokenLifetime,
            AccessTokenType = src.AccessTokenType,
            AllowOfflineAccess = src.AllowOfflineAccess,
            RequirePkce = src.RequirePkce,
            RequireClientSecret = src.RequireClientSecret,
            RequireConsent = src.RequireConsent,
            ClientUri = src.ClientUri,
            LogoUri = src.LogoUri,
            ClientClaimsPrefix = src.ClientClaimsPrefix,
            FrontChannelLogoutUri = src.FrontChannelLogoutUri,
            BackChannelLogoutUri = src.BackChannelLogoutUri,
            FrontChannelLogoutSessionRequired = src.FrontChannelLogoutSessionRequired,
            BackChannelLogoutSessionRequired = src.BackChannelLogoutSessionRequired,
            UpdateAccessTokenClaimsOnRefresh = src.UpdateAccessTokenClaimsOnRefresh,
            CoordinateLifetimeWithUserSession = src.CoordinateLifetimeWithUserSession,
            AllowAccessTokensViaBrowser = src.AllowAccessTokensViaBrowser,
            AllowPlainTextPkce = src.AllowPlainTextPkce,
            AllowRememberConsent = src.AllowRememberConsent,
            AlwaysIncludeUserClaimsInIdToken = src.AlwaysIncludeUserClaimsInIdToken,
            AlwaysSendClientClaims = src.AlwaysSendClientClaims,
            AuthorizationCodeLifetime = src.AuthorizationCodeLifetime,
            ConsentLifetime = src.ConsentLifetime,
            RefreshTokenExpiration = src.RefreshTokenExpiration,
            RefreshTokenUsage = src.RefreshTokenUsage,
            SlidingRefreshTokenLifetime = src.SlidingRefreshTokenLifetime,
            IncludeJwtId = src.IncludeJwtId,
            UserSsoLifetime = src.UserSsoLifetime,
            UserCodeType = src.UserCodeType,
            DeviceCodeLifetime = src.DeviceCodeLifetime,
            RequireRequestObject = src.RequireRequestObject,
            CibaLifetime = src.CibaLifetime,
            PollingInterval = src.PollingInterval,
            RequireDPoP = src.RequireDPoP,
            DPoPValidationMode = src.DPoPValidationMode,
            DPoPClockSkew = src.DPoPClockSkew,
            PushedAuthorizationLifetime = src.PushedAuthorizationLifetime,
            RequirePushedAuthorization = src.RequirePushedAuthorization,
            InitiateLoginUri = src.InitiateLoginUri,
            NonEditable = src.NonEditable,
            ClientType = src.ClientType,
            Updated = src.Updated,
            LastAccessed = src.LastAccessed,
            RedirectUris = new List<string>(src.RedirectUris ?? new List<string>()),
            PostLogoutRedirectUris = new List<string>(src.PostLogoutRedirectUris ?? new List<string>()),
            AllowedCorsOrigins = new List<string>(src.AllowedCorsOrigins ?? new List<string>()),
            AllowedGrantTypes = new List<string>(src.AllowedGrantTypes ?? new List<string>()),
            AllowedScopes = new List<string>(src.AllowedScopes ?? new List<string>()),
            AllowedIdentityTokenSigningAlgorithms = new List<string>(src.AllowedIdentityTokenSigningAlgorithms ?? new List<string>()),
            IdentityProviderRestrictions = new List<string>(src.IdentityProviderRestrictions ?? new List<string>()),
        };
        if (src.TenantRedirectPairs != null)
        {
            foreach (var p in src.TenantRedirectPairs)
            {
                copy.TenantRedirectPairs.Add(new ClientTenantRedirectPairDto
                {
                    TenantKey = p.TenantKey,
                    SignInCallbackUrl = p.SignInCallbackUrl,
                    SignOutCallbackUrl = p.SignOutCallbackUrl,
                    CorsOrigin = p.CorsOrigin,
                });
            }
        }
        return copy;
    }
}
