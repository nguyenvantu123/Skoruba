// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Helpers.Localization;

// Feature: login-ui-redesign-i18n, Property 2: Culture configuration resolver default culture fallback
//
// Property 2 (from design.md):
//   For any CultureConfiguration input where DefaultCulture is null, empty, or whitespace AND the
//   resolved SupportedCultures set contains the culture "vi", CultureConfigurationResolver
//   .Resolve(...).DefaultCulture.Name SHALL equal "vi".
//   AND for any non-whitespace DefaultCulture value that is contained in the resolved
//   SupportedCultures set, the resolver SHALL return that value as DefaultCulture.
//   AND the static field CultureConfiguration.DefaultRequestCulture SHALL remain unchanged at the
//   value "en".
//
// Validates: Requirements 7.3
public class CultureConfigurationResolver_Property2_Tests
{
    /// <summary>
    /// Curated pool of culture codes for the generator. Each entry is:
    ///   1) Parseable by <see cref="System.Globalization.CultureInfo.GetCultureInfo(string)"/> on every supported runtime, AND
    ///   2) A subset of <see cref="CultureConfiguration.AvailableCultures"/> ∪ {"vi"}, which is
    ///      the pool the resolver intersects against by default. This guarantees that any
    ///      culture drawn from the pool ends up in the resolved <c>SupportedCultures</c> set,
    ///      so the property's precondition (chosen default IS supported) always holds.
    /// </summary>
    private static readonly string[] CuratedCulturePool =
    {
        "vi", "en", "fr", "de", "es", "zh", "pt", "ru", "nl", "fi"
    };

    /// <summary>
    /// Sub-claim 1 + sub-claim 3:
    ///   Whitespace/null/empty <c>DefaultCulture</c> + <c>"vi"</c> in resolved supported set
    ///   => resolver picks <c>"vi"</c> as the default culture.
    ///   AND the static <c>CultureConfiguration.DefaultRequestCulture</c> field is
    ///   <c>"en"</c> both before and after the resolver runs.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Resolver_FallsBackTo_Vi_WhenDefaultIsWhitespace_AndViSupported()
    {
        // null, "", and several flavors of whitespace cover the documented input space.
        var whitespaceOrNullGen = Gen.Elements<string?>(
            null, string.Empty, " ", "  ", "\t", "\n", " \r\n ", "   \t  ");

        // Pick zero or more additional cultures from the curated pool (excluding "vi") that we
        // will combine with the mandatory "vi" entry to form the input Cultures list.
        var nonViCulturesGen = Gen.SubListOf(CuratedCulturePool.Where(c => c != "vi"))
            .Select(list => list.ToList());

        return Prop.ForAll(
            whitespaceOrNullGen.ToArbitrary(),
            nonViCulturesGen.ToArbitrary(),
            (whitespaceDefault, otherCultures) =>
            {
                // Snapshot the static field before invoking the resolver (sub-claim 3).
                var staticDefaultBefore = CultureConfiguration.DefaultRequestCulture;

                var cultures = new List<string> { "vi" };
                cultures.AddRange(otherCultures);

                var configuration = new CultureConfiguration
                {
                    // CultureConfiguration.DefaultCulture is annotated non-nullable in the
                    // production model; the resolver is documented to tolerate null/empty/whitespace.
                    DefaultCulture = whitespaceDefault!,
                    Cultures = cultures
                };

                var result = CultureConfigurationResolver.Resolve(configuration);

                // Precondition: "vi" must be in the resolved supported set. The curated pool
                // guarantees it parses; we always include it in the input list above.
                var supportedNames = result.SupportedCultures
                    .Select(ci => ci.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var viIsSupported = supportedNames.Contains("vi");

                // Sub-claim 1: when whitespace/null and "vi" supported, default is "vi".
                var defaultIsVi = string.Equals(
                    result.DefaultCulture.Name, "vi", StringComparison.OrdinalIgnoreCase);

                // Sub-claim 3: static field is "en" before and after; resolver must not mutate it.
                var staticDefaultAfter = CultureConfiguration.DefaultRequestCulture;
                var staticUnchanged = staticDefaultBefore == staticDefaultAfter
                    && staticDefaultAfter == "en";

                return (viIsSupported && defaultIsVi && staticUnchanged)
                    .Label($"viSupported={viIsSupported} defaultIsVi={defaultIsVi} " +
                           $"staticUnchanged={staticUnchanged} " +
                           $"resolvedDefault={result.DefaultCulture.Name} " +
                           $"staticBefore={staticDefaultBefore} staticAfter={staticDefaultAfter}");
            });
    }

    /// <summary>
    /// Sub-claim 2 + sub-claim 3:
    ///   For any non-whitespace <c>DefaultCulture</c> value that is contained in the resolved
    ///   supported set, the resolver returns that value verbatim as <c>DefaultCulture</c>.
    ///   AND the static <c>CultureConfiguration.DefaultRequestCulture</c> field is <c>"en"</c>
    ///   both before and after the resolver runs.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Resolver_Returns_NonWhitespace_SupportedDefault_Verbatim()
    {
        // The chosen default is drawn from the curated pool — it is always parseable and we
        // always include it in the supported input list, so the resolver MUST return it.
        var chosenDefaultGen = Gen.Elements(CuratedCulturePool);

        // Optional additional cultures that may also appear in the input list.
        var extrasGen = Gen.SubListOf(CuratedCulturePool).Select(list => list.ToList());

        return Prop.ForAll(
            chosenDefaultGen.ToArbitrary(),
            extrasGen.ToArbitrary(),
            (chosenDefault, extras) =>
            {
                var staticDefaultBefore = CultureConfiguration.DefaultRequestCulture;

                // Always include the chosen default first; de-dup the extras case-insensitively.
                var cultures = new List<string> { chosenDefault };
                foreach (var e in extras)
                {
                    if (!string.Equals(e, chosenDefault, StringComparison.OrdinalIgnoreCase))
                    {
                        cultures.Add(e);
                    }
                }

                var configuration = new CultureConfiguration
                {
                    DefaultCulture = chosenDefault,
                    Cultures = cultures
                };

                var result = CultureConfigurationResolver.Resolve(configuration);

                var supportedNames = result.SupportedCultures
                    .Select(ci => ci.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var chosenIsSupported = supportedNames.Contains(chosenDefault);

                // Sub-claim 2: resolver returns the chosen default verbatim (case-insensitive
                // for safety against runtime-canonicalised culture name differences).
                var defaultMatchesChosen = string.Equals(
                    result.DefaultCulture.Name, chosenDefault, StringComparison.OrdinalIgnoreCase);

                // Sub-claim 3 (re-asserted): static field is "en" before and after.
                var staticDefaultAfter = CultureConfiguration.DefaultRequestCulture;
                var staticUnchanged = staticDefaultBefore == staticDefaultAfter
                    && staticDefaultAfter == "en";

                return (chosenIsSupported && defaultMatchesChosen && staticUnchanged)
                    .Label($"chosen={chosenDefault} resolvedDefault={result.DefaultCulture.Name} " +
                           $"chosenSupported={chosenIsSupported} match={defaultMatchesChosen} " +
                           $"staticUnchanged={staticUnchanged} " +
                           $"staticBefore={staticDefaultBefore} staticAfter={staticDefaultAfter}");
            });
    }
}
