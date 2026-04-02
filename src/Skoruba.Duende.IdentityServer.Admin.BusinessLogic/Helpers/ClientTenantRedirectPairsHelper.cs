// Copyright (c) Jan Ã…Â koruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Helpers
{
    internal static class ClientTenantRedirectPairsHelper
    {
        public const string PropertyKey = "skoruba_tenant_redirect_pairs";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static void PopulateFromStoredProperty(ClientDto client)
        {
            if (client == null)
            {
                return;
            }

            var storedProperty = FindStoredProperty(client.Properties);
            client.TenantRedirectPairs = Parse(storedProperty?.Value);
            client.Properties = RemoveStoredProperty(client.Properties);
        }

        public static void StripStoredProperty(ClientDto client)
        {
            if (client == null)
            {
                return;
            }

            client.Properties ??= new List<ClientPropertyDto>();
            client.Properties = RemoveStoredProperty(client.Properties);
        }

        public static void PopulateMissingPairValuesFromFlatLists(ClientDto client)
        {
            if (client == null)
            {
                return;
            }

            client.TenantRedirectPairs = Normalize(client.TenantRedirectPairs);
            if (client.TenantRedirectPairs.Count == 0)
            {
                return;
            }

            var postLogoutRedirectUris = NormalizeStrings(client.PostLogoutRedirectUris);
            var allowedCorsOrigins = NormalizeStrings(client.AllowedCorsOrigins);

            client.TenantRedirectPairs = client.TenantRedirectPairs
                .Select(pair => new ClientTenantRedirectPairDto
                {
                    TenantKey = pair.TenantKey,
                    SignInCallbackUrl = pair.SignInCallbackUrl,
                    SignOutCallbackUrl = NormalizeValue(pair.SignOutCallbackUrl)
                        ?? FindMatchingAbsoluteUrl(postLogoutRedirectUris, pair.SignInCallbackUrl),
                    CorsOrigin = NormalizeValue(pair.CorsOrigin)
                        ?? FindMatchingOrigin(allowedCorsOrigins, pair.SignInCallbackUrl)
                })
                .ToList();
        }

        public static void ApplyRedirectUris(ClientDto client)
        {
            if (client == null)
            {
                return;
            }

            client.TenantRedirectPairs = Normalize(client.TenantRedirectPairs);
            client.RedirectUris = MergeUrls(client.RedirectUris, client.TenantRedirectPairs.Select(x => x.SignInCallbackUrl));
            client.PostLogoutRedirectUris = MergeUrls(client.PostLogoutRedirectUris, client.TenantRedirectPairs.Select(x => x.SignOutCallbackUrl));
            client.AllowedCorsOrigins = MergeUrls(client.AllowedCorsOrigins, client.TenantRedirectPairs.Select(x => x.CorsOrigin));
        }

        public static void StripPairRedirectUris(ClientDto client)
        {
            if (client == null)
            {
                return;
            }

            client.TenantRedirectPairs = Normalize(client.TenantRedirectPairs);
            client.RedirectUris = RemoveTenantMappedValues(client.RedirectUris, client.TenantRedirectPairs.Select(x => x.SignInCallbackUrl));
            client.PostLogoutRedirectUris = RemoveTenantMappedValues(client.PostLogoutRedirectUris, client.TenantRedirectPairs.Select(x => x.SignOutCallbackUrl));
            client.AllowedCorsOrigins = RemoveTenantMappedValues(client.AllowedCorsOrigins, client.TenantRedirectPairs.Select(x => x.CorsOrigin));
        }

        public static List<ClientTenantRedirectPairDto> NormalizePairs(IEnumerable<ClientTenantRedirectPairDto> pairs)
        {
            return Normalize(pairs);
        }

        private static ClientPropertyDto FindStoredProperty(IEnumerable<ClientPropertyDto> properties)
        {
            return properties?.FirstOrDefault(IsStoredProperty);
        }

        private static bool IsStoredProperty(ClientPropertyDto property)
        {
            return property != null &&
                   string.Equals(property.Key, PropertyKey, StringComparison.Ordinal);
        }

        private static List<ClientPropertyDto> RemoveStoredProperty(IEnumerable<ClientPropertyDto> properties)
        {
            return properties?
                .Where(x => !IsStoredProperty(x))
                .ToList()
                   ?? new List<ClientPropertyDto>();
        }

        private static List<ClientTenantRedirectPairDto> Normalize(IEnumerable<ClientTenantRedirectPairDto> pairs)
        {
            var normalizedPairs = new List<ClientTenantRedirectPairDto>();
            var seenTenants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in pairs ?? Enumerable.Empty<ClientTenantRedirectPairDto>())
            {
                var tenantKey = NormalizeValue(pair?.TenantKey);
                var signInCallbackUrl = NormalizeValue(pair?.SignInCallbackUrl);
                var signOutCallbackUrl = NormalizeValue(pair?.SignOutCallbackUrl);
                var corsOrigin = NormalizeValue(pair?.CorsOrigin);

                if (string.IsNullOrWhiteSpace(tenantKey) ||
                    (string.IsNullOrWhiteSpace(signInCallbackUrl) &&
                     string.IsNullOrWhiteSpace(signOutCallbackUrl) &&
                     string.IsNullOrWhiteSpace(corsOrigin)))
                {
                    continue;
                }

                if (!seenTenants.Add(tenantKey))
                {
                    continue;
                }

                normalizedPairs.Add(new ClientTenantRedirectPairDto
                {
                    TenantKey = tenantKey,
                    SignInCallbackUrl = signInCallbackUrl,
                    SignOutCallbackUrl = signOutCallbackUrl,
                    CorsOrigin = corsOrigin
                });
            }

            return normalizedPairs;
        }

        private static List<ClientTenantRedirectPairDto> Parse(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new List<ClientTenantRedirectPairDto>();
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<List<StoredClientTenantRedirectPair>>(rawValue, SerializerOptions);
                return Normalize(parsed?.Select(x => x.ToDto()));
            }
            catch (JsonException)
            {
                return new List<ClientTenantRedirectPairDto>();
            }
        }

        private static List<string> MergeUrls(IEnumerable<string> currentValues, IEnumerable<string> pairValues)
        {
            return currentValues
                .Concat(pairValues ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> RemoveTenantMappedValues(IEnumerable<string> currentValues, IEnumerable<string> pairValues)
        {
            var tenantMappedValues = new HashSet<string>(
                pairValues?
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            return currentValues?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Where(x => !tenantMappedValues.Contains(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                   ?? new List<string>();
        }

        private static List<string> NormalizeStrings(IEnumerable<string> values)
        {
            return values?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                   ?? new List<string>();
        }

        private static string NormalizeValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string FindMatchingAbsoluteUrl(IEnumerable<string> candidates, string redirectUrl)
        {
            if (!TryGetUri(redirectUrl, out var redirectUri))
            {
                return null;
            }

            return candidates.FirstOrDefault(candidate =>
                TryGetUri(candidate, out var candidateUri) &&
                HasSameOrigin(candidateUri, redirectUri));
        }

        private static string FindMatchingOrigin(IEnumerable<string> candidates, string redirectUrl)
        {
            if (!TryGetUri(redirectUrl, out var redirectUri))
            {
                return null;
            }

            return candidates.FirstOrDefault(candidate =>
                TryGetUri(candidate, out var candidateUri) &&
                HasSameOrigin(candidateUri, redirectUri));
        }

        private static bool TryGetUri(string value, out Uri uri)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static bool HasSameOrigin(Uri left, Uri right)
        {
            return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
                   left.Port == right.Port;
        }

        private sealed class StoredClientTenantRedirectPair
        {
            public string TenantKey { get; set; }
            public string SignInCallbackUrl { get; set; }
            [JsonPropertyName("redirectUrl")]
            public string LegacySignInCallbackUrl { get; set; }
            public string SignOutCallbackUrl { get; set; }
            [JsonPropertyName("postLogoutRedirectUrl")]
            public string LegacySignOutCallbackUrl { get; set; }
            public string CorsOrigin { get; set; }

            public ClientTenantRedirectPairDto ToDto()
            {
                return new ClientTenantRedirectPairDto
                {
                    TenantKey = TenantKey,
                    SignInCallbackUrl = NormalizeValue(SignInCallbackUrl) ?? NormalizeValue(LegacySignInCallbackUrl),
                    SignOutCallbackUrl = NormalizeValue(SignOutCallbackUrl) ?? NormalizeValue(LegacySignOutCallbackUrl),
                    CorsOrigin = NormalizeValue(CorsOrigin)
                };
            }
        }
    }
}


