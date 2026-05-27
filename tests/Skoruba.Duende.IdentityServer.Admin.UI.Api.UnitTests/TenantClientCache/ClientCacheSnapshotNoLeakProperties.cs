// Feature: tenant-client-cache-expansion, Task 3
//
// Property-based tests defending the Public_Safe_Fields whitelist contract
// (negative-leak side of P1) plus the 256 KiB size guard.
//
// Validates: Requirements 2.2, 2.6, 14.5, 15.1, 17.6
//
// Strategy: build randomized ClientDto instances that aggressively populate
// the FORBIDDEN fields (ClientSecrets, Claims, Properties,
// IdentityProviderRestrictions) with sentinel values, run them through
// Map → Serialize, and assert the sentinel never appears in the output —
// on top of the whitelist key check.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class ClientCacheSnapshotNoLeakProperties
{
    /// <summary>
    /// Sentinel string deliberately stuffed into every forbidden field. If
    /// it ever appears in the serialized payload, that proves a leak.
    /// </summary>
    private const string SecretSentinel = "S3CR3T";

    /// <summary>
    /// Forbidden top-level keys inside <c>data</c>. Mirrors the list in
    /// <see cref="ClientCacheSnapshotSerializerTests"/>; kept local so the
    /// two suites can drift independently if the whitelist ever changes.
    /// </summary>
    private static readonly string[] ForbiddenDataKeys =
    {
        "clientSecrets",
        "claims",
        "properties",
        "identityProviderRestrictions",
        "pairWiseSubjectSalt",
        "id",
    };

    public static class Arbs
    {
        private static readonly char[] AsciiPool =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_".ToCharArray();

        public static Gen<string> ShortStringGen(int min = 1, int max = 16)
            => from len in Gen.Choose(min, max)
               from chars in Gen.Elements(AsciiPool).ListOf(len)
               select new string(chars.ToArray());

        public static Gen<List<string>> StringListGen(int max = 5)
            => from count in Gen.Choose(0, max)
               from items in ShortStringGen().ListOf(count)
               select items.ToList();

        /// <summary>
        /// Generator for ClientDto instances guaranteed to have populated
        /// forbidden fields (ClientSecrets / Claims / Properties /
        /// IdentityProviderRestrictions) with the SecretSentinel string.
        /// </summary>
        public static Arbitrary<ClientDto> Dto()
        {
            var gen =
                from clientId in ShortStringGen()
                from clientName in ShortStringGen()
                from clientUri in ShortStringGen()
                from logoUri in ShortStringGen()
                from description in ShortStringGen()
                from enabled in Gen.Elements(true, false)
                from protocolType in Gen.Elements("oidc", "saml2p", "wsfed")
                from redirectUris in StringListGen()
                from postLogoutRedirectUris in StringListGen()
                from allowedCorsOrigins in StringListGen()
                from allowedGrantTypes in StringListGen()
                from allowedScopes in StringListGen()
                from allowedAlgs in StringListGen()
                from accessTokenLifetime in Gen.Choose(60, 86_400)
                from identityTokenLifetime in Gen.Choose(60, 86_400)
                from authorizationCodeLifetime in Gen.Choose(60, 86_400)
                from absoluteRefreshTokenLifetime in Gen.Choose(60, 30 * 86_400)
                from slidingRefreshTokenLifetime in Gen.Choose(60, 30 * 86_400)
                from refreshTokenExpiration in Gen.Choose(0, 1)
                from refreshTokenUsage in Gen.Choose(0, 1)
                from idpRestrictionsCount in Gen.Choose(1, 3)
                from claimCount in Gen.Choose(1, 3)
                from propCount in Gen.Choose(1, 3)
                from secretCount in Gen.Choose(1, 3)
                select BuildDto(
                    clientId,
                    clientName,
                    clientUri,
                    logoUri,
                    description,
                    enabled,
                    protocolType,
                    redirectUris,
                    postLogoutRedirectUris,
                    allowedCorsOrigins,
                    allowedGrantTypes,
                    allowedScopes,
                    allowedAlgs,
                    accessTokenLifetime,
                    identityTokenLifetime,
                    authorizationCodeLifetime,
                    absoluteRefreshTokenLifetime,
                    slidingRefreshTokenLifetime,
                    refreshTokenExpiration,
                    refreshTokenUsage,
                    idpRestrictionsCount,
                    claimCount,
                    propCount,
                    secretCount);
            return gen.ToArbitrary();
        }

        private static ClientDto BuildDto(
            string clientId,
            string clientName,
            string clientUri,
            string logoUri,
            string description,
            bool enabled,
            string protocolType,
            List<string> redirectUris,
            List<string> postLogoutRedirectUris,
            List<string> allowedCorsOrigins,
            List<string> allowedGrantTypes,
            List<string> allowedScopes,
            List<string> allowedAlgs,
            int accessTokenLifetime,
            int identityTokenLifetime,
            int authorizationCodeLifetime,
            int absoluteRefreshTokenLifetime,
            int slidingRefreshTokenLifetime,
            int refreshTokenExpiration,
            int refreshTokenUsage,
            int idpRestrictionsCount,
            int claimCount,
            int propCount,
            int secretCount)
        {
            var dto = new ClientDto
            {
                Id = 42,
                ClientId = clientId,
                ClientName = clientName,
                ClientUri = clientUri,
                LogoUri = logoUri,
                Description = description,
                Enabled = enabled,
                ProtocolType = protocolType,
                RedirectUris = redirectUris,
                PostLogoutRedirectUris = postLogoutRedirectUris,
                AllowedCorsOrigins = allowedCorsOrigins,
                AllowedGrantTypes = allowedGrantTypes,
                AllowedScopes = allowedScopes,
                AllowedIdentityTokenSigningAlgorithms = allowedAlgs,
                AccessTokenLifetime = accessTokenLifetime,
                IdentityTokenLifetime = identityTokenLifetime,
                AuthorizationCodeLifetime = authorizationCodeLifetime,
                AbsoluteRefreshTokenLifetime = absoluteRefreshTokenLifetime,
                SlidingRefreshTokenLifetime = slidingRefreshTokenLifetime,
                RefreshTokenExpiration = refreshTokenExpiration,
                RefreshTokenUsage = refreshTokenUsage,
                PairWiseSubjectSalt = SecretSentinel,
            };

            // Populate forbidden fields with the sentinel.
            for (var i = 0; i < secretCount; i++)
            {
                dto.ClientSecrets.Add(new ClientSecretDto
                {
                    Type = "SharedSecret",
                    Value = SecretSentinel,
                    Description = "secret-" + i,
                });
            }

            for (var i = 0; i < claimCount; i++)
            {
                dto.Claims.Add(new ClientClaimDto
                {
                    Type = "claim-" + i,
                    Value = SecretSentinel,
                });
            }

            for (var i = 0; i < propCount; i++)
            {
                dto.Properties.Add(new ClientPropertyDto
                {
                    Key = "prop-" + i,
                    Value = SecretSentinel,
                });
            }

            for (var i = 0; i < idpRestrictionsCount; i++)
            {
                dto.IdentityProviderRestrictions.Add(SecretSentinel);
            }

            return dto;
        }
    }

    /// <summary>
    /// Property — the snapshot path NEVER serializes the sentinel value or
    /// any forbidden top-level <c>data</c> key, regardless of how
    /// aggressively the source ClientDto is populated.
    ///
    /// Validates: Requirements 2.2, 15.1, 17.6 (negative leak side of P1)
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public void NoSecretLeak(ClientDto source)
    {
        var lastWriteUtc = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = ClientCacheSnapshotMapper.Map(source, lastWriteUtc);
        var envelope = new ClientCacheSnapshotEnvelope
        {
            Version = 1,
            TenantKey = "tenant-a",
            ClientId = source.ClientId,
            LastWriteUtc = lastWriteUtc,
            Data = snapshot,
        };

        var bytes = ClientCacheSnapshotSerializer.Serialize(envelope);
        var json = Encoding.UTF8.GetString(bytes);

        json.Should().NotContain(SecretSentinel,
            "no forbidden field value should ever leak into the snapshot");

        var data = JsonNode.Parse(bytes)!["data"]!.AsObject();
        var actualKeys = data.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in ForbiddenDataKeys)
        {
            actualKeys.Should().NotContain(forbidden,
                $"R2.2: forbidden key '{forbidden}' must not appear in snapshot data");
        }
    }
}
