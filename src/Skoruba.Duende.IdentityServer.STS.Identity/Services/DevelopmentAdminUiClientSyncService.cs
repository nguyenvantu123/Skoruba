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
    public class DevelopmentAdminUiClientSyncService
    {
        private static readonly StringComparer UriComparer = StringComparer.OrdinalIgnoreCase;

        private readonly IdentityServerConfigurationDbContext _configurationDbContext;
        private readonly AdminConfiguration _adminConfiguration;
        private readonly ILogger<DevelopmentAdminUiClientSyncService> _logger;

        public DevelopmentAdminUiClientSyncService(
            IdentityServerConfigurationDbContext configurationDbContext,
            AdminConfiguration adminConfiguration,
            ILogger<DevelopmentAdminUiClientSyncService> logger)
        {
            _configurationDbContext = configurationDbContext;
            _adminConfiguration = adminConfiguration;
            _logger = logger;
        }

        public async Task SyncAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_adminConfiguration.IdentityAdminClientId) ||
                string.IsNullOrWhiteSpace(_adminConfiguration.IdentityAdminBaseUrl))
            {
                _logger.LogInformation(
                    "Skipping admin UI client sync because IdentityAdminClientId or IdentityAdminBaseUrl is not configured.");
                return;
            }

            if (!TryBuildExpectedUris(
                    _adminConfiguration.IdentityAdminBaseUrl,
                    out var clientUri,
                    out var redirectUri,
                    out var frontChannelLogoutUri,
                    out var postLogoutRedirectUri,
                    out var corsOrigin))
            {
                _logger.LogWarning(
                    "Skipping admin UI client sync because IdentityAdminBaseUrl '{IdentityAdminBaseUrl}' is invalid.",
                    _adminConfiguration.IdentityAdminBaseUrl);
                return;
            }

            var client = await _configurationDbContext.Clients
                .Include(x => x.RedirectUris)
                .Include(x => x.PostLogoutRedirectUris)
                .Include(x => x.AllowedCorsOrigins)
                .SingleOrDefaultAsync(
                    x => x.ClientId == _adminConfiguration.IdentityAdminClientId,
                    cancellationToken);

            if (client == null)
            {
                _logger.LogWarning(
                    "Skipping admin UI client sync because client '{ClientId}' was not found.",
                    _adminConfiguration.IdentityAdminClientId);
                return;
            }

            var desiredRedirectUris = new[] { redirectUri };
            var desiredPostLogoutRedirectUris = new[] { postLogoutRedirectUri };
            var desiredCorsOrigins = new[] { corsOrigin };

            var changed = false;

            if (!UriComparer.Equals(client.ClientUri, clientUri))
            {
                client.ClientUri = clientUri;
                changed = true;
            }

            if (!UriComparer.Equals(client.FrontChannelLogoutUri, frontChannelLogoutUri))
            {
                client.FrontChannelLogoutUri = frontChannelLogoutUri;
                changed = true;
            }

            if (client.RequirePushedAuthorization)
            {
                client.RequirePushedAuthorization = false;
                changed = true;
            }

            changed |= ReplaceRedirectUris(client.RedirectUris, desiredRedirectUris);
            changed |= ReplacePostLogoutRedirectUris(client.PostLogoutRedirectUris, desiredPostLogoutRedirectUris);
            changed |= ReplaceCorsOrigins(client.AllowedCorsOrigins, desiredCorsOrigins);

            if (!changed)
            {
                _logger.LogInformation(
                    "Admin UI client '{ClientId}' is already synchronized to '{ClientUri}'.",
                    client.ClientId,
                    clientUri);
                return;
            }

            client.Updated = DateTime.UtcNow;

            await _configurationDbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Synchronized admin UI client '{ClientId}' to '{ClientUri}' with redirect '{RedirectUri}'.",
                client.ClientId,
                clientUri,
                redirectUri);
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
            out string clientUri,
            out string redirectUri,
            out string frontChannelLogoutUri,
            out string postLogoutRedirectUri,
            out string corsOrigin)
        {
            clientUri = null;
            redirectUri = null;
            frontChannelLogoutUri = null;
            postLogoutRedirectUri = null;
            corsOrigin = null;

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var adminBaseUri))
            {
                return false;
            }

            var pathBase = adminBaseUri.AbsolutePath?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(pathBase) || pathBase == "/")
            {
                pathBase = string.Empty;
            }

            clientUri = $"{adminBaseUri.Scheme}://{adminBaseUri.Authority}{pathBase}";
            redirectUri = $"{clientUri}/signin-oidc";
            frontChannelLogoutUri = $"{clientUri}/signout-oidc";
            postLogoutRedirectUri = $"{clientUri}/signout-callback-oidc";
            corsOrigin = $"{adminBaseUri.Scheme}://{adminBaseUri.Authority}";

            return true;
        }
    }
}
