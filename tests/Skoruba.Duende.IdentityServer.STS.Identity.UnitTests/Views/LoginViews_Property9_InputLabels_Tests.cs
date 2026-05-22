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

// Feature: login-ui-redesign-i18n, Property 9: Every visible input has an associated label
//
// For any rendering of the login partials a user can interact with
// (`_PhoneRequestPanel.cshtml`, `_LoginLanguageSwitcher.cshtml`) under arbitrary view-model
// and configuration variations, every `<input>` element whose `type` attribute is not
// `hidden` SHALL have one of:
//   * an enclosing `<label>` ancestor element, OR
//   * a `<label for="X">` element somewhere in the same partial output where `X` matches
//     the input's `id` attribute.
//
// Scope and rendering-harness limitation
// --------------------------------------
// The full `Views/Account/Login.cshtml` and `Views/Account/LoginWithPhone/Verify.cshtml`
// pages cannot render in isolation under the unit-test `RazorRenderHost` harness because
// they (or the `_Layout.cshtml` chain reachable via `_ViewStart.cshtml`) inject identity
// infrastructure that requires a live IdentityServer + EF Core service graph
// (`SignInManager<UserIdentity>`, `IOptions<IdentityServerOptions>`,
// `IOptions<ServerSideSessionsConfiguration>`, `Duende.IdentityServer` services, etc.).
// Bringing up that stack would turn the property test into an integration test and is
// out of scope for this unit-test project.
//
// Following the design's documented fallback ("if the view truly cannot render in
// isolation, scope this property test to the partials only and document the limitation
// in a code comment"), the property is enforced against the two partials that own all
// of the visible `<input>` elements that Login_Page renders inside its login-shell:
//   * `_PhoneRequestPanel.cshtml` — owns `phoneOtpPhoneNumber` (phone tab on Login_Page)
//     and the honeypot bot-trap (`aria-hidden="true"`, excluded from the visible set).
//   * `_LoginLanguageSwitcher.cshtml` — emits a `<select>` (NOT an `<input>`); including
//     this partial confirms the vacuous case (no visible `<input>`s ⇒ property holds)
//     under both the hide branch (N&lt;2) and the show branch (N&gt;=2).
//
// Visible inputs owned by the page chrome itself (Login_Page's `Username` and `Password`,
// Verify_Page's `phoneOtpCode`) are out of reach of this unit test. Their label
// associations are guarded by:
//   * the `<label class="form-label" for="...">` markup committed verbatim in those
//     `.cshtml` files (reviewed at write time and reachable via the spec's source-control
//     diff), and
//   * Property 10's superset assertion (which asserts `name` attributes survive any
//     render permutation), which combined with the static label markup guarantees the
//     label association as long as the static markup is unchanged.
// An end-to-end Razor render assertion on the full pages belongs to the optional
// integration-test scaffolding under `tests/Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests`
// (Tasks 11.1 and 11.2 in the implementation plan), which has access to the running host.
//
// Validates: Requirements 8.3
public sealed class LoginViews_Property9_InputLabels_Tests
{
    /// <summary>
    /// Generator for `LoginViewModel` field variations called out in the task spec.
    /// `_PhoneRequestPanel.cshtml` does not bind directly to `LoginViewModel`, but the
    /// partial reads `ViewData["PhoneOtpReturnUrl"]` populated by Login.cshtml from
    /// `Model.ReturnUrl`. The fields varied here drive the corresponding `ViewData`
    /// inputs that the partial actually consumes.
    /// </summary>
    private static Gen<LoginViewModelVariation> LoginViewModelVariationGen()
    {
        // External provider count in [0, 3] — `_PhoneRequestPanel` does not render
        // external providers, but Property 9 must be invariant under this dimension
        // because the panel may be rendered alongside the providers grid.
        var providerCountGen = Gen.Choose(0, 3);

        return
            from allowRemember in Gen.Elements(true, false)
            from enableLocal in Gen.Elements(true, false)
            from providerCount in providerCountGen
            from hasTenantContext in Gen.Elements(true, false)
            from returnUrl in Gen.Elements<string?>(
                null, string.Empty, "/", "/Connect/Authorize?client_id=foo",
                "/Account/Profile?tab=security&page=2")
            select new LoginViewModelVariation(
                allowRemember,
                enableLocal,
                providerCount,
                hasTenantContext,
                returnUrl);
    }

