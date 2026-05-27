// Feature: tenant-client-cache-expansion, Task 3
//
// Example-based tests for ClientCacheSnapshotMapper:
//   - verbatim Public_Safe_Fields mapping,
//   - empty / null source list normalization to Array.Empty<string>(),
//   - defensive whitelist guard via reflection.
//
// Validates: Requirements 2.2, 2.5, 14.5, 15.1
//
// Property-based leak coverage and size-guard coverage live in
// ClientCacheSnapshotNoLeakProperties.cs (separate file per Task 3 split).

using System;
using System.Collections.Generic;

using FluentAssertions;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class ClientCacheSnapshotMapperTests
{
    private static readonly DateTime SampleLastWriteUtc =
        new(2025, 6, 15, 10, 30, 45, DateTimeKind.Utc);

    /// <summary>
    /// Pin a fully-populated <see cref="ClientDto"/> against the snapshot
    /// projection. Every Public_Safe_Fields entry is asserted explicitly so
    /// future drift between the mapper and the whitelist surfaces here.
    /// </summary>
    [Fact]
    public void Maps_Public_Safe_Fields_Verbatim()
    {
        var source = new ClientDto
        {
            ClientId = "client-1",
            ClientName = "Sample Client",
            ClientUri = "https://example.com",
            LogoUri = "https://example.com/logo.png",
            Description = "desc",
            Enabled = true,
            ProtocolType = "oidc",

            RedirectUris = new List<string> { "https://app/callback" },
            PostLogoutRedirectUris = new List<string> { "https://app/postlogout" },
            AllowedCorsOrigins = new List<string> { "https://app" },
            AllowedGrantTypes = new List<string> { "authorization_code" },
            AllowedScopes = new List<string> { "openid", "profile" },
            AllowedIdentityTokenSigningAlgorithms = new List<string> { "RS256" },

            RequirePkce = true,
            AllowPlainTextPkce = false,
            RequireClientSecret = true,
            RequireConsent = false,
            AllowOfflineAccess = true,
            AllowAccessTokensViaBrowser = false,
            AlwaysIncludeUserClaimsInIdToken = true,

            FrontChannelLogoutUri = "https://app/fc-logout",
            FrontChannelLogoutSessionRequired = true,
            BackChannelLogoutUri = "https://app/bc-logout",
            BackChannelLogoutSessionRequired = true,

            AccessTokenLifetime = 3600,
            IdentityTokenLifetime = 600,
            AuthorizationCodeLifetime = 300,
            AbsoluteRefreshTokenLifetime = 2_592_000,
            SlidingRefreshTokenLifetime = 1_296_000,
            RefreshTokenExpiration = 1, // ClientDto exposes this as int
            RefreshTokenUsage = 1,
            UpdateAccessTokenClaimsOnRefresh = true,

            EnableLocalLogin = true,
            RequirePushedAuthorization = false,
            RequireRequestObject = false,
            InitiateLoginUri = "https://app/initiate-login",
            UseTenantRedirectPairs = true,
        };

        var snapshot = ClientCacheSnapshotMapper.Map(source, SampleLastWriteUtc);

        snapshot.ClientId.Should().Be("client-1");
        snapshot.ClientName.Should().Be("Sample Client");
        snapshot.ClientUri.Should().Be("https://example.com");
        snapshot.LogoUri.Should().Be("https://example.com/logo.png");
        snapshot.Description.Should().Be("desc");
        snapshot.Enabled.Should().BeTrue();
        snapshot.ProtocolType.Should().Be("oidc");

        snapshot.RedirectUris.Should().Equal("https://app/callback");
        snapshot.PostLogoutRedirectUris.Should().Equal("https://app/postlogout");
        snapshot.AllowedCorsOrigins.Should().Equal("https://app");
        snapshot.AllowedGrantTypes.Should().Equal("authorization_code");
        snapshot.AllowedScopes.Should().Equal("openid", "profile");
        snapshot.AllowedIdentityTokenSigningAlgorithms.Should().Equal("RS256");

        snapshot.RequirePkce.Should().BeTrue();
        snapshot.AllowPlainTextPkce.Should().BeFalse();
        snapshot.RequireClientSecret.Should().BeTrue();
        snapshot.RequireConsent.Should().BeFalse();
        snapshot.AllowOfflineAccess.Should().BeTrue();
        snapshot.AllowAccessTokensViaBrowser.Should().BeFalse();
        snapshot.AlwaysIncludeUserClaimsInIdToken.Should().BeTrue();

        snapshot.FrontChannelLogoutUri.Should().Be("https://app/fc-logout");
        snapshot.FrontChannelLogoutSessionRequired.Should().BeTrue();
        snapshot.BackChannelLogoutUri.Should().Be("https://app/bc-logout");
        snapshot.BackChannelLogoutSessionRequired.Should().BeTrue();

        snapshot.AccessTokenLifetime.Should().Be(3600);
        snapshot.IdentityTokenLifetime.Should().Be(600);
        snapshot.AuthorizationCodeLifetime.Should().Be(300);
        snapshot.AbsoluteRefreshTokenLifetime.Should().Be(2_592_000);
        snapshot.SlidingRefreshTokenLifetime.Should().Be(1_296_000);
        snapshot.RefreshTokenExpiration.Should().Be(1);
        snapshot.RefreshTokenUsage.Should().Be(1);
        snapshot.UpdateAccessTokenClaimsOnRefresh.Should().BeTrue();

        snapshot.EnableLocalLogin.Should().BeTrue();
        snapshot.RequirePushedAuthorization.Should().BeFalse();
        snapshot.RequireRequestObject.Should().BeFalse();
        snapshot.InitiateLoginUri.Should().Be("https://app/initiate-login");
        snapshot.UseTenantRedirectPairs.Should().BeTrue();

        snapshot.LastWriteUtc.Should().Be(SampleLastWriteUtc);
    }

    /// <summary>
    /// When the source has a null list (which shouldn't happen because the
    /// ClientDto constructor pre-allocates them, but is possible if a caller
    /// hand-crafts the DTO), the mapper falls back to <c>Array.Empty</c>
    /// rather than producing a null reference. R2.4 expects empty arrays in
    /// the serialized payload, not omitted properties or nulls.
    /// </summary>
    [Fact]
    public void Empty_Source_Lists_Become_Empty_Arrays_Not_Null()
    {
        var source = new ClientDto
        {
            ClientId = "client-1",
            ClientName = "Sample",
            // Wipe all lists to null to exercise the null-coalescing branch.
            RedirectUris = null!,
            PostLogoutRedirectUris = null!,
            AllowedCorsOrigins = null!,
            AllowedGrantTypes = null!,
            AllowedScopes = null!,
            AllowedIdentityTokenSigningAlgorithms = null!,
        };

        var snapshot = ClientCacheSnapshotMapper.Map(source, SampleLastWriteUtc);

        snapshot.RedirectUris.Should().NotBeNull().And.BeEmpty();
        snapshot.PostLogoutRedirectUris.Should().NotBeNull().And.BeEmpty();
        snapshot.AllowedCorsOrigins.Should().NotBeNull().And.BeEmpty();
        snapshot.AllowedGrantTypes.Should().NotBeNull().And.BeEmpty();
        snapshot.AllowedScopes.Should().NotBeNull().And.BeEmpty();
        snapshot.AllowedIdentityTokenSigningAlgorithms.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// The defensive whitelist guard MUST trip when reflection sees a future
    /// secret-bearing property (i.e. a property whose name matches
    /// <c>(?i).*secret.*</c> and whose value is non-null/non-default) that is
    /// NOT on the baseline whitelist. We simulate the future refactor with a
    /// test-only subclass of <see cref="ClientDto"/>.
    /// </summary>
    [Fact]
    public void EnsureNoLeakedSecretField_Throws_When_Reflection_Sees_Future_SecretBearing_Property()
    {
        var source = new FutureSecretLeakingClientDto
        {
            ClientId = "client-1",
            ClientName = "Sample",
            // The future property — not in the baseline whitelist.
            SecretValue = "TOPSECRET",
        };

        var act = () => ClientCacheSnapshotMapper.Map(source, SampleLastWriteUtc);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// The exception message must name the offending field but never echo
    /// its value (R2.5, R13).
    /// </summary>
    [Fact]
    public void EnsureNoLeakedSecretField_Throws_Message_Names_Field_Not_Value()
    {
        var source = new FutureSecretLeakingClientDto
        {
            ClientId = "client-1",
            ClientName = "Sample",
            SecretValue = "TOPSECRET",
        };

        var act = () => ClientCacheSnapshotMapper.EnsureNoLeakedSecretField(source);

        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().Contain(nameof(FutureSecretLeakingClientDto.SecretValue));
        ex.Message.Should().NotContain("TOPSECRET");
    }

    /// <summary>
    /// Sanity check: a baseline ClientDto with the existing
    /// <c>RequireClientSecret = true</c> must NOT trip the guard, because
    /// <c>RequireClientSecret</c> is a boolean toggle (not a secret value)
    /// and is on the explicit whitelist.
    /// </summary>
    [Fact]
    public void EnsureNoLeakedSecretField_Does_Not_Throw_For_RequireClientSecret_Toggle()
    {
        var source = new ClientDto
        {
            ClientId = "client-1",
            ClientName = "Sample",
            RequireClientSecret = true,
        };

        var act = () => ClientCacheSnapshotMapper.EnsureNoLeakedSecretField(source);

        act.Should().NotThrow();
    }

    /// <summary>
    /// And another sanity check: a populated <c>ClientSecrets</c> collection
    /// (the existing baseline secret-bearing property) must NOT trip the
    /// guard either — it is the canonical case the mapper is designed to
    /// silently exclude. The point of the guard is to catch *future* secret
    /// fields that haven't been reviewed.
    /// </summary>
    [Fact]
    public void EnsureNoLeakedSecretField_Does_Not_Throw_For_Existing_ClientSecrets_Field()
    {
        var source = new ClientDto
        {
            ClientId = "client-1",
            ClientName = "Sample",
            ClientSecrets = new List<ClientSecretDto>
            {
                new() { Type = "SharedSecret", Value = "S3CR3T" },
            },
        };

        var act = () => ClientCacheSnapshotMapper.EnsureNoLeakedSecretField(source);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Test-only subclass that adds a future secret-bearing property. This
    /// simulates a refactor where someone introduces a brand-new
    /// secret-named field on ClientDto without updating the snapshot
    /// whitelist.
    /// </summary>
    private sealed class FutureSecretLeakingClientDto : ClientDto
    {
        public string? SecretValue { get; set; }
    }
}
