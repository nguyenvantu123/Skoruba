// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization;
using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Helpers.Localization;

/// <summary>
/// Property-based tests for <see cref="CultureConfigurationResolver"/> validating that the
/// resolver preserves valid input cultures from <see cref="CultureConfiguration.Cultures"/>
/// while normalizing the result to <c>(CultureConfiguration.AvailableCultures ∪ {"vi"})</c>
/// and never producing an unparseable <see cref="CultureInfo"/>.
///
/// Validates Requirements 7.2 and 7.6 of the login-ui-redesign-i18n spec:
///   * 7.2 — When <c>CultureConfiguration:Cultures</c> is empty/absent, Supported_Cultures
///           defaults to <c>AvailableCultures ∪ {"vi"}</c>.
///   * 7.6 — When the operator adds a culture code that is in the allowed pool, the page
///           SHALL render that culture without code change. Equivalently: every input code
///           that is in the pool is preserved in <c>SupportedCultures</c>.
/// </summary>
public sealed class CultureConfigurationResolver_Property1_Tests
{
    /// <summary>
    /// Custom generator: a random list of strings drawn from a curated pool that intentionally
    /// mixes parseable culture codes ("en", "vi", "fr", "zh") with the four documented
    /// "known-bad" strings the resolver must tolerate without throwing ("xx-INVALID", empty,
    /// whitespace, and a punctuation-only token). Null/empty list inputs are exercised as
    /// dedicated facts below so the documented "fall back to full pool" branch (Requirement 7.2)
    /// is asserted with deterministic fixtures.
    /// </summary>
    public static class CultureCodeArbitraries
    {
        // Pool members per the task spec: four valid codes + four known-bad strings.
        // "en", "fr", "zh" are members of CultureConfiguration.AvailableCultures; "vi" is the
        // resolver's hard-coded fallback that is unioned into the allowed pool. Together the
        // four valid codes exercise both the AvailableCultures branch and the fallback branch
        // of the resolver's pool-construction logic.
        private static readonly string[] ValidCodes = { "en", "vi", "fr", "zh" };

        // Known-bad strings: the first is parseable as a CultureInfo on no supported runtime;
        // the next two are whitespace; the fourth is non-whitespace but unparseable. All four
        // must be ignored by the resolver and reported via InvalidCultureCodes (Property 3).
        private static readonly string[] KnownBadStrings = { "xx-INVALID", "", "   ", "--" };

        public static Arbitrary<List<string>> CultureCodeList()
        {
            var validCodeGen = Gen.Elements(ValidCodes);
            var badStringGen = Gen.Elements(KnownBadStrings);
            // 50/50 mix so most lists contain at least one of each kind, exercising both
            // the "in-pool keep" and "out-of-pool drop" branches of the resolver.
            var mixedGen = Gen.OneOf(validCodeGen, badStringGen);
            return mixedGen
                .ListOf()
                .Select(items => items is null ? new List<string>() : items.ToList())
                .ToArbitrary();
        }
    }

    /// <summary>
    /// Oracle: the resolver's documented allowed pool —
    /// <c>CultureConfiguration.AvailableCultures ∪ {"vi"}</c>, compared case-insensitively.
    /// </summary>
    private static readonly HashSet<string> ExpectedPool = new(
        CultureConfiguration.AvailableCultures.Concat(new[] { CultureConfigurationResolver.StsHostFallbackCulture }),
        StringComparer.OrdinalIgnoreCase);

