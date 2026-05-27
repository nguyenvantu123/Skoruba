// Feature: tenant-client-cache-expansion, Task 3
//
// Maps a fully-loaded ClientDto into the public-safe ClientCacheSnapshotDto
// using explicit per-field assignment for every Public_Safe_Fields entry.
// This is the ONLY tier that touches ClientDto on the cache-write path —
// keep the mapping verbatim and never reflect over the source.
//
// Forbidden inputs (must never be read):
//   ClientSecrets, Claims, Properties, IdentityProviderRestrictions,
//   Id, PairWiseSubjectSalt, *Items view-helpers, AccessTokenTypes,
//   RefreshTokenExpirations, RefreshTokenUsages, ProtocolTypes,
//   DPoPValidationModes, raw TenantRedirectPairs.
//
// Validates: Requirements 2.2, 2.5, 15.1

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

internal static class ClientCacheSnapshotMapper
{
    /// <summary>
    /// Regex matching any property name that LOOKS LIKE it could carry a
    /// secret. Used by <see cref="EnsureNoLeakedSecretField"/> as a
    /// defensive tripwire against future ClientDto refactors.
    /// </summary>
    private static readonly Regex SecretLikePattern = new(
        @"(?i).*secret.*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Baseline set of ClientDto properties that match
    /// <see cref="SecretLikePattern"/> and are intentionally NOT mapped:
    /// <list type="bullet">
    /// <item><c>ClientSecrets</c> — collection of secret entries; explicitly
    /// excluded from snapshot per R2.2.</item>
    /// <item><c>RequireClientSecret</c> — boolean toggle, not a secret value;
    /// IS mapped into the snapshot per Public_Safe_Fields.</item>
    /// </list>
    /// Anything ELSE matching the regex is treated as a future leak.
    /// </summary>
    private static readonly HashSet<string> BaselineSecretLikeWhitelist =
        new(StringComparer.Ordinal)
        {
            nameof(ClientDto.ClientSecrets),
            nameof(ClientDto.RequireClientSecret),
        };

    /// <summary>
    /// Project a <see cref="ClientDto"/> into the public-safe snapshot.
    /// Lists fall back to <see cref="Array.Empty{T}"/> when the source is
    /// null (R2.4 expects empty arrays, not omitted fields).
    /// </summary>
    public static ClientCacheSnapshotDto Map(ClientDto source, DateTime lastWriteUtc)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        // Defensive whitelist guard — runs BEFORE we materialize the DTO so
        // a future regression fails loudly at write time (R2.5).
        EnsureNoLeakedSecretField(source);

        return new ClientCacheSnapshotDto
        {
            // ----- 1: identity ------------------------------------------------
            ClientId = source.ClientId ?? string.Empty,

            // ----- 2-5: descriptive metadata ---------------------------------
            ClientName = source.ClientName,
            ClientUri = source.ClientUri,
            LogoUri = source.LogoUri,
            Description = source.Description,

            // ----- 6 ----------------------------------------------------------
            Enabled = source.Enabled,

            // ----- 7: scalar protocol ----------------------------------------
            // ProtocolType is the scalar string ("oidc" / "saml2p"); never the
            // SelectList helper `ProtocolTypes`.
            ProtocolType = source.ProtocolType ?? "oidc",

            // ----- 8-13: canonical lists -------------------------------------
            // We deliberately read the canonical IList<string> properties
            // (NOT the *Items view-helpers, which are flattened strings used
            // only by the Razor admin views).
            RedirectUris = ToReadOnly(source.RedirectUris),
            PostLogoutRedirectUris = ToReadOnly(source.PostLogoutRedirectUris),
            AllowedCorsOrigins = ToReadOnly(source.AllowedCorsOrigins),
            AllowedGrantTypes = ToReadOnly(source.AllowedGrantTypes),
            AllowedScopes = ToReadOnly(source.AllowedScopes),
            AllowedIdentityTokenSigningAlgorithms =
                ToReadOnly(source.AllowedIdentityTokenSigningAlgorithms),

            // ----- 14-20: protocol toggles -----------------------------------
            RequirePkce = source.RequirePkce,
            AllowPlainTextPkce = source.AllowPlainTextPkce,
            RequireClientSecret = source.RequireClientSecret,
            RequireConsent = source.RequireConsent,
            AllowOfflineAccess = source.AllowOfflineAccess,
            AllowAccessTokensViaBrowser = source.AllowAccessTokensViaBrowser,
            AlwaysIncludeUserClaimsInIdToken = source.AlwaysIncludeUserClaimsInIdToken,

            // ----- 21-24: front-/back-channel logout -------------------------
            FrontChannelLogoutUri = source.FrontChannelLogoutUri,
            FrontChannelLogoutSessionRequired = source.FrontChannelLogoutSessionRequired,
            BackChannelLogoutUri = source.BackChannelLogoutUri,
            BackChannelLogoutSessionRequired = source.BackChannelLogoutSessionRequired,

            // ----- 25-32: lifetimes + refresh-token semantics ----------------
            // RefreshTokenExpiration / RefreshTokenUsage are int-backed enum
            // values on ClientDto; cast preserved here per Task 3 ("map enums
            // to int by casting").
            AccessTokenLifetime = source.AccessTokenLifetime,
            IdentityTokenLifetime = source.IdentityTokenLifetime,
            AuthorizationCodeLifetime = source.AuthorizationCodeLifetime,
            AbsoluteRefreshTokenLifetime = source.AbsoluteRefreshTokenLifetime,
            SlidingRefreshTokenLifetime = source.SlidingRefreshTokenLifetime,
            RefreshTokenExpiration = (int)source.RefreshTokenExpiration,
            RefreshTokenUsage = (int)source.RefreshTokenUsage,
            UpdateAccessTokenClaimsOnRefresh = source.UpdateAccessTokenClaimsOnRefresh,

            // ----- 33-37: misc switches --------------------------------------
            EnableLocalLogin = source.EnableLocalLogin,
            RequirePushedAuthorization = source.RequirePushedAuthorization,
            RequireRequestObject = source.RequireRequestObject,
            InitiateLoginUri = source.InitiateLoginUri,
            UseTenantRedirectPairs = source.UseTenantRedirectPairs,

            // ----- 38: write timestamp ---------------------------------------
            LastWriteUtc = lastWriteUtc,
        };
    }

