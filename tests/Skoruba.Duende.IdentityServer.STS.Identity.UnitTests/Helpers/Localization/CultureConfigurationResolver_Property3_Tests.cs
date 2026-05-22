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

// Feature: login-ui-redesign-i18n, Property 3: Culture configuration resolver isolates invalid culture codes
//
// Property 3 (from design.md / requirements.md §7.7):
//   For any list of strings `Cultures` containing arbitrary mixes of valid culture codes
//   (parseable by CultureInfo.GetCultureInfo AND in the set AvailableCultures ∪ {"vi"})
//   and invalid strings (unparseable, whitespace, or otherwise rejected),
//   CultureConfigurationResolver.Resolve(Cultures).InvalidCultureCodes SHALL contain
//   exactly the unparseable strings, AND no string from InvalidCultureCodes SHALL appear
//   in SupportedCultures (compared by CultureInfo.Name). The resolver SHALL NOT throw
//   for any input string.
//
// Implementation note: per the resolver source, "unparseable" expands to the union of
// (a) strings that are null/empty/whitespace, treated as invalid input by the resolver, and
// (b) strings that throw CultureNotFoundException / ArgumentException from
//     CultureInfo.GetCultureInfo. Out-of-pool parseable codes (e.g. "ja" when not in
//     AvailableCultures ∪ {"vi"}) are silently filtered as "not supported" — they do NOT
//     appear in InvalidCultureCodes. The property mirrors the implementation contract
//     exactly so it stays green across runtime-specific ICU differences.
//
// Validates: Requirements 7.7
public class CultureConfigurationResolver_Property3_Tests
{
    /// <summary>
    /// Mixed-culture-code generator. Produces lists drawn from a fixed alphabet that exercises
    /// every branch of the resolver: in-pool valid ("en", "vi", "fr", "de"), unparseable
    /// ("xx-INVALID", "!!", "123"), and whitespace ("", "  "). Null elements are excluded
    /// per the task spec — the resolver tolerates them but the property focuses on string
    /// mixing.
    /// </summary>
    public static class CultureCodeArbitraries
    {
        // Mix of:
        //   - Pool members that are also parseable: "en", "vi", "fr", "de"
        //   - Parseable but possibly out-of-pool depending on runtime: none here (kept simple)
        //   - Unparseable strings: "xx-INVALID", "!!", "123"
        //   - Whitespace: "", "  "
        // Null is intentionally excluded per the task brief.
        private static readonly string[] Alphabet =
        {
            "en", "vi", "fr", "de",
            "xx-INVALID", "!!", "123",
            "", "  ",
        };

        public static Arbitrary<List<string>> CultureCodeList()
        {
            return Gen.Elements(Alphabet)
                .ListOf()
                .Select(items => items?.ToList() ?? new List<string>())
                .ToArbitrary();
        }
    }

    // Feature: login-ui-redesign-i18n, Property 3: Culture configuration resolver isolates invalid culture codes
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(CultureCodeArbitraries) })]
    public void Resolve_InvalidCultureCodes_ContainsExactlyUnparseable_AndDisjointFromSupported(
        List<string> inputCultures)
    {
        // The empty-list branch is covered by the resolver's "use the pool" fallback
        // (userProvidedCultures = false) — InvalidCultureCodes is then empty by construction
        // and the property has nothing meaningful to assert on the input. Skip it here.
        if (inputCultures is null || inputCultures.Count == 0)
        {
            return;
        }

        var configuration = new CultureConfiguration { Cultures = inputCultures };

        // Sub-claim: the resolver must never throw, regardless of input.
        var result = CultureConfigurationResolver.Resolve(configuration);

        Assert.NotNull(result);
        Assert.NotNull(result.InvalidCultureCodes);
        Assert.NotNull(result.SupportedCultures);

        // Oracle: an input string is "invalid" (per resolver semantics) when it is
        // null/empty/whitespace OR fails CultureInfo.GetCultureInfo. We materialize the
        // expected list in input order, preserving duplicates — the resolver does the same.
        var expectedInvalid = new List<string>();
        foreach (var raw in inputCultures)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                // The resolver normalizes null to "" before adding; emulate that here.
                expectedInvalid.Add(raw ?? string.Empty);
                continue;
            }

            if (!IsParseable(raw))
            {
                expectedInvalid.Add(raw);
            }
        }

        // Sub-claim 1: InvalidCultureCodes contains exactly the expected invalid strings,
        // in the same order and with the same multiplicity as they appeared in the input.
        Assert.Equal(expectedInvalid, result.InvalidCultureCodes);

        // Sub-claim 2: no string in InvalidCultureCodes appears in SupportedCultures (by Name,
        // case-insensitive). The resolver only ever flags non-parseable input, so this also
        // follows from sub-claim 1; we still assert it explicitly because the requirement
        // calls it out as a distinct guarantee.
        var supportedNames = result.SupportedCultures
            .Select(ci => ci.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var invalidCode in result.InvalidCultureCodes)
        {
            Assert.False(
                supportedNames.Contains(invalidCode),
                $"Invalid code '{invalidCode}' must not appear in SupportedCultures " +
                $"(SupportedCultures = [{string.Join(", ", supportedNames)}]).");
        }
    }

    /// <summary>
    /// Mirrors <c>CultureConfigurationResolver.TryGetCultureInfo</c>: a code is "parseable"
    /// if and only if <see cref="CultureInfo.GetCultureInfo(string)"/> returns without
    /// throwing <see cref="CultureNotFoundException"/> or <see cref="ArgumentException"/>.
    /// </summary>
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
}
