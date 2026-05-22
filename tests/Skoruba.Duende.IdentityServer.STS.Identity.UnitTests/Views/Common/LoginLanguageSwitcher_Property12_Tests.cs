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

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Views.Common;

// Feature: login-ui-redesign-i18n, Property 12: Language switcher renders one option per
// supported culture with the current culture pre-selected.
//
// For any list of N >= 2 distinct CultureInfos drawn from a fixed pool of well-known
// cultures, any URL-safe path/query pair, and a current culture chosen from the list,
// rendering Views/Shared/Common/_LoginLanguageSwitcher.cshtml MUST:
//   * Emit exactly one <form id="selectLanguageForm" action="/Home/SetLanguage" method="post">.
//   * Emit exactly one <input type="hidden" name="__RequestVerificationToken"> inside the form.
//   * Emit exactly one <input type="hidden" name="returnUrl"> with value equal to
//     currentPath + currentQuery.
//   * Emit exactly one <select id="cultureSelect" name="culture"> inside the form.
//   * Emit exactly N <option> elements inside that <select>, one per supplied culture.
//   * Emit exactly one option with the `selected` attribute, whose `value` matches
//     the current culture's name.
//
// Validates: Requirements 6.3, 6.4
public sealed class LoginLanguageSwitcher_Property12_Tests
{
    /// <summary>
    /// Fixed pool of well-known cultures the generator draws from. Each entry is
    /// constructable on every supported runtime via <see cref="CultureInfo.GetCultureInfo"/>
    /// without falling back to an invariant placeholder, which keeps the test deterministic
    /// across CI environments. Twelve entries provide enough headroom for N up to 12.
    /// </summary>
    private static readonly string[] CulturePool =
    {
        "en", "vi", "fr", "de", "es", "ja", "ko", "zh", "pt", "ru", "it", "nl"
    };

    /// <summary>
    /// Generator for an ordered, distinct subset of <see cref="CulturePool"/> with size in
    /// [2, CulturePool.Length]. Distinct ordering matters because <c>SupportedUICultures</c>
    /// is a list — duplicate entries would render duplicate &lt;option&gt; elements which is
    /// not the contract the partial advertises (Requirement 6.3 enumerates one per culture).
    /// </summary>
    private static Gen<List<CultureInfo>> SupportedCulturesGen()
    {
        return
            from size in Gen.Choose(2, CulturePool.Length)
            // Pick 'size' distinct codes by shuffling the full pool and taking the first 'size'.
            from order in Gen.Choose(0, int.MaxValue).ListOf(CulturePool.Length)
            let pairs = CulturePool.Zip(order, (code, k) => (code, k))
            let sorted = pairs.OrderBy(p => p.k).Select(p => p.code).Take(size)
            select sorted.Select(CultureInfo.GetCultureInfo).ToList();
    }

