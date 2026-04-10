namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services;

public interface IClientScopeCacheService
{
    Task SaveAllowedScopesAsync(string clientId, IEnumerable<string>? allowedScopes, CancellationToken cancellationToken);

    Task RemoveAllowedScopesAsync(string clientId, CancellationToken cancellationToken);
}
