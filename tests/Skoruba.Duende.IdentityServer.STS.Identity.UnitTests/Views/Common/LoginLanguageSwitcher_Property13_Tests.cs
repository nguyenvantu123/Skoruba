// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.AspNetCore.Localization;
using Skoruba.Duende.IdentityServer.STS.Identity.Models.Login;
using Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.TestInfrastructure;
using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Views.Common;

// Feature: login-ui-redesign-i18n, Property 13: Language switcher option text falls back
// from NativeName to DisplayName.
//
// The partial computes each option's display text as:
//     string.IsNullOrWhiteSpace(c.NativeName) ? c.DisplayName : c.NativeName
//
// For any list of N >= 2 distinct CultureInfos drawn from a fixed pool of well-known
// cultures, every culture in the pool has a non-empty NativeName on every supported
// runtime (verified by a guard fact below). Consequently the property test exercises the
// NativeName branch over many (culture, list, current-culture) combinations and asserts
// option text equality against c.NativeName. The DisplayName fallback is not exercised
// by the property because constructing a CultureInfo with whitespace NativeName is not
// possible on the runtime (NativeName is a culture-data-driven property the runtime
// supplies non-empty values for); the guard fact documents this invariant so any future
// runtime change that surfaces a whitespace NativeName would surface the test failure
// here rather than silently passing.
//
// Validates: Requirements 6.7
public sealed class LoginLanguageSwitcher_Property13_Tests
{
    /// <summary>
    /// Same well-known pool as Property 12, plus a few extras with non-empty NativeName
    /// representations to broaden coverage of the NativeName branch (e.g. "vi" → "Tiếng Việt",
    /// "ja" → "日本語", "ko" → "한국어"). Each entry must be parseable by
    /// <see cref="CultureInfo.GetCultureInfo"/> and must have a non-empty
    /// <see cref="CultureInfo.NativeName"/>; the guard fact below enforces this.
    /// </summary>
    private static readonly string[] CulturePool =
    {
        "en", "vi", "fr", "de", "es", "ja", "ko", "zh", "pt", "ru", "it", "nl"
    };

    [Fact]
    public void Pool_cultures_all_have_non_whitespace_native_names()
    {
        // Guard for the property below: if a future runtime returns a whitespace NativeName
        // for any pool member, the property's positive branch would no longer apply and
        // would silently pass without exercising the requirement. This fact catches that.
        foreach (var code in CulturePool)
        {
            var culture = CultureInfo.GetCultureInfo(code);
            Assert.False(
                string.IsNullOrWhiteSpace(culture.NativeName),
                $"Expected non-whitespace NativeName for '{code}', got '{culture.NativeName}'.");
        }
    }

    private static Gen<List<CultureInfo>> SupportedCulturesGen()
    {
        return
            from size in Gen.Choose(2, CulturePool.Length)
            from order in Gen.Choose(0, int.MaxValue).ListOf(CulturePool.Length)
            let pairs = CulturePool.Zip(order, (code, k) => (code, k))
            let sorted = pairs.OrderBy(p => p.k).Select(p => p.code).Take(size)
            select sorted.Select(CultureInfo.GetCultureInfo).ToList();
    }

    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, Property 13: Language switcher option text falls back from NativeName to DisplayName
    public Property Option_text_equals_NativeName_when_NativeName_is_non_whitespace()
    {
        return Prop.ForAll(
            SupportedCulturesGen().ToArbitrary(),
            Gen.Choose(0, CulturePool.Length - 1).ToArbitrary(),
            (cultures, currentIndexSeed) =>
            {
                var currentIndex = currentIndexSeed % cultures.Count;
                var currentCulture = cultures[currentIndex];

                using var harness = new RazorRenderHost();
                harness.LocalizationOptions.SetSupportedUICultures(cultures);

                // The partial's returnUrl hidden input is irrelevant to the option-text
                // assertion, so a fixed path/query keeps the test focused on the property
                // under inspection (Requirement 6.7).
                var html = harness.RenderPartialAsync(
                        "Common/_LoginLanguageSwitcher",
                        model: new LoginShellHeaderModel
                        {
                            CurrentPath = "/Account/Login",
                            CurrentQuery = string.Empty,
                        },
                        requestPath: "/Account/Login",
                        requestQuery: string.Empty,
                        requestCulture: new RequestCulture(currentCulture, currentCulture))
                    .GetAwaiter().GetResult();

                var parser = new HtmlParser();
                using var document = parser.ParseDocument(html);

                var options = document
                    .QuerySelectorAll("form#selectLanguageForm select#cultureSelect option")
                    .OfType<IHtmlOptionElement>()
                    .ToList();

                // Claim 1: option count equals the supplied culture count. Without this
                // sanity check the per-option text comparison below would silently succeed
                // on a partial that drops options.
                var optionCountMatches = options.Count == cultures.Count;

                // Claim 2: for each option, the rendered text matches the partial's resolution
                // rule applied to the corresponding input culture. Because every pool member
                // has a non-empty NativeName (guarded by the fact above), the expected value
                // is always c.NativeName.
                bool eachOptionTextMatchesNativeName = optionCountMatches;
                var mismatches = new List<string>();
                if (optionCountMatches)
                {
                    for (var i = 0; i < options.Count; i++)
                    {
                        var expected = ExpectedOptionText(cultures[i]);
                        var actual = options[i].TextContent?.Trim() ?? string.Empty;
                        if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        {
                            eachOptionTextMatchesNativeName = false;
                            mismatches.Add($"[{i}] code='{cultures[i].Name}' " +
                                           $"expected='{expected}' actual='{actual}'");
                        }
                    }
                }

                var ok = optionCountMatches && eachOptionTextMatchesNativeName;
                return ok.Label(
                    $"options={options.Count} N={cultures.Count} " +
                    $"mismatches=[{string.Join(" | ", mismatches)}]");
            });
    }

    /// <summary>
    /// Mirror of the partial's resolution rule. Centralised here so any future change to the
    /// partial's algorithm surfaces a build error in one place rather than diverging silently.
    /// </summary>
    private static string ExpectedOptionText(CultureInfo culture)
    {
        var native = culture.NativeName;
        return string.IsNullOrWhiteSpace(native) ? culture.DisplayName : native;
    }
}