    /// <summary>
    /// Walk the runtime type of <paramref name="source"/> via reflection and
    /// throw if any property whose name matches <see cref="SecretLikePattern"/>
    /// is observed with a non-null / non-default value AND is not on the
    /// known baseline whitelist.
    ///
    /// The thrown exception message names the offending FIELD only — never
    /// its value — per R2.5.
    /// </summary>
    public static void EnsureNoLeakedSecretField(ClientDto source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var properties = source
            .GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        foreach (var prop in properties)
        {
            if (BaselineSecretLikeWhitelist.Contains(prop.Name))
            {
                continue;
            }

            if (!SecretLikePattern.IsMatch(prop.Name))
            {
                continue;
            }

            // Skip indexers and write-only properties — those cannot leak
            // through reflection.
            if (prop.GetIndexParameters().Length > 0 || !prop.CanRead)
            {
                continue;
            }

            object? value;
            try
            {
                value = prop.GetValue(source);
            }
            catch
            {
                // A throwing getter is suspicious but not necessarily a leak;
                // skip it so the guard cannot itself break the write path.
                continue;
            }

            if (IsNullOrDefault(value, prop.PropertyType))
            {
                continue;
            }

            // Message names the field; the actual value is intentionally
            // omitted to avoid logging the leaked secret (R2.5, R13).
            throw new InvalidOperationException(
                $"ClientCacheSnapshotMapper detected a non-whitelisted secret-bearing field on ClientDto: '{prop.Name}'. Refusing to map.");
        }
    }

    private static IReadOnlyList<string> ToReadOnly(List<string>? source)
    {
        return source is null
            ? Array.Empty<string>()
            : source;
    }

    private static bool IsNullOrDefault(object? value, Type type)
    {
        if (value is null)
        {
            return true;
        }

        if (type.IsValueType)
        {
            var defaultValue = Activator.CreateInstance(type);
            return Equals(value, defaultValue);
        }

        if (value is string s)
        {
            return s.Length == 0;
        }

        // Treat empty collections as the conventional "unpopulated" state
        // (the ClientDto constructor pre-allocates empty Lists).
        if (value is IEnumerable enumerable)
        {
            foreach (var _ in enumerable)
            {
                return false;
            }
            return true;
        }

        return false;
    }
}