    // Feature: login-ui-redesign-i18n, Property 1: Culture configuration resolver preserves valid input cultures
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(CultureCodeArbitraries) })]
    public void Resolve_NonEmptyInput_PreservesPoolMembersAndDropsRest(List<string> inputCultures)
    {
        // Skip the empty case here; it is covered by the dedicated facts below so that the
        // documented null/empty branch is asserted with deterministic fixtures.
        if (inputCultures is null || inputCultures.Count == 0)
        {
            return;
        }

        var configuration = new CultureConfiguration { Cultures = inputCultures };
        var result = CultureConfigurationResolver.Resolve(configuration);
        AssertResolverInvariants(inputCultures, result);
    }

    [Fact]
    public void Resolve_NullConfiguration_ReturnsFullPool()
    {
        var result = CultureConfigurationResolver.Resolve(null);
        AssertResolverInvariants(inputCultures: null, result);
    }

    [Fact]
    public void Resolve_NullCulturesList_ReturnsFullPool()
    {
        var configuration = new CultureConfiguration { Cultures = null! };
        var result = CultureConfigurationResolver.Resolve(configuration);
        AssertResolverInvariants(inputCultures: null, result);
    }

    [Fact]
    public void Resolve_EmptyCulturesList_ReturnsFullPool()
    {
        var configuration = new CultureConfiguration { Cultures = new List<string>() };
        var result = CultureConfigurationResolver.Resolve(configuration);
        AssertResolverInvariants(new List<string>(), result);
    }

    /// <summary>
    /// Asserts the universal invariants of <see cref="CultureConfigurationResolver.Resolve"/>:
    /// every culture is parseable, every culture is in the allowed pool, the set has no
    /// duplicates, and the set matches the documented intersection (or full pool fallback).
    /// </summary>
    private static void AssertResolverInvariants(
        List<string>? inputCultures,
        CultureConfigurationResolverResult result)
    {
        Assert.NotNull(result);
        Assert.NotNull(result.SupportedCultures);

        // Invariant 1: every entry in SupportedCultures is parseable by CultureInfo.GetCultureInfo
        // (never throws). This is the safety contract for the host's RequestLocalizationOptions.
        foreach (var culture in result.SupportedCultures)
        {
            Assert.NotNull(culture);
            var parsed = CultureInfo.GetCultureInfo(culture.Name);
            Assert.Equal(culture.Name, parsed.Name);
        }

        // Invariant 2: every entry is a member of the allowed pool — AvailableCultures ∪ {"vi"}.
        foreach (var culture in result.SupportedCultures)
        {
            Assert.True(
                ExpectedPool.Contains(culture.Name),
                $"Supported culture '{culture.Name}' is not in (AvailableCultures ∪ {{\"vi\"}}).");
        }

        // Invariant 3: no duplicates (case-insensitive on culture Name).
        var supportedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in result.SupportedCultures)
        {
            Assert.True(
                supportedNames.Add(culture.Name),
                $"Duplicate culture '{culture.Name}' in SupportedCultures.");
        }

        var nonEmptyInput = inputCultures is { Count: > 0 };

        if (!nonEmptyInput)
        {
            // Null/empty input ⇒ the full pool (Requirement 7.2).
            var expectedFullPool = ExpectedPool
                .Where(IsParseable)
                .Select(SafeGetCultureName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.True(
                expectedFullPool.SetEquals(supportedNames),
                $"Expected full pool {{{string.Join(", ", expectedFullPool)}}} but got {{{string.Join(", ", supportedNames)}}}.");
            return;
        }

        // Non-empty input ⇒ the case-insensitive set of parseable input codes whose original
        // string OR resolved CultureInfo.Name is in the pool, mapped to the resolved Name
        // (Requirement 7.6). If that intersection is empty, the resolver falls back to the
        // full pool as a documented safety net (CultureConfigurationResolver.Resolve step 4).
        var expectedFromInput = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in inputCultures!)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            if (!IsParseable(raw))
            {
                continue;
            }
            var resolvedName = SafeGetCultureName(raw);
            var inPool = ExpectedPool.Contains(raw) || ExpectedPool.Contains(resolvedName);
            if (!inPool)
            {
                continue;
            }
            expectedFromInput.Add(resolvedName);
        }

        if (expectedFromInput.Count == 0)
        {
            var fallbackPool = ExpectedPool
                .Where(IsParseable)
                .Select(SafeGetCultureName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.True(
                fallbackPool.SetEquals(supportedNames),
                $"Expected fallback pool {{{string.Join(", ", fallbackPool)}}} but got {{{string.Join(", ", supportedNames)}}}.");
        }
        else
        {
            Assert.True(
                expectedFromInput.SetEquals(supportedNames),
                $"Expected {{{string.Join(", ", expectedFromInput)}}} but got {{{string.Join(", ", supportedNames)}}}.");
        }
    }

    private static bool IsParseable(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        try
        {
            _ = CultureInfo.GetCultureInfo(code);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string SafeGetCultureName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).Name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }
}
