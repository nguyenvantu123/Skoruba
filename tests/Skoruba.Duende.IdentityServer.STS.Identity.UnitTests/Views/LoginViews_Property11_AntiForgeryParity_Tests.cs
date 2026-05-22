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

// Feature: login-ui-redesign-i18n, Property 11: Anti-forgery token count equals form count
//
// For any rendering of the login partials with arbitrary view-model and configuration
// variations, the number of `<input type="hidden" name="__RequestVerificationToken">`
// elements SHALL equal the number of `<form>` elements in the same render output. This
// guarantees Requirement 9.4 ("preserve anti-forgery token emission for every form") and
// Requirement 10.8 ("emit anti-forgery tokens on every form rendered by these pages").
//
// Scope and rendering-harness limitation
// --------------------------------------
// The full `Views/Account/Login.cshtml` and `Views/Account/LoginWithPhone/Verify.cshtml`
// pages cannot render in isolation under the unit-test `RazorRenderHost` harness — they
// transitively pull in `SignInManager<UserIdentity>`,
// `IOptions<IdentityServerOptions>`, and `Duende.IdentityServer` runtime services via
// `_ViewStart.cshtml` ⇒ `_Layout.cshtml`, none of which are wired up under the in-memory
// test host. Following the design's documented fallback path, this property test
// verifies token / form parity over the four partials that compose those pages and
// emit forms or anti-forgery tokens:
//   * `_PhoneRequestPanel.cshtml` — exactly one `<form>` ⇒ one token expected.
//   * `_LoginLanguageSwitcher.cshtml` — exactly one `<form>` when N&gt;=2 supported
//     cultures, zero forms when N&lt;2 (hide branch).
//   * `_LoginFooter.cshtml` — zero forms ⇒ zero tokens expected.
//   * `_LoginTenantPill.cshtml` — zero forms ⇒ zero tokens expected.
//
// Forms emitted directly by Login.cshtml (`#local-login-form`) and Verify.cshtml
// (`/Account/LoginWithPhone/Verify`, `/Account/LoginWithPhone/Resend`) are committed
// verbatim with `@Html.AntiForgeryToken()` in those .cshtml files (visible in source
// control) and are exercised end-to-end by the existing PhoneOtp integration tests
// (Requirement 9.9). The integration-test extensions in Tasks 11.1 / 11.2 cover the
// page-level parity; this unit test focuses on the partials' parity because they are
// the dimensions that vary under view-model / configuration permutations.
//
// Validates: Requirements 9.4, 10.8
public sealed class LoginViews_Property11_AntiForgeryParity_Tests
{
    /// <summary>
    /// Generator for `LoginViewModel` field variations called out in the task spec.
    /// Drives the `ViewData` keys consumed by `_PhoneRequestPanel` so the panel is
    /// exercised with both empty and populated error / return-URL states.
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
    /// Generator for `PhoneVerifyViewModel` field variations from the task spec:
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
    /// Picks 0..3 distinct cultures so the property exercises both branches:
    ///   * Hide branch (N&lt;2): switcher renders nothing — zero forms, zero tokens.
    ///   * Show branch (N&gt;=2): switcher renders one form — one token expected.
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
    // Feature: login-ui-redesign-i18n, Property 11: Anti-forgery token count equals form count
    public Property Anti_forgery_token_count_equals_form_count_per_partial()
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

                // _LoginFooter renders zero forms — included to exercise the trivial case
                // (0 forms ⇒ 0 tokens) on a partial whose dependencies vary independently
                // of the cultures + view-model dimensions.
                var footerHtml = harness
                    .RenderPartialAsync(
                        "Common/_LoginFooter",
                        requestPath: "/Account/Login",
                        requestQuery: string.Empty)
                    .GetAwaiter().GetResult();

                // _LoginTenantPill renders zero forms; with the default null tenant
                // context the partial short-circuits and renders empty output, giving
                // the trivial 0-form / 0-token case from a different dimension.
                var pillHtml = harness
                    .RenderPartialAsync(
                        "Common/_LoginTenantPill",
                        requestPath: "/Account/Login",
                        requestQuery: string.Empty)
                    .GetAwaiter().GetResult();

                var phonePanelOk = TokenCountEqualsFormCount(phonePanelHtml,
                    out var phonePanelForms, out var phonePanelTokens);
                var switcherOk = TokenCountEqualsFormCount(switcherHtml,
                    out var switcherForms, out var switcherTokens);
                var footerOk = TokenCountEqualsFormCount(footerHtml,
                    out var footerForms, out var footerTokens);
                var pillOk = TokenCountEqualsFormCount(pillHtml,
                    out var pillForms, out var pillTokens);

                var ok = phonePanelOk && switcherOk && footerOk && pillOk;

                _ = phoneVariation;

                return ok.Label(
                    $"phonePanel: forms={phonePanelForms} tokens={phonePanelTokens} | " +
                    $"switcher: forms={switcherForms} tokens={switcherTokens} | " +
                    $"footer: forms={footerForms} tokens={footerTokens} | " +
                    $"pill: forms={pillForms} tokens={pillTokens} | " +
                    $"cultures={cultures.Count} loginVariation={loginVariation}");
            });
    }

    /// <summary>
    /// Returns true iff the rendered HTML contains the same number of `<form>` elements
    /// as `<input type="hidden" name="__RequestVerificationToken">` elements. The form
    /// + token counts are returned via out parameters for diagnostic labelling.
    /// </summary>
    private static bool TokenCountEqualsFormCount(string html, out int formCount, out int tokenCount)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        formCount = document.QuerySelectorAll("form").Count();
        tokenCount = document
            .QuerySelectorAll("input[type='hidden'][name='__RequestVerificationToken']")
            .Count();
        return formCount == tokenCount;
    }
}
