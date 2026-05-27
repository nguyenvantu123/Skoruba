// Feature: tenant-client-cache-expansion, Task 2: Snapshot DTO + envelope + serializer + Public_Safe_Fields whitelist
//
// Validates: Requirements 2.1, 2.2, 2.3, 2.4, 2.7, 2.8, 2.9, 10.4, 15.1, 15.4, 17.5, 17.6
//
// This file holds:
//   - Example-based assertions covering envelope shape, camelCase, no-whitespace,
//     unknown-property tolerance, corrupt-payload handling, and version drift.
//   - Three property-based tests (P1, P2, P3) using FsCheck.Xunit.
//
// Why one file: Task 2 in tasks.md instructs example tests + P1/P2/P3 to live
// together so the whitelist contract is reviewed in one place.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class ClientCacheSnapshotSerializerTests
{
    /// <summary>
    /// Glossary <c>Public_Safe_Fields</c> verbatim, expressed in camelCase
    /// because that is how the serializer writes them. Keep this list in
    /// sync with the DTO definition — the property tests use it as the
    /// authoritative whitelist.
    /// </summary>
    public static readonly IReadOnlyList<string> PublicSafeFieldsCamelCase = new[]
    {
        "clientId",
        "clientName",
        "clientUri",
        "logoUri",
        "description",
        "enabled",
        "protocolType",
        "redirectUris",
        "postLogoutRedirectUris",
        "allowedCorsOrigins",
        "allowedGrantTypes",
        "allowedScopes",
        "allowedIdentityTokenSigningAlgorithms",
        "requirePkce",
        "allowPlainTextPkce",
        "requireClientSecret",
        "requireConsent",
        "allowOfflineAccess",
        "allowAccessTokensViaBrowser",
        "alwaysIncludeUserClaimsInIdToken",
        "frontChannelLogoutUri",
        "frontChannelLogoutSessionRequired",
        "backChannelLogoutUri",
        "backChannelLogoutSessionRequired",
        "accessTokenLifetime",
        "identityTokenLifetime",
        "authorizationCodeLifetime",
        "absoluteRefreshTokenLifetime",
        "slidingRefreshTokenLifetime",
        "refreshTokenExpiration",
        "refreshTokenUsage",
        "updateAccessTokenClaimsOnRefresh",
        "enableLocalLogin",
        "requirePushedAuthorization",
        "requireRequestObject",
        "initiateLoginUri",
        "useTenantRedirectPairs",
        "lastWriteUtc",
    };

    /// <summary>
    /// Forbidden top-level keys inside <c>data</c>. Anything matching these
    /// (case-insensitively) is a leak and must NEVER appear in a serialized
    /// snapshot.
    /// </summary>
    private static readonly Regex ForbiddenKeyPattern = new(
        @"(?i)(.*secret.*|^claims$|^properties$|^identityProviderRestrictions$|^pairWiseSubjectSalt$|^id$|.*Items$|^accessTokenTypes$|^refreshTokenExpirations$|^refreshTokenUsages$|^protocolTypes$|^dPoPValidationModes$)",
        RegexOptions.Compiled);

    private static ClientCacheSnapshotEnvelope SampleEnvelope() => new()
    {
        Version = 1,
        TenantKey = "tenant-a",
        ClientId = "client-1",
        LastWriteUtc = new DateTime(2025, 6, 15, 10, 30, 45, DateTimeKind.Utc),
        Data = new ClientCacheSnapshotDto
        {
            ClientId = "client-1",
            ClientName = "Sample",
            Enabled = true,
            ProtocolType = "oidc",
            // Intentionally leave RedirectUris empty to exercise R2.4.
            RedirectUris = Array.Empty<string>(),
            AllowedScopes = new[] { "openid", "profile" },
            AccessTokenLifetime = 3600,
            LastWriteUtc = new DateTime(2025, 6, 15, 10, 30, 45, DateTimeKind.Utc),
        },
    };

    // ----- Example-based assertions ---------------------------------------

    [Fact]
    public void Envelope_TopLevel_Keys_Are_Exactly_VersionTenantKeyClientIdLastWriteUtcData()
    {
        var bytes = ClientCacheSnapshotSerializer.Serialize(SampleEnvelope());
        var node = JsonNode.Parse(bytes)!.AsObject();

        node.Select(p => p.Key).Should().BeEquivalentTo(
            new[] { "version", "tenantKey", "clientId", "lastWriteUtc", "data" });
    }

    [Fact]
    public void EmptyList_Serializes_AsEmptyArray_NotOmitted()
    {
        var bytes = ClientCacheSnapshotSerializer.Serialize(SampleEnvelope());
        var data = JsonNode.Parse(bytes)!["data"]!.AsObject();

        data.ContainsKey("redirectUris").Should().BeTrue("R2.4 forbids omitting empty collections");
        data["redirectUris"]!.AsArray().Count.Should().Be(0);
    }

    [Fact]
    public void Payload_Has_No_Whitespace()
    {
        var bytes = ClientCacheSnapshotSerializer.Serialize(SampleEnvelope());
        var json = Encoding.UTF8.GetString(bytes);

        // Whitespace OUTSIDE string values is what R2.7 forbids. We use a
        // simple state machine to walk the payload and ignore whitespace
        // characters that fall inside a JSON string token.
        var inString = false;
        var prev = '\0';
        foreach (var c in json)
        {
            if (c == '"' && prev != '\\')
            {
                inString = !inString;
            }
            else if (!inString && (c == ' ' || c == '\n' || c == '\r' || c == '\t'))
            {
                Assert.Fail($"R2.7: payload contains structural whitespace: {json}");
            }

            prev = c;
        }
    }

    [Fact]
    public void Camel_Case_Property_Names()
    {
        var bytes = ClientCacheSnapshotSerializer.Serialize(SampleEnvelope());
        var data = JsonNode.Parse(bytes)!["data"]!.AsObject();

        foreach (var prop in data)
        {
            char.IsLower(prop.Key[0]).Should().BeTrue(
                $"R2.4: every data field must be camelCase (saw '{prop.Key}')");
        }
    }

    [Fact]
    public void Deserialize_Unknown_Property_Ignored()
    {
        // A payload that mirrors the envelope shape plus an unknown sibling
        // key. R2.8 says unknown properties must be tolerated.
        var json = """
{
  "version": 1,
  "tenantKey": "tenant-a",
  "clientId": "client-1",
  "lastWriteUtc": "2025-06-15T10:30:45Z",
  "data": { "clientId": "client-1", "lastWriteUtc": "2025-06-15T10:30:45Z" },
  "futureField": { "anything": 42 }
}
""";

        var envelope = ClientCacheSnapshotSerializer.TryDeserialize(
            Encoding.UTF8.GetBytes(json), out var failureReason);

        failureReason.Should().BeNull();
        envelope.Should().NotBeNull();
        envelope!.Version.Should().Be(1);
        envelope.ClientId.Should().Be("client-1");
    }

    [Fact]
    public void Deserialize_Truncated_Bytes_Returns_Null_With_Corrupt_Reason()
    {
        var bytes = ClientCacheSnapshotSerializer.Serialize(SampleEnvelope());
        var truncated = bytes.Take(bytes.Length / 2).ToArray();

        var envelope = ClientCacheSnapshotSerializer.TryDeserialize(truncated, out var failureReason);

        envelope.Should().BeNull();
        failureReason.Should().Be("corrupt");
    }

    [Fact]
    public void Deserialize_Empty_Bytes_Returns_Null_With_Corrupt_Reason()
    {
        var envelope = ClientCacheSnapshotSerializer.TryDeserialize(
            Array.Empty<byte>(), out var failureReason);

        envelope.Should().BeNull();
        failureReason.Should().Be("corrupt");
    }

    [Fact]
    public void Version_Greater_Than_1_Deserializes_But_Marked_Stale_By_Caller()
    {
        // R2.8 — the serializer itself accepts forward-compat envelopes; the
        // CALLER (TenantClientCacheService.ReadSnapshotAsync, Task 5) is
        // responsible for translating Version > 1 into Cache_Outcome.Stale.
        // This test pins the contract from the serializer's side.
        var future = new ClientCacheSnapshotEnvelope
        {
            Version = 7,
            TenantKey = "tenant-a",
            ClientId = "client-1",
            LastWriteUtc = DateTime.UtcNow,
            Data = SampleEnvelope().Data,
        };

        var bytes = ClientCacheSnapshotSerializer.Serialize(future);
        var roundTripped = ClientCacheSnapshotSerializer.TryDeserialize(bytes, out var failureReason);

        failureReason.Should().BeNull();
        roundTripped.Should().NotBeNull();
        roundTripped!.Version.Should().Be(7);
    }

    // ----- Property-based tests -------------------------------------------

    /// <summary>
    /// FsCheck arbitraries for envelope/DTO. The generators stay inside
    /// realistic value domains so the suite finishes fast (≤ a few seconds
    /// at MaxTest = 200).
    /// </summary>
    public static class Arbs
    {
        private static readonly char[] AsciiPool =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_".ToCharArray();

        private static Gen<string> ShortStringGen(int min = 1, int max = 16)
            => from len in Gen.Choose(min, max)
               from chars in Gen.Elements(AsciiPool).ListOf(len)
               select new string(chars.ToArray());

        private static Gen<string?> NullableShortStringGen()
            => Gen.Frequency(
                (1, Gen.Constant<string?>(null)),
                (3, ShortStringGen().Select(s => (string?)s)));

        private static Gen<bool> BoolGen()
            => Gen.Elements(true, false);

        private static Gen<IReadOnlyList<string>> StringListGen()
            => from count in Gen.Choose(0, 5)
               from items in ShortStringGen().ListOf(count)
               select (IReadOnlyList<string>)items.ToArray();

        private static Gen<DateTime> UtcGen()
            => from offsetSeconds in Gen.Choose(0, 10 * 365 * 24 * 3600)
               select new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(offsetSeconds);

        public static Arbitrary<ClientCacheSnapshotDto> Dto()
        {
            var gen =
                from clientId in ShortStringGen()
                from clientName in NullableShortStringGen()
                from clientUri in NullableShortStringGen()
                from logoUri in NullableShortStringGen()
                from description in NullableShortStringGen()
                from enabled in BoolGen()
                from protocolType in Gen.Elements("oidc", "saml2p", "wsfed")
                from redirectUris in StringListGen()
                from postLogoutRedirectUris in StringListGen()
                from allowedCorsOrigins in StringListGen()
                from allowedGrantTypes in StringListGen()
                from allowedScopes in StringListGen()
                from allowedAlgs in StringListGen()
                from requirePkce in BoolGen()
                from allowPlainTextPkce in BoolGen()
                from requireClientSecret in BoolGen()
                from requireConsent in BoolGen()
                from allowOfflineAccess in BoolGen()
                from allowAccessTokensViaBrowser in BoolGen()
                from alwaysIncludeUserClaimsInIdToken in BoolGen()
                from frontChannelLogoutUri in NullableShortStringGen()
                from frontChannelLogoutSessionRequired in BoolGen()
                from backChannelLogoutUri in NullableShortStringGen()
                from backChannelLogoutSessionRequired in BoolGen()
                from accessTokenLifetime in Gen.Choose(60, 86_400)
                from identityTokenLifetime in Gen.Choose(60, 86_400)
                from authorizationCodeLifetime in Gen.Choose(60, 86_400)
                from absoluteRefreshTokenLifetime in Gen.Choose(60, 30 * 86_400)
                from slidingRefreshTokenLifetime in Gen.Choose(60, 30 * 86_400)
                from refreshTokenExpiration in Gen.Choose(0, 1)
                from refreshTokenUsage in Gen.Choose(0, 1)
                from updateAccessTokenClaimsOnRefresh in BoolGen()
                from enableLocalLogin in BoolGen()
                from requirePushedAuthorization in BoolGen()
                from requireRequestObject in BoolGen()
                from initiateLoginUri in NullableShortStringGen()
                from useTenantRedirectPairs in BoolGen()
                from lastWriteUtc in UtcGen()
                select new ClientCacheSnapshotDto
                {
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
                    RequirePkce = requirePkce,
                    AllowPlainTextPkce = allowPlainTextPkce,
                    RequireClientSecret = requireClientSecret,
                    RequireConsent = requireConsent,
                    AllowOfflineAccess = allowOfflineAccess,
                    AllowAccessTokensViaBrowser = allowAccessTokensViaBrowser,
                    AlwaysIncludeUserClaimsInIdToken = alwaysIncludeUserClaimsInIdToken,
                    FrontChannelLogoutUri = frontChannelLogoutUri,
                    FrontChannelLogoutSessionRequired = frontChannelLogoutSessionRequired,
                    BackChannelLogoutUri = backChannelLogoutUri,
                    BackChannelLogoutSessionRequired = backChannelLogoutSessionRequired,
                    AccessTokenLifetime = accessTokenLifetime,
                    IdentityTokenLifetime = identityTokenLifetime,
                    AuthorizationCodeLifetime = authorizationCodeLifetime,
                    AbsoluteRefreshTokenLifetime = absoluteRefreshTokenLifetime,
                    SlidingRefreshTokenLifetime = slidingRefreshTokenLifetime,
                    RefreshTokenExpiration = refreshTokenExpiration,
                    RefreshTokenUsage = refreshTokenUsage,
                    UpdateAccessTokenClaimsOnRefresh = updateAccessTokenClaimsOnRefresh,
                    EnableLocalLogin = enableLocalLogin,
                    RequirePushedAuthorization = requirePushedAuthorization,
                    RequireRequestObject = requireRequestObject,
                    InitiateLoginUri = initiateLoginUri,
                    UseTenantRedirectPairs = useTenantRedirectPairs,
                    LastWriteUtc = lastWriteUtc,
                };
            return gen.ToArbitrary();
        }

        public static Arbitrary<ClientCacheSnapshotEnvelope> Envelope()
        {
            var gen =
                from version in Gen.Choose(1, 1) // Pin to current schema version.
                from tenantKey in ShortStringGen()
                from clientId in ShortStringGen()
                from lastWriteUtc in UtcGen()
                from data in Dto().Generator
                select new ClientCacheSnapshotEnvelope
                {
                    Version = version,
                    TenantKey = tenantKey,
                    ClientId = clientId,
                    LastWriteUtc = lastWriteUtc,
                    Data = data,
                };
            return gen.ToArbitrary();
        }
    }

    /// <summary>
    /// Property 1 — Snapshot whitelist:
    /// for every randomly generated DTO, the serialized <c>data</c> object
    /// (a) has property names exactly equal to Public_Safe_Fields (camelCase),
    /// (b) does not contain any forbidden key pattern.
    ///
    /// Validates: Requirements 2.1, 2.2, 2.9, 15.1, 15.4, 17.6
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public void Property01_WhitelistFields(ClientCacheSnapshotEnvelope envelope)
    {
        var bytes = ClientCacheSnapshotSerializer.Serialize(envelope);
        var data = JsonNode.Parse(bytes)!["data"]!.AsObject();

        var actual = data.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var expected = PublicSafeFieldsCamelCase.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        actual.Should().BeEquivalentTo(expected,
            "R2.1: data object key set must equal Public_Safe_Fields");

        // Defensive tripwire: any key NOT already in the whitelist that matches
        // the forbidden pattern indicates a future refactor accidentally
        // introduced a secret-bearing field. Whitelisted keys are reviewed in
        // requirements.md and accepted (e.g. `requireClientSecret` is a boolean
        // toggle, not a secret value).
        var whitelistSet = new HashSet<string>(PublicSafeFieldsCamelCase, StringComparer.Ordinal);
        foreach (var prop in data)
        {
            if (whitelistSet.Contains(prop.Key))
            {
                continue;
            }

            ForbiddenKeyPattern.IsMatch(prop.Key).Should().BeFalse(
                $"R2.2/R15.1: forbidden key '{prop.Key}' leaked into snapshot");
        }
    }

    /// <summary>
    /// Property 2 — Envelope shape:
    /// the top-level object is exactly <c>{version, tenantKey, clientId,
    /// lastWriteUtc, data}</c>, every key is camelCase, and no whitespace
    /// appears outside string literals.
    ///
    /// Validates: Requirements 2.3, 2.4, 2.7
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public void Property02_EnvelopeShape(ClientCacheSnapshotEnvelope envelope)
    {
        var bytes = ClientCacheSnapshotSerializer.Serialize(envelope);
        var node = JsonNode.Parse(bytes)!.AsObject();

        node.Select(p => p.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Should()
            .Equal(new[] { "clientId", "data", "lastWriteUtc", "tenantKey", "version" });

        foreach (var prop in node)
        {
            char.IsLower(prop.Key[0]).Should().BeTrue(
                $"top-level key '{prop.Key}' must be camelCase");
        }

        var json = Encoding.UTF8.GetString(bytes);
        var inString = false;
        var prev = '\0';
        foreach (var c in json)
        {
            if (c == '"' && prev != '\\')
            {
                inString = !inString;
            }
            else if (!inString)
            {
                (c == ' ' || c == '\n' || c == '\r' || c == '\t').Should().BeFalse(
                    "R2.7: structural whitespace is forbidden");
            }
            prev = c;
        }
    }

    /// <summary>
    /// Property 3 — Round-trip:
    /// for any envelope, <c>TryDeserialize(Serialize(env))</c> reconstructs
    /// the same envelope (Version, TenantKey, ClientId, LastWriteUtc to ms,
    /// Data field-by-field including list ordering).
    ///
    /// Validates: Requirements 17.5
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(Arbs) })]
    public void Property03_RoundTrip(ClientCacheSnapshotEnvelope envelope)
    {
        var bytes = ClientCacheSnapshotSerializer.Serialize(envelope);
        var roundTripped = ClientCacheSnapshotSerializer.TryDeserialize(bytes, out var failureReason);

        failureReason.Should().BeNull();
        roundTripped.Should().NotBeNull();

        roundTripped!.Version.Should().Be(envelope.Version);
        roundTripped.TenantKey.Should().Be(envelope.TenantKey);
        roundTripped.ClientId.Should().Be(envelope.ClientId);

        // System.Text.Json round-trips DateTime to millisecond precision —
        // compare at that resolution to avoid sub-millisecond drift.
        roundTripped.LastWriteUtc.ToUniversalTime().ToString("O")
            .Should().Be(envelope.LastWriteUtc.ToUniversalTime().ToString("O"));

        var actual = roundTripped.Data;
        var expected = envelope.Data;

        actual.ClientId.Should().Be(expected.ClientId);
        actual.ClientName.Should().Be(expected.ClientName);
        actual.ClientUri.Should().Be(expected.ClientUri);
        actual.LogoUri.Should().Be(expected.LogoUri);
        actual.Description.Should().Be(expected.Description);
        actual.Enabled.Should().Be(expected.Enabled);
        actual.ProtocolType.Should().Be(expected.ProtocolType);

        // Lists must round-trip both content and ordering.
        actual.RedirectUris.Should().Equal(expected.RedirectUris);
        actual.PostLogoutRedirectUris.Should().Equal(expected.PostLogoutRedirectUris);
        actual.AllowedCorsOrigins.Should().Equal(expected.AllowedCorsOrigins);
        actual.AllowedGrantTypes.Should().Equal(expected.AllowedGrantTypes);
        actual.AllowedScopes.Should().Equal(expected.AllowedScopes);
        actual.AllowedIdentityTokenSigningAlgorithms.Should().Equal(expected.AllowedIdentityTokenSigningAlgorithms);

        actual.RequirePkce.Should().Be(expected.RequirePkce);
        actual.AllowPlainTextPkce.Should().Be(expected.AllowPlainTextPkce);
        actual.RequireClientSecret.Should().Be(expected.RequireClientSecret);
        actual.RequireConsent.Should().Be(expected.RequireConsent);
        actual.AllowOfflineAccess.Should().Be(expected.AllowOfflineAccess);
        actual.AllowAccessTokensViaBrowser.Should().Be(expected.AllowAccessTokensViaBrowser);
        actual.AlwaysIncludeUserClaimsInIdToken.Should().Be(expected.AlwaysIncludeUserClaimsInIdToken);

        actual.FrontChannelLogoutUri.Should().Be(expected.FrontChannelLogoutUri);
        actual.FrontChannelLogoutSessionRequired.Should().Be(expected.FrontChannelLogoutSessionRequired);
        actual.BackChannelLogoutUri.Should().Be(expected.BackChannelLogoutUri);
        actual.BackChannelLogoutSessionRequired.Should().Be(expected.BackChannelLogoutSessionRequired);

        actual.AccessTokenLifetime.Should().Be(expected.AccessTokenLifetime);
        actual.IdentityTokenLifetime.Should().Be(expected.IdentityTokenLifetime);
        actual.AuthorizationCodeLifetime.Should().Be(expected.AuthorizationCodeLifetime);
        actual.AbsoluteRefreshTokenLifetime.Should().Be(expected.AbsoluteRefreshTokenLifetime);
        actual.SlidingRefreshTokenLifetime.Should().Be(expected.SlidingRefreshTokenLifetime);
        actual.RefreshTokenExpiration.Should().Be(expected.RefreshTokenExpiration);
        actual.RefreshTokenUsage.Should().Be(expected.RefreshTokenUsage);
        actual.UpdateAccessTokenClaimsOnRefresh.Should().Be(expected.UpdateAccessTokenClaimsOnRefresh);

        actual.EnableLocalLogin.Should().Be(expected.EnableLocalLogin);
        actual.RequirePushedAuthorization.Should().Be(expected.RequirePushedAuthorization);
        actual.RequireRequestObject.Should().Be(expected.RequireRequestObject);
        actual.InitiateLoginUri.Should().Be(expected.InitiateLoginUri);
        actual.UseTenantRedirectPairs.Should().Be(expected.UseTenantRedirectPairs);

        actual.LastWriteUtc.ToUniversalTime().ToString("O")
            .Should().Be(expected.LastWriteUtc.ToUniversalTime().ToString("O"));
    }
}
