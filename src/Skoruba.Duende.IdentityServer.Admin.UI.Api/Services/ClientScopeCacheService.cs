using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services;

public sealed class ClientScopeCacheService : IClientScopeCacheService
{
    private readonly IDistributedCache _distributedCache;
    private readonly ILogger<ClientScopeCacheService> _logger;

    public ClientScopeCacheService(
        IDistributedCache distributedCache,
        ILogger<ClientScopeCacheService> logger)
    {
        _distributedCache = distributedCache;
        _logger = logger;
    }

    public async Task SaveAllowedScopesAsync(string clientId, IEnumerable<string>? allowedScopes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _logger.LogWarning("Skipping client scope cache synchronization because the client identifier is empty.");
            return;
        }

        var serializedScopes = SerializeScopes(allowedScopes);

        try
        {
            await _distributedCache.SetStringAsync(clientId.Trim(), serializedScopes, cancellationToken);
            _logger.LogInformation(
                "Synchronized allowed scopes for client {ClientId} into distributed cache. SerializedScopes={SerializedScopes}",
                clientId,
                serializedScopes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to synchronize allowed scopes for client {ClientId} into distributed cache.",
                clientId);
        }
    }

    public async Task RemoveAllowedScopesAsync(string clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        try
        {
            await _distributedCache.RemoveAsync(clientId.Trim(), cancellationToken);
            _logger.LogInformation(
                "Removed distributed cache entry for client {ClientId} after the client was deleted.",
                clientId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to remove distributed cache entry for client {ClientId}.",
                clientId);
        }
    }

    private static string SerializeScopes(IEnumerable<string>? allowedScopes)
    {
        if (allowedScopes == null)
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            allowedScopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Select(scope => scope.Trim())
                .Distinct(StringComparer.Ordinal));
    }
}
