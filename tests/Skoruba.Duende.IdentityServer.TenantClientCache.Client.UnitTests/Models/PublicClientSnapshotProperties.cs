// Feature: tenant-client-cache-public-read, Property 18: PublicClientSnapshot field set + camelCase
//
// Reflection-based whitelist guard for the SDK DTO. Mirrors the server-side
// Property 1 (Public_Safe_Fields whitelist, parent spec tenant-client-cache-expansion).
// Validates: Requirements 10.5, 12.7

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;
using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests.Models;

public class PublicClientSnapshotProperties
{
    // Camel-case JSON names for the 38 Public_Safe_Fields whitelist + the
    // LastWriteUtc timestamp the server stamps onto every snapshot envelope.
    private static readonly HashSet<string> AllowedJsonNames = new(StringComparer.Ordinal)
    {
        // 1
        "clientId",
        // 2-5
        "clientName", "clientUri", "logoUri", "description",
        // 6
        "enabled",
        // 7
        "protocolType",
        // 8-13
        "redirectUris", "postLogoutRedirectUris", "allowedCorsOrigins",
        "allowedGrantTypes", "allowedScopes", "allowedIdentityTokenSigningAlgorithms",
        // 14-20
        "requirePkce", "allowPlainTextPkce", "requireClientSecret",
        "requireConsent", "allowOfflineAccess", "allowAccessTokensViaBrowser",
        "alwaysIncludeUserClaimsInIdToken",
        // 21-24
        "frontChannelLogoutUri", "frontChannelLogoutSessionRequired",
        "backChannelLogoutUri", "backChannelLogoutSessionRequired",
        // 25-32
        "accessTokenLifetime", "identityTokenLifetime", "authorizationCodeLifetime",
        "absoluteRefreshTokenLifetime", "slidingRefreshTokenLifetime",
        "refreshTokenExpiration", "refreshTokenUsage", "updateAccessTokenClaimsOnRefresh",
        // 33-37
        "enableLocalLogin", "requirePushedAuthorization", "requireRequestObject",
        "initiateLoginUri", "useTenantRedirectPairs",
        // 38
        "lastWriteUtc"
    };

    private static readonly Regex SecretLike = new(
        // Mirror the narrow pattern parent spec tenant-client-cache-expansion
        // uses in SecurityRegressionTests: forbid the SelectList-style
        // *Items view-helper suffix and the *Secrets/*Secret/*Password
        // patterns, but DO NOT match the legitimate boolean toggle
        // `requireClientSecret` (which is a Public_Safe_Field).
        @"^.*Items$|secrets$|password|pairWise",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ForbiddenJsonNames = new(StringComparer.Ordinal)
    {
        "clientSecrets",
        "claims",
        "properties",
        "identityProviderRestrictions",
        "pairWiseSubjectSalt",
        "id"
    };

    private static readonly PropertyInfo[] AllProperties = typeof(PublicClientSnapshot)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public);

    /// <summary>
    /// Property 18: every public property on <see cref="PublicClientSnapshot"/>
    /// MUST carry a <see cref="JsonPropertyNameAttribute"/> whose value is the
    /// camelCase form of the C# property name AND lies in the Public_Safe_Fields
    /// whitelist (∪ <c>lastWriteUtc</c>) AND does NOT match the forbidden set.
    ///
    /// We use FsCheck's <c>Prop.ForAll</c> over the reflected
    /// <see cref="PropertyInfo"/> set so a regression surfaces the offending
    /// property name as the FsCheck counterexample.
    /// </summary>
    [Property(MaxTest = 25, DisplayName = "P18: PublicClientSnapshot field set + camelCase whitelist")]
    public Property Property18_FieldSet_And_CamelCase()
    {
        var generator = Gen.Elements(AllProperties.Select(p => p.Name).ToArray()).ToArbitrary();

        return Prop.ForAll(generator, propertyName =>
        {
            var prop = AllProperties.Single(p => p.Name == propertyName);
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (attr is null)
                return false.Label($"property '{propertyName}' is missing [JsonPropertyName]");

            var jsonName = attr.Name;

            // Must be the camelCase form of the C# property name.
            if (jsonName != ToCamelCase(propertyName))
                return false.Label($"property '{propertyName}' jsonName '{jsonName}' is not camelCase of C# name");

            // Must lie in the Public_Safe_Fields whitelist (∪ lastWriteUtc).
            if (!AllowedJsonNames.Contains(jsonName))
                return false.Label($"jsonName '{jsonName}' is not in the Public_Safe_Fields whitelist");

            // Must NOT match any forbidden secret-bearing field name.
            if (ForbiddenJsonNames.Contains(jsonName))
                return false.Label($"jsonName '{jsonName}' matches forbidden set");

            // Must NOT match the narrowed forbidden regex. We DO NOT broaden to
            // `(?i).*secret.*` because `requireClientSecret` is a legitimate
            // boolean toggle in Public_Safe_Fields (parent spec narrows this
            // pattern in SecurityRegressionTests for the same reason).
            if (SecretLike.IsMatch(jsonName))
                return false.Label($"jsonName '{jsonName}' matches the narrowed forbidden regex (Items/Secrets/Password/PairWise)");

            return true.ToProperty();
        });
    }

    [Fact(DisplayName = "P18 (regression): allowed JSON name set equals the type's projected names")]
    public void Property18_Regression_FullSet()
    {
        var emitted = AllProperties
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

        emitted.Should().BeEquivalentTo(AllowedJsonNames);
    }

    private static string ToCamelCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
            return pascalCase;
        return string.Concat(
            char.ToLower(pascalCase[0], CultureInfo.InvariantCulture).ToString(),
            pascalCase.AsSpan(1));
    }
}
