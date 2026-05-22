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

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Views;

// Feature: login-ui-redesign-i18n, Property 10: Form `name` attributes preserved per page
//
// For any rendering of the login partials with arbitrary view-model and configuration
// variations, the set of distinct `name` attribute values across all `<input>` and
// `<button>` elements SHALL be a superset of the page-specific required set:
//   * `_PhoneRequestPanel.cshtml`: { PhoneNumber, ReturnUrl, website }
//   * `_LoginLanguageSwitcher.cshtml`: { culture, returnUrl } when rendered
//     (plus `__RequestVerificationToken` for anti-forgery, but that's covered by Property 11)
//
// Scope and rendering-harness limitation
// --------------------------------------
// The full `Views/Account/Login.cshtml` and `Views/Account/LoginWithPhone/Verify.cshtml`
// pages cannot render in isolation under the unit-test `RazorRenderHost` harness — they
// transitively pull in `SignInManager<UserIdentity>`, `IOptions<IdentityServerOptions>`,
// `Duende.IdentityServer` runtime services via `_ViewStart.cshtml` ⇒ `_Layout.cshtml`,
// none of which are wired up under the in-memory test host. Following the design's
// documented fallback path, this property test asserts the superset rule against the
// two partials that emit the input/button `name` attributes those pages compose with:
//   * `_PhoneRequestPanel.cshtml` — exercises { PhoneNumber, ReturnUrl, website }
//     directly (the panel is rendered verbatim by Login.cshtml when the phone tab is
//     enabled).
//   * `_LoginLanguageSwitcher.cshtml` — exercises { culture, returnUrl } directly (the
//     switcher is rendered verbatim by Login.cshtml and Verify.cshtml via _LoginHeader).
//
// `name` attributes owned by Login.cshtml's local-login form ({Username, Password,
// RememberLogin, ReturnUrl, button}) and Verify.cshtml's verify form ({Otp, ReturnUrl})
// are committed verbatim in those .cshtml files and verified by the existing PhoneOtp
// integration tests under
// `tests/Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests`
// (Requirement 9.9). The integration-test extensions in Tasks 11.1 / 11.2 add the
// page-level superset assertion for those names; this unit test focuses on the
// partials' superset contracts because they are the dimensions that vary under
// view-model permutations.
//
// Validates: Requirements 9.3
public sealed class LoginViews_Property10_FormNames_Tests
{
    /// <summary>
    /// Generator for the `LoginViewModel` field variations called out in the task spec.
    /// Even though `_PhoneRequestPanel` does not bind to the `LoginViewModel` directly,
    /// these variations drive the `ViewData` keys (`PhoneOtpReturnUrl`, `PhoneOtpError`)
    /// that the panel reads. The partial's superset claim must hold for every value.
    /// </summary>
    private static Gen<LoginViewModelVariation> LoginViewModelVariationGen()
    {
        var providerCountGen = Gen.Choose(0, 3);

        return
            from allowRemember in Gen.Elements(true, false)
            from enableLocal in Gen.Elements(true, false)
            from providerCount in providerCountGen
            from hasTenantContext in Gen.Elements(true, false)
            from returnUrl in Gen.Elements<string?>(
                null, string.Empty, "/", "/Connect/Authorize?client_id=foo",
                "/Account/Profile?tab=security&page=2")
            from phoneOtpError in Gen.Elements<string?>(
                null, string.Empty, "Cannot send OTP. Please try again in a few minutes.")
            select new LoginViewModelVariation(
                allowRemember,
                enableLocal,
                providerCount,
                hasTenantContext,
                returnUrl,
                phoneOtpError);
    }

    /// <summary>
    /// Generator for the `PhoneVerifyViewModel` field variations from the task spec:
    /// `MaskedPhone`, `OtpLength` in [4, 8], `ResendCooldownRemainingSeconds` in [0, 120].
    /// </summary>
    private static Gen<PhoneVerifyVariation> PhoneVerifyVariationGen()
    {
        return
            from maskedPhone in Gen.Elements(
                string.Empty, "+84*****1234", "+1**********",
                "+44*** *** ***", "+1 (***) ***-1234")
            from otpLength in Gen.Choose(4, 8)
            from cooldown in Gen.Choose(0, 120)
            select new PhoneVerifyVariation(maskedPhone, otpLength, cooldown);
    }

    /// <summary>
    /// Generator for the supported-cultures list used to drive the language switcher.
    /// Ranges 0..3 distinct cultures so the property exercises both branches:
    ///   * Hide branch (N&lt;2): the switcher renders nothing, so the partial-level
    ///     superset is the empty set (vacuously satisfied).
    ///   * Show branch (N&gt;=2): the partial must contain `name="culture"` and
    ///     `name="returnUrl"`.
    /// </summary>
    private static Gen<List<CultureInfo>> SupportedCulturesGen()
    {
        string[] pool = { "en", "vi", "fr", "de" };
        return
            from size in Gen.Choose(0, pool.Length)
            from order in Gen.Choose(0, int.MaxValue).ListOf(pool.Length)
            let pairs = pool.Zip(order, (code, k) => (code, k))
            let sorted = pairs.OrderBy(p => p.k).Select(p => p.code).Take(size)
            select sorted.Select(CultureInfo.GetCultureInfo).ToList();
    }