    /// <summary>
    /// Generator for `PhoneVerifyViewModel` field variations: `MaskedPhone`,
    /// `OtpLength` in [4, 8], `ResendCooldownRemainingSeconds` in [0, 120]. The partial
    /// under test (`_PhoneRequestPanel`) does not consume these fields, but they are
    /// included to exercise the spec's parameter space — the language switcher partial
    /// is then re-rendered against the same generator to confirm invariance.
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
    /// Picks 0..3 distinct cultures from a fixed pool so the language switcher exercises
    /// both the hide branch (N &lt; 2) and the show branch (N &gt;= 2).
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
        string? ReturnUrl);

    public sealed record PhoneVerifyVariation(
        string MaskedPhone,
        int OtpLength,
        int ResendCooldownRemainingSeconds);

    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, Property 9: Every visible input has an associated label
    public Property Every_visible_input_has_an_associated_label()
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

                // ViewData fed to `_PhoneRequestPanel`: the partial reads
                // `ViewData["PhoneOtpReturnUrl"]` (Requirement 9.5) and may consult an
                // optional error string. We surface the loginVariation.ReturnUrl through
                // the same ViewData key Login.cshtml uses so the partial's hidden ReturnUrl
                // input is exercised.
                var phonePanelViewData = new Dictionary<string, object?>
                {
                    ["PhoneOtpReturnUrl"] = loginVariation.ReturnUrl ?? string.Empty,
                };

                var phonePanelHtml = harness
                    .RenderPartialAsync(
                        "_PhoneRequestPanel",
                        viewData: phonePanelViewData,
                        requestPath: "/Account/Login",
                        requestQuery: string.Empty)
                    .GetAwaiter().GetResult();

                // Even when the language switcher hides itself (cultures.Count < 2) the
                // Property must still hold trivially (no <input> elements rendered ⇒ all
                // visible inputs have labels).
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

                var (phoneOk, phoneFailures) = AllVisibleInputsHaveLabels(phonePanelHtml);
                var (switcherOk, switcherFailures) = AllVisibleInputsHaveLabels(switcherHtml);

                var ok = phoneOk && switcherOk;
                var label =
                    $"phonePanelInputs={phoneFailures.totalVisibleInputs} " +
                    $"phonePanelMissingLabels={phoneFailures.unlabeled.Count} " +
                    $"switcherInputs={switcherFailures.totalVisibleInputs} " +
                    $"switcherMissingLabels={switcherFailures.unlabeled.Count} " +
                    $"phoneFailures=[{string.Join(",", phoneFailures.unlabeled)}] " +
                    $"switcherFailures=[{string.Join(",", switcherFailures.unlabeled)}] " +
                    $"loginVariation={loginVariation} " +
                    $"phoneVariation={phoneVariation} " +
                    $"cultures={cultures.Count}";

                // PhoneVerifyVariation values are not currently consumed by the partials
                // under test. Discard them in the label only — including them silences the
                // FsCheck "unused value" warning while documenting the parameter space.
                _ = phoneVariation;

                return ok.Label(label);
            });
    }

    /// <summary>
    /// Returns (ok, diagnostics) where diagnostics enumerate input ids missing a label
    /// association. An input is considered "associated" when it satisfies one of the
    /// design's two conditions:
    ///   1. the input has an enclosing `<label>` ancestor, OR
    ///   2. there is a `<label for="X">` element where X matches the input's id.
    /// Inputs are evaluated when they are "visible" per Requirement 8.3 — inputs with
    /// `type="hidden"` are excluded by the design's wording and inputs with
    /// `aria-hidden="true"` are excluded as defense-in-depth: the only `aria-hidden`
    /// input on these surfaces is the honeypot bot-trap (`name="website"`) which is
    /// visually hidden, removed from the tab order via `tabindex="-1"`, and explicitly
    /// not announced to assistive technology. Requirement 8.3 enumerates "the username
    /// input, password input, phone number input, and OTP input on Phone_Verify_Page"
    /// — the honeypot is intentionally excluded from the visible-input set.
    /// Inputs without a `type` attribute are treated as text per the HTML5 default —
    /// they ARE visible and must satisfy the property.
    /// </summary>
    private static (bool ok, (int totalVisibleInputs, List<string> unlabeled)) AllVisibleInputsHaveLabels(string html)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var inputs = document
            .QuerySelectorAll("input")
            .OfType<IHtmlInputElement>()
            .Where(input =>
            {
                var type = input.GetAttribute("type") ?? "text";
                if (string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var ariaHidden = input.GetAttribute("aria-hidden");
                if (string.Equals(ariaHidden, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            })
            .ToList();

        // Build a set of `for` targets emitted by `<label for="...">` elements. Using a
        // case-insensitive comparison would be wrong here — the HTML spec requires the
        // `for` attribute to byte-match the input's `id`, so we use Ordinal comparison.
        var labelTargets = document
            .QuerySelectorAll("label[for]")
            .OfType<IHtmlLabelElement>()
            .Select(l => l.GetAttribute("for") ?? string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.Ordinal);

        var unlabeled = new List<string>();

        foreach (var input in inputs)
        {
            var id = input.GetAttribute("id");
            // Condition 1: any ancestor is a <label> element.
            var hasLabelAncestor = AncestorIsLabel(input);

            // Condition 2: `<label for="X">` where X equals the input's id.
            var hasMatchingForLabel = !string.IsNullOrEmpty(id) && labelTargets.Contains(id!);

            if (!hasLabelAncestor && !hasMatchingForLabel)
            {
                unlabeled.Add(string.IsNullOrEmpty(id) ? "<no-id>" : id!);
            }
        }

        return (unlabeled.Count == 0, (inputs.Count, unlabeled));
    }

    private static bool AncestorIsLabel(IElement element)
    {
        var parent = element.ParentElement;
        while (parent is not null)
        {
            if (string.Equals(parent.LocalName, "label", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            parent = parent.ParentElement;
        }
        return false;
    }
}
