// Feature: tenant-client-cache-public-read
// Public_Safe_Fields DTO surfaced over the public-read endpoint.
//
// The field set is the verbatim Public_Safe_Fields whitelist (38 fields) defined in spec
// tenant-client-cache-expansion Glossary entry "Public_Safe_Fields", PLUS one timestamp
// (`lastWriteUtc`) that is stamped onto every snapshot envelope.
//
// Validates: Requirements 10.5, 12.6, 12.7
//
// Forbidden fields (must NEVER appear here):
//   ClientSecrets, Claims, Properties, IdentityProviderRestrictions,
//   Id, PairWiseSubjectSalt, *Items view-helpers, AccessTokenTypes,
//   RefreshTokenExpirations, RefreshTokenUsages, ProtocolTypes,
//   DPoPValidationModes, raw TenantRedirectPairs payload, or any field
//   whose name matches `(?i).*secret.*`.
//
// Property 18 (P18) — reflection-based test in
// PublicClientSnapshotProperties.Property18_FieldSet_And_CamelCase guards this whitelist.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

/// <summary>
/// Public-safe snapshot of a Duende IdentityServer client, returned by
/// <c>GET /api/public/tenants/{tenantKey}/clients/{clientId}</c> and
/// surfaced to consumers via <see cref="TenantClientSnapshotResult"/>.
/// </summary>
public sealed record PublicClientSnapshot
{
    // 1
    [JsonPropertyName("clientId")]
    public string ClientId { get; init; } = string.Empty;

    // 2-5: descriptive metadata.
    [JsonPropertyName("clientName")]
    public string? ClientName { get; init; }

    [JsonPropertyName("clientUri")]
    public string? ClientUri { get; init; }

    [JsonPropertyName("logoUri")]
    public string? LogoUri { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    // 6
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    // 7: scalar protocol type (e.g. "oidc").
    [JsonPropertyName("protocolType")]
    public string ProtocolType { get; init; } = "oidc";

    // 8-13: canonical lists (NOT the *Items view-helpers).
    [JsonPropertyName("redirectUris")]
    public IReadOnlyList<string> RedirectUris { get; init; } = Array.Empty<string>();

    [JsonPropertyName("postLogoutRedirectUris")]
    public IReadOnlyList<string> PostLogoutRedirectUris { get; init; } = Array.Empty<string>();

    [JsonPropertyName("allowedCorsOrigins")]
    public IReadOnlyList<string> AllowedCorsOrigins { get; init; } = Array.Empty<string>();

    [JsonPropertyName("allowedGrantTypes")]
    public IReadOnlyList<string> AllowedGrantTypes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("allowedScopes")]
    public IReadOnlyList<string> AllowedScopes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("allowedIdentityTokenSigningAlgorithms")]
    public IReadOnlyList<string> AllowedIdentityTokenSigningAlgorithms { get; init; } = Array.Empty<string>();

    // 14-20: client-protocol toggles.
    [JsonPropertyName("requirePkce")]
    public bool RequirePkce { get; init; }

    [JsonPropertyName("allowPlainTextPkce")]
    public bool AllowPlainTextPkce { get; init; }

    [JsonPropertyName("requireClientSecret")]
    public bool RequireClientSecret { get; init; }

    [JsonPropertyName("requireConsent")]
    public bool RequireConsent { get; init; }

    [JsonPropertyName("allowOfflineAccess")]
    public bool AllowOfflineAccess { get; init; }

    [JsonPropertyName("allowAccessTokensViaBrowser")]
    public bool AllowAccessTokensViaBrowser { get; init; }

    [JsonPropertyName("alwaysIncludeUserClaimsInIdToken")]
    public bool AlwaysIncludeUserClaimsInIdToken { get; init; }

    // 21-24: front-/back-channel logout settings.
    [JsonPropertyName("frontChannelLogoutUri")]
    public string? FrontChannelLogoutUri { get; init; }

    [JsonPropertyName("frontChannelLogoutSessionRequired")]
    public bool FrontChannelLogoutSessionRequired { get; init; }

    [JsonPropertyName("backChannelLogoutUri")]
    public string? BackChannelLogoutUri { get; init; }

    [JsonPropertyName("backChannelLogoutSessionRequired")]
    public bool BackChannelLogoutSessionRequired { get; init; }

    // 25-32: token lifetime + refresh-token semantics. Enums are stored as
    // their integer value, NOT a SelectList helper.
    [JsonPropertyName("accessTokenLifetime")]
    public int AccessTokenLifetime { get; init; }

    [JsonPropertyName("identityTokenLifetime")]
    public int IdentityTokenLifetime { get; init; }

    [JsonPropertyName("authorizationCodeLifetime")]
    public int AuthorizationCodeLifetime { get; init; }

    [JsonPropertyName("absoluteRefreshTokenLifetime")]
    public int AbsoluteRefreshTokenLifetime { get; init; }

    [JsonPropertyName("slidingRefreshTokenLifetime")]
    public int SlidingRefreshTokenLifetime { get; init; }

    [JsonPropertyName("refreshTokenExpiration")]
    public int RefreshTokenExpiration { get; init; }

    [JsonPropertyName("refreshTokenUsage")]
    public int RefreshTokenUsage { get; init; }

    [JsonPropertyName("updateAccessTokenClaimsOnRefresh")]
    public bool UpdateAccessTokenClaimsOnRefresh { get; init; }

    // 33-37: misc. behavior switches.
    [JsonPropertyName("enableLocalLogin")]
    public bool EnableLocalLogin { get; init; }

    [JsonPropertyName("requirePushedAuthorization")]
    public bool RequirePushedAuthorization { get; init; }

    [JsonPropertyName("requireRequestObject")]
    public bool RequireRequestObject { get; init; }

    [JsonPropertyName("initiateLoginUri")]
    public string? InitiateLoginUri { get; init; }

    [JsonPropertyName("useTenantRedirectPairs")]
    public bool UseTenantRedirectPairs { get; init; }

    // 38: timestamp the server stamps onto every snapshot.
    [JsonPropertyName("lastWriteUtc")]
    public DateTime LastWriteUtc { get; init; }
}