    public sealed record LoginViewModelVariation(
        bool AllowRememberLogin,
        bool EnableLocalLogin,
        int VisibleExternalProvidersCount,
        bool HasTenantContext,
        string? ReturnUrl,
        string? PhoneOtpError);

    public sealed record PhoneVerifyVariation(
        string MaskedPhone,
        int OtpLength,
        int ResendCooldownRemainingSeconds);

    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, Property 10: Form `name` attributes preserved per page
    public Property Form_name_attributes_form_a_superset_of_required_set_per_partial()
    {
        return Prop.ForAll(
            LoginViewModelVariationGen().ToArbitrary(),
            PhoneVerifyVariationGen().ToArbitrary(),
            SupportedCulturesGen().ToArbitrary(),
            (loginVariation, phoneVariation, cultures) =>
            {
                using var harness = new RazorRenderHost();
                if (cultures.Count > 0)
                {
                    harness.LocalizationOptions.SetSupportedUICultures(cultures);
                }

                var phonePanelViewData = new Dictionary<string, object?>
                {
                    ["PhoneOtpReturnUrl"] = loginVariation.ReturnUrl ?? string.Empty,
                    ["PhoneOtpError"] = loginVariation.PhoneOtpError,
                };

                var phonePanelHtml = harness
                    .RenderPartialAsync(
                        "_PhoneRequestPanel",
                        viewData: phonePanelViewData,
                        requestPath: "/Account/Login",
                        requestQuery: string.Empty)
                    .GetAwaiter().GetResult();

                var requestCulture = cultures.Count >= 1
                    ? new RequestCulture(cultures[0], cultures[0])
                    : new RequestCulture(CultureInfo.InvariantCulture, CultureInfo.InvariantCulture);

                var switcherHtml = harness
                    .RenderPartialAsync(
                        "Common/_LoginLanguageSwitcher",
                        model: new LoginShellHeaderModel
                        {
                            CurrentPath = "/Account/Login",
                            CurrentQuery = string.Empty,
                        },
                        requestPath: "/Account/Login",
                        requestQuery: string.Empty,
                        requestCulture: requestCulture)
                    .GetAwaiter().GetResult();

                // Phone request panel: required set is { PhoneNumber, ReturnUrl, website }
                var phoneNames = ExtractInputAndButtonNames(phonePanelHtml);
                var phoneRequired = new HashSet<string>(StringComparer.Ordinal)
                {
                    "PhoneNumber",
                    "ReturnUrl",
                    "website",
                };
                var phoneSubsetOk = phoneRequired.IsSubsetOf(phoneNames);

                // Language switcher: required set when rendered is { culture, returnUrl }
                // (plus the anti-forgery hidden, which Property 11 covers). When N < 2 the
                // partial renders nothing, so the empty required set is vacuously satisfied.
                var switcherNames = ExtractInputAndButtonNames(switcherHtml);
                var switcherRequired = cultures.Count >= 2
                    ? new HashSet<string>(StringComparer.Ordinal) { "culture", "returnUrl" }
                    : new HashSet<string>(StringComparer.Ordinal);
                var switcherSubsetOk = switcherRequired.IsSubsetOf(switcherNames);

                var ok = phoneSubsetOk && switcherSubsetOk;

                _ = phoneVariation;

                return ok.Label(
                    $"phoneNames=[{string.Join(",", phoneNames)}] " +
                    $"phoneRequired=[{string.Join(",", phoneRequired)}] " +
                    $"phoneSubsetOk={phoneSubsetOk} " +
                    $"switcherNames=[{string.Join(",", switcherNames)}] " +
                    $"switcherRequired=[{string.Join(",", switcherRequired)}] " +
                    $"switcherSubsetOk={switcherSubsetOk} " +
                    $"cultures={cultures.Count} " +
                    $"loginVariation={loginVariation}");
            });
    }

    /// <summary>
    /// Returns the set of distinct `name` attribute values present on the named form
    /// controls in the rendered HTML. The design's wording calls out "all `&lt;input&gt;`
    /// and `&lt;button&gt;` elements" but every page-specific required set in the same
    /// design section explicitly includes `culture` — which the host emits as the
    /// `name` of a `&lt;select&gt;` element on `_LoginLanguageSwitcher.cshtml`. The
    /// required set therefore could not possibly hold under a strict reading of the
    /// element-tag whitelist; the consistent reading is that the property covers every
    /// named form control that participates in the form-data contract Requirement 9.3
    /// guarantees. We extend the whitelist to `&lt;input&gt;`, `&lt;button&gt;`, and
    /// `&lt;select&gt;` accordingly. `&lt;textarea&gt;` is not included because no
    /// required-set entry references one and adding it would broaden the assertion
    /// beyond Requirement 9.3.
    /// Empty / missing names are excluded so the comparison focuses on the elements
    /// that actually contribute name-value pairs to the submitted form data.
    /// </summary>
    private static HashSet<string> ExtractInputAndButtonNames(string html)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in document.QuerySelectorAll("input, button, select").OfType<IElement>())
        {
            var name = element.GetAttribute("name");
            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name!);
            }
        }
        return names;
    }
}
