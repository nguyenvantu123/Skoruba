using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityServer.EntityFramework.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services
{
    public sealed class DevelopmentOidcClientSyncService
    {
        private static readonly StringComparer UriComparer = StringComparer.OrdinalIgnoreCase;

        private readonly IdentityServerConfigurationDbContext _configurationDbContext;
        private readonly DevelopmentOidcClientSyncConfiguration _configuration;
        private readonly ILogger<DevelopmentOidcClientSyncService> _logger;

        public DevelopmentOidcClientSyncService(
            IdentityServerConfigurationDbContext configurationDbContext,
            DevelopmentOidcClientSyncConfiguration configuration,
            ILogger<DevelopmentOidcClientSyncService> logger)
        {
            _configurationDbContext = configurationDbContext;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SyncAsync(CancellationToken cancellationToken = default)
        {
            if (!_configuration.Enabled || _configuration.Clients.Count == 0)
            {
                _logger.LogInformation("Skipping development OIDC client sync because it is disabled or has no targets.");
                return;
            }

            foreach (var target in _configuration.Clients)
            {
                await SyncClientAsync(target, cancellationToken);
            }
        }

        private async Task SyncClientAsync(
            DevelopmentOidcClientSyncConfiguration.ClientSyncTarget target,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(target.ClientId))
            {
                _logger.LogWarning("Skipping development OIDC client sync for an entry without ClientId.");
                return;
            }

            var normalizedBaseUrls = target.BaseUrls
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(UriComparer)
                .ToArray();

            if (normalizedBaseUrls.Length == 0)
            {
                _logger.LogWarning(
                    "Skipping development OIDC client sync for client '{ClientId}' because no BaseUrls were configured.",
                    target.ClientId);
                return;
            }

            var redirectUris = new List<string>();
            var postLogoutRedirectUris = new List<string>();
            var corsOrigins = new List<string>();

            foreach (var baseUrl in normalizedBaseUrls)
            {
                if (!TryBuildExpectedUris(baseUrl, out var redirectUri, out var postLogoutRedirectUri, out var corsOrigin))
                {
                    _logger.LogWarning(
                        "Skipping invalid BaseUrl '{BaseUrl}' while synchronizing client '{ClientId}'.",
                        baseUrl,
                        target.ClientId);
                    continue;
                }

                redirectUris.Add(redirectUri);
                postLogoutRedirectUris.Add(postLogoutRedirectUri);
                corsOrigins.Add(corsOrigin);
            }

            if (redirectUris.Count == 0)
            {
                _logger.LogWarning(
                    "Skipping development OIDC client sync for client '{ClientId}' because no valid BaseUrls remained after validation.",
                    target.ClientId);
                return;
            }

            var client = await _configurationDbContext.Clients
                .Include(x => x.RedirectUris)
                .Include(x => x.PostLogoutRedirectUris)
                .Include(x => x.AllowedCorsOrigins)
                .SingleOrDefaultAsync(x => x.ClientId == target.ClientId, cancellationToken);

            if (client == null)
            {
                _logger.LogWarning(
                    "Skipping development OIDC client sync because client '{ClientId}' was not found.",
                    target.ClientId);
                return;
            }

            var changed = false;

            changed |= ReplaceRedirectUris(client.RedirectUris, redirectUris);
            changed |= ReplacePostLogoutRedirectUris(client.PostLogoutRedirectUris, postLogoutRedirectUris);
            changed |= ReplaceCorsOrigins(client.AllowedCorsOrigins, corsOrigins);

            if (!changed)
            {
                _logger.LogInformation(
                    "Development OIDC client '{ClientId}' is already synchronized for {Count} base url(s).",
                    client.ClientId,
                    redirectUris.Count);
                return;
            }

            client.Updated = DateTime.UtcNow;

            await _configurationDbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Synchronized development OIDC client '{ClientId}' with redirect URIs: {RedirectUris}",
                client.ClientId,
                string.Join(", ", redirectUris));
        }

        private static bool ReplaceRedirectUris(ICollection<ClientRedirectUri> collection, IReadOnlyCollection<string> desiredValues)
        {
            var currentValues = collection.Select(x => x.RedirectUri).ToArray();
            if (SetEquals(currentValues, desiredValues))
            {
                return false;
            }

            collection.Clear();
            foreach (var value in desiredValues)
            {
                collection.Add(new ClientRedirectUri { RedirectUri = value });
            }

            return true;
        }

        private static bool ReplacePostLogoutRedirectUris(
            ICollection<ClientPostLogoutRedirectUri> collection,
            IReadOnlyCollection<string> desiredValues)
        {
            var currentValues = collection.Select(x => x.PostLogoutRedirectUri).ToArray();
            if (SetEquals(currentValues, desiredValues))
            {
                return false;
            }

            collection.Clear();
            foreach (var value in desiredValues)
            {
                collection.Add(new ClientPostLogoutRedirectUri { PostLogoutRedirectUri = value });
            }

            return true;
        }

        private static bool ReplaceCorsOrigins(ICollection<ClientCorsOrigin> collection, IReadOnlyCollection<string> desiredValues)
        {
            var currentValues = collection.Select(x => x.Origin).ToArray();
            if (SetEquals(currentValues, desiredValues))
            {
                return false;
            }

            collection.Clear();
            foreach (var value in desiredValues)
            {
                collection.Add(new ClientCorsOrigin { Origin = value });
            }

            return true;
        }

        private static bool SetEquals(IEnumerable<string> currentValues, IReadOnlyCollection<string> desiredValues)
        {
            return new HashSet<string>(currentValues.Where(x => !string.IsNullOrWhiteSpace(x)), UriComparer)
                .SetEquals(desiredValues.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static bool TryBuildExpectedUris(
            string baseUrl,
            out string redirectUri,
            out string postLogoutRedirectUri,
            out string corsOrigin)
        {
            redirectUri = null;
            postLogoutRedirectUri = null;
            corsOrigin = null;

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                return false;
            }

            var pathBase = baseUri.AbsolutePath?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(pathBase) || pathBase == "/")
            {
                pathBase = string.Empty;
            }

            var normalizedBaseUrl = $"{baseUri.Scheme}://{baseUri.Authority}{pathBase}";
            redirectUri = $"{normalizedBaseUrl}/signin-oidc";
            postLogoutRedirectUri = $"{normalizedBaseUrl}/signout-callback-oidc";
            corsOrigin = $"{baseUri.Scheme}://{baseUri.Authority}";

            return true;
        }
    }
}
