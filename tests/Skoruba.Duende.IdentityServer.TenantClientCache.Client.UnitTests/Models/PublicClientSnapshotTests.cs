// Feature: tenant-client-cache-public-read
// Example-based tests for the SDK DTO PublicClientSnapshot.
// Property-based whitelist guard lives in PublicClientSnapshotProperties (P18).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;
using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests.Models;

public class PublicClientSnapshotTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Defaults_Construct_NoNullRef()
    {
        var snapshot = new PublicClientSnapshot();

        snapshot.ClientId.Should().BeEmpty();
        snapshot.ProtocolType.Should().Be("oidc");
        snapshot.RedirectUris.Should().NotBeNull().And.BeEmpty();
        snapshot.PostLogoutRedirectUris.Should().NotBeNull().And.BeEmpty();
        snapshot.AllowedCorsOrigins.Should().NotBeNull().And.BeEmpty();
        snapshot.AllowedGrantTypes.Should().NotBeNull().And.BeEmpty();
        snapshot.AllowedScopes.Should().NotBeNull().And.BeEmpty();
        snapshot.AllowedIdentityTokenSigningAlgorithms.Should().NotBeNull().And.BeEmpty();
        snapshot.LastWriteUtc.Should().Be(default);
    }

    [Fact]
    public void Serialize_To_Json_Uses_CamelCase_Keys()
    {
        var snapshot = new PublicClientSnapshot
        {
            ClientId = "acme-spa",
            ClientName = "Acme",
            Enabled = true,
            ProtocolType = "oidc",
            RedirectUris = new[] { "https://acme.example/cb" }
        };

        var json = JsonSerializer.Serialize(snapshot, Web);
        var node = JsonNode.Parse(json)!.AsObject();

        node.ContainsKey("clientId").Should().BeTrue();
        node.ContainsKey("clientName").Should().BeTrue();
        node.ContainsKey("enabled").Should().BeTrue();
        node.ContainsKey("protocolType").Should().BeTrue();
        node.ContainsKey("redirectUris").Should().BeTrue();
        node.ContainsKey("lastWriteUtc").Should().BeTrue();

        // Pascal-case property names MUST NOT leak to the wire.
        node.ContainsKey("ClientId").Should().BeFalse();
        node.ContainsKey("LastWriteUtc").Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_System_Text_Json_Preserves_All_Fields()
    {
        var original = new PublicClientSnapshot
        {
            ClientId = "tenant-a-client",
            ClientName = "Tenant A",
            ClientUri = "https://tenant-a.example",
            LogoUri = "https://tenant-a.example/logo.png",
            Description = "Public-safe snapshot under test.",
            Enabled = true,
            ProtocolType = "oidc",
            RedirectUris = new[] { "https://tenant-a.example/cb", "https://tenant-a.example/cb2" },
            PostLogoutRedirectUris = new[] { "https://tenant-a.example/post-logout" },
            AllowedCorsOrigins = new[] { "https://tenant-a.example" },
            AllowedGrantTypes = new[] { "authorization_code", "refresh_token" },
            AllowedScopes = new[] { "openid", "profile", "email" },
            AllowedIdentityTokenSigningAlgorithms = new[] { "RS256" },
            RequirePkce = true,
            AllowPlainTextPkce = false,
            RequireClientSecret = false,
            RequireConsent = true,
            AllowOfflineAccess = true,
            AllowAccessTokensViaBrowser = false,
            AlwaysIncludeUserClaimsInIdToken = true,
            FrontChannelLogoutUri = "https://tenant-a.example/fc-logout",
            FrontChannelLogoutSessionRequired = true,
            BackChannelLogoutUri = "https://tenant-a.example/bc-logout",
            BackChannelLogoutSessionRequired = false,
            AccessTokenLifetime = 3600,
            IdentityTokenLifetime = 300,
            AuthorizationCodeLifetime = 60,
            AbsoluteRefreshTokenLifetime = 2592000,
            SlidingRefreshTokenLifetime = 1296000,
            RefreshTokenExpiration = 1,
            RefreshTokenUsage = 1,
            UpdateAccessTokenClaimsOnRefresh = true,
            EnableLocalLogin = true,
            RequirePushedAuthorization = false,
            RequireRequestObject = false,
            InitiateLoginUri = "https://tenant-a.example/login",
            UseTenantRedirectPairs = true,
            LastWriteUtc = new DateTime(2024, 5, 1, 12, 30, 45, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(original, Web);
        var roundTripped = JsonSerializer.Deserialize<PublicClientSnapshot>(json, Web);

        roundTripped.Should().NotBeNull();
        roundTripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Property_Set_Includes_All_38_Public_Safe_Fields_Plus_LastWriteUtc()
    {
        var properties = typeof(PublicClientSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .OrderBy(name => name)
            .ToArray();

        // The Public_Safe_Fields whitelist is 38 fields total. Field #38 is the
        // LastWriteUtc timestamp the server stamps onto every snapshot. The
        // remaining 37 fields are the public-safe Client surface mirrored from
        // the server-side ClientCacheSnapshotDto (parent spec).
        properties.Should().HaveCount(38);
        properties.Should().Contain("LastWriteUtc");

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            // 1
            "ClientId",
            // 2-5
            "ClientName", "ClientUri", "LogoUri", "Description",
            // 6
            "Enabled",
            // 7
            "ProtocolType",
            // 8-13
            "RedirectUris", "PostLogoutRedirectUris", "AllowedCorsOrigins",
            "AllowedGrantTypes", "AllowedScopes", "AllowedIdentityTokenSigningAlgorithms",
            // 14-20
            "RequirePkce", "AllowPlainTextPkce", "RequireClientSecret",
            "RequireConsent", "AllowOfflineAccess", "AllowAccessTokensViaBrowser",
            "AlwaysIncludeUserClaimsInIdToken",
            // 21-24
            "FrontChannelLogoutUri", "FrontChannelLogoutSessionRequired",
            "BackChannelLogoutUri", "BackChannelLogoutSessionRequired",
            // 25-32
            "AccessTokenLifetime", "IdentityTokenLifetime", "AuthorizationCodeLifetime",
            "AbsoluteRefreshTokenLifetime", "SlidingRefreshTokenLifetime",
            "RefreshTokenExpiration", "RefreshTokenUsage", "UpdateAccessTokenClaimsOnRefresh",
            // 33-37
            "EnableLocalLogin", "RequirePushedAuthorization", "RequireRequestObject",
            "InitiateLoginUri", "UseTenantRedirectPairs",
            // 38
            "LastWriteUtc"
        };

        properties.ToHashSet(StringComparer.Ordinal).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void All_Properties_Carry_JsonPropertyName_Attribute()
    {
        var properties = typeof(PublicClientSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        foreach (var p in properties)
        {
            p.GetCustomAttribute<JsonPropertyNameAttribute>()
                .Should().NotBeNull(
                    "property {0} must declare a [JsonPropertyName] attribute (camelCase contract)",
                    p.Name);
        }
    }
}
