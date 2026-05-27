// Feature: tenant-client-cache-expansion
// Public-safe DTO whose JSON projection is what gets persisted to Distributed_Cache.
//
// The field set is the verbatim Public_Safe_Fields whitelist from the spec
// Glossary (requirements.md). Any future field that could carry secret data
// MUST NOT be added here — see ClientCacheSnapshotMapper.EnsureNoLeakedSecretField
// for the defensive guard against accidental drift.
//
// Validates: Requirements 2.1, 2.2, 2.9, 15.1, 15.4
//
// Forbidden fields (must never appear here):
//   ClientSecrets, Claims, Properties, IdentityProviderRestrictions,
//   Id, PairWiseSubjectSalt, *Items view-helpers, AccessTokenTypes,
//   RefreshTokenExpirations, RefreshTokenUsages, ProtocolTypes,
//   DPoPValidationModes, raw TenantRedirectPairs payload.

#nullable enable

using System;
using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

public sealed class ClientCacheSnapshotDto
{
    // 1
    public string ClientId { get; init; } = "";

    // 2-5: descriptive metadata.
    public string? ClientName { get; init; }
    public string? ClientUri { get; init; }
    public string? LogoUri { get; init; }
    public string? Description { get; init; }

    // 6
    public bool Enabled { get; init; }

    // 7: scalar protocol type (e.g. "oidc"). Not the SelectList helper.
    public string ProtocolType { get; init; } = "oidc";

    // 8-13: canonical lists (NOT the *Items view-helpers).
    public IReadOnlyList<string> RedirectUris { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PostLogoutRedirectUris { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedCorsOrigins { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedGrantTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedScopes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedIdentityTokenSigningAlgorithms { get; init; } = Array.Empty<string>();

    // 14-20: client-protocol toggles.
    public bool RequirePkce { get; init; }
    public bool AllowPlainTextPkce { get; init; }
    public bool RequireClientSecret { get; init; }
    public bool RequireConsent { get; init; }
    public bool AllowOfflineAccess { get; init; }
    public bool AllowAccessTokensViaBrowser { get; init; }
    public bool AlwaysIncludeUserClaimsInIdToken { get; init; }

    // 21-24: front-/back-channel logout settings.
    public string? FrontChannelLogoutUri { get; init; }
    public bool FrontChannelLogoutSessionRequired { get; init; }
    public string? BackChannelLogoutUri { get; init; }
    public bool BackChannelLogoutSessionRequired { get; init; }

    // 25-32: token lifetime + refresh-token semantics. Enums are stored as
    // their integer value, NOT the SelectList helper.
    public int AccessTokenLifetime { get; init; }
    public int IdentityTokenLifetime { get; init; }
    public int AuthorizationCodeLifetime { get; init; }
    public int AbsoluteRefreshTokenLifetime { get; init; }
    public int SlidingRefreshTokenLifetime { get; init; }
    public int RefreshTokenExpiration { get; init; }
    public int RefreshTokenUsage { get; init; }
    public bool UpdateAccessTokenClaimsOnRefresh { get; init; }

    // 33-37: misc. behavior switches.
    public bool EnableLocalLogin { get; init; }
    public bool RequirePushedAuthorization { get; init; }
    public bool RequireRequestObject { get; init; }
    public string? InitiateLoginUri { get; init; }
    public bool UseTenantRedirectPairs { get; init; }

    // 38: timestamp this feature stamps onto every snapshot.
    public DateTime LastWriteUtc { get; init; }
}