    /// <summary>
    /// Generator for a URL-safe path/query pair. The character set matches the unreserved
    /// subset of RFC 3986 plus a leading <c>/</c>/<c>?</c> so AngleSharp's HTML parser
    /// doesn't normalise attribute values during round-trip parsing.
    /// </summary>
    private static Gen<(string Path, string Query)> PathAndQueryGen()
    {
        const string urlSafe =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var urlSafeChar = Gen.Elements(urlSafe.ToCharArray());

        var pathGen =
            from len in Gen.Choose(1, 16)
            from chars in urlSafeChar.ListOf(len)
            select "/" + new string(chars.ToArray());

        var emptyQuery = Gen.Constant(string.Empty);
        var nonEmptyQuery =
            from kLen in Gen.Choose(1, 6)
            from vLen in Gen.Choose(1, 6)
            from k in urlSafeChar.ListOf(kLen)
            from v in urlSafeChar.ListOf(vLen)
            select $"?{new string(k.ToArray())}={new string(v.ToArray())}";
        var queryGen = Gen.Frequency(
            (1, emptyQuery),
            (3, nonEmptyQuery));

        return
            from p in pathGen
            from q in queryGen
            select (p, q);
    }

    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, Property 12: Language switcher renders one option per supported culture with the current culture pre-selected
    public Property Switcher_renders_form_hidden_inputs_select_with_one_selected_option_per_culture()
    {
        return Prop.ForAll(
            SupportedCulturesGen().ToArbitrary(),
            PathAndQueryGen().ToArbitrary(),
            // Generator for an index into the supported-cultures list, so we can pick the
            // "current culture" deterministically at render time.
            Gen.Choose(0, CulturePool.Length - 1).ToArbitrary(),
            (cultures, pathAndQuery, currentIndexSeed) =>
            {
                var (path, query) = pathAndQuery;
                var currentIndex = currentIndexSeed % cultures.Count;
                var currentCulture = cultures[currentIndex];

                using var harness = new RazorRenderHost();
                harness.LocalizationOptions.SetSupportedUICultures(cultures);

                var html = harness.RenderPartialAsync(
                        "Common/_LoginLanguageSwitcher",
                        model: new LoginShellHeaderModel
                        {
                            CurrentPath = path,
                            CurrentQuery = query,
                        },
                        requestPath: path,
                        requestQuery: query,
                        requestCulture: new RequestCulture(currentCulture, currentCulture))
                    .GetAwaiter().GetResult();

                var parser = new HtmlParser();
                using var document = parser.ParseDocument(html);

                // Claim 1: exactly one <form id="selectLanguageForm"> with the documented
                // action and method. Selecting by id avoids matching any unrelated forms a
                // future shell change might introduce as siblings.
                var forms = document
                    .QuerySelectorAll("form#selectLanguageForm")
                    .OfType<IHtmlFormElement>()
                    .ToList();
                var formCountIs1 = forms.Count == 1;
                var form = forms.FirstOrDefault();
                var formActionMatches = form is not null
                    && string.Equals(form.GetAttribute("action"), "/Home/SetLanguage", StringComparison.Ordinal);
                var formMethodMatches = form is not null
                    && string.Equals(form.GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase);

                // Claim 2: exactly one anti-forgery hidden input. ASP.NET Core renders this
                // with name="__RequestVerificationToken" — Requirement 6.3 / 9.4.
                var antiForgeryInputs = form?.QuerySelectorAll(
                    "input[type='hidden'][name='__RequestVerificationToken']")
                    .OfType<IHtmlInputElement>()
                    .ToList() ?? new List<IHtmlInputElement>();
                var antiForgeryCountIs1 = antiForgeryInputs.Count == 1;

                // Claim 3: exactly one returnUrl hidden input whose value equals path+query.
                var returnUrlInputs = form?.QuerySelectorAll(
                    "input[type='hidden'][name='returnUrl']")
                    .OfType<IHtmlInputElement>()
                    .ToList() ?? new List<IHtmlInputElement>();
                var returnUrlCountIs1 = returnUrlInputs.Count == 1;
                var expectedReturnUrl = path + query;
                var returnUrlValueMatches = returnUrlInputs.Count == 1
                    && string.Equals(
                        returnUrlInputs[0].GetAttribute("value") ?? string.Empty,
                        expectedReturnUrl,
                        StringComparison.Ordinal);

                // Claim 4: exactly one <select id="cultureSelect" name="culture">.
                var selects = form?.QuerySelectorAll(
                    "select#cultureSelect[name='culture']")
                    .OfType<IHtmlSelectElement>()
                    .ToList() ?? new List<IHtmlSelectElement>();
                var selectCountIs1 = selects.Count == 1;
                var select = selects.FirstOrDefault();

                // Claim 5: exactly N <option> elements, one per supplied culture, with values
                // matching CultureInfo.Name in input order.
                var options = select?.QuerySelectorAll("option")
                    .OfType<IHtmlOptionElement>()
                    .ToList() ?? new List<IHtmlOptionElement>();
                var optionCountMatchesN = options.Count == cultures.Count;
                var optionValuesMatch = optionCountMatchesN
                    && options
                        .Select(o => o.GetAttribute("value") ?? string.Empty)
                        .SequenceEqual(cultures.Select(c => c.Name), StringComparer.Ordinal);

                // Claim 6: exactly one option carries the `selected` attribute, and its value
                // equals the current culture's name. We test for the presence of the attribute
                // (non-null) rather than IHtmlOptionElement.IsSelected because the latter
                // depends on the form's selection logic when multiple options match — the
                // requirement is about the rendered HTML, not the parsed selection state.
                var selectedOptions = options
                    .Where(o => o.GetAttribute("selected") is not null)
                    .ToList();
                var selectedCountIs1 = selectedOptions.Count == 1;
                var selectedValueMatches = selectedCountIs1
                    && string.Equals(
                        selectedOptions[0].GetAttribute("value") ?? string.Empty,
                        currentCulture.Name,
                        StringComparison.Ordinal);

                var ok =
                    formCountIs1 &&
                    formActionMatches &&
                    formMethodMatches &&
                    antiForgeryCountIs1 &&
                    returnUrlCountIs1 &&
                    returnUrlValueMatches &&
                    selectCountIs1 &&
                    optionCountMatchesN &&
                    optionValuesMatch &&
                    selectedCountIs1 &&
                    selectedValueMatches;

                return ok.Label(
                    $"forms={forms.Count} action='{form?.GetAttribute("action")}' " +
                    $"method='{form?.GetAttribute("method")}' " +
                    $"antiForgery={antiForgeryInputs.Count} " +
                    $"returnUrlInputs={returnUrlInputs.Count} " +
                    $"returnUrlValue='{(returnUrlInputs.FirstOrDefault()?.GetAttribute("value"))}' " +
                    $"expectedReturnUrl='{expectedReturnUrl}' " +
                    $"selects={selects.Count} options={options.Count} N={cultures.Count} " +
                    $"optionValues=[{string.Join(",", options.Select(o => o.GetAttribute("value")))}] " +
                    $"selected={selectedOptions.Count} " +
                    $"selectedValue='{selectedOptions.FirstOrDefault()?.GetAttribute("value")}' " +
                    $"currentCulture='{currentCulture.Name}'");
            });
    }
}
