// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.TestInfrastructure;
using Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Views;

// Feature: login-ui-redesign-i18n, Property 5: Resend button cooldown binds both `disabled` and `aria-disabled`
//
// For any non-negative integer cooldown value drawn from [0, 600], rendering
// ~/Views/Account/LoginWithPhone/Verify.cshtml MUST keep the resend submit button
// in a state where the `disabled` HTML attribute and the `aria-disabled` ARIA
// attribute always agree:
//   * cooldown >  0  -> button has `disabled` AND `aria-disabled="true"`.
//   * cooldown == 0  -> button has NEITHER `disabled` NOR `aria-disabled` set to "true";
//                       specifically, `disabled` is absent and `aria-disabled="false"`.
//
// The two attributes must always be coupled because keyboard / screen-reader users
// rely on `aria-disabled` while sighted users rely on the visual `disabled` state
// (Requirement 8.8). A drift between them would silently break accessibility.
//
// Validates: Requirements 3.6, 8.8
public sealed class Verify_Property5_ResendCooldown_Tests
{
    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, Property 5: Resend button cooldown binds both `disabled` and `aria-disabled`
    public Property Resend_Button_Cooldown_Binds_Disabled_And_AriaDisabled()
    {
        return Prop.ForAll(
            Gen.Choose(0, 600).ToArbitrary(),
            cooldown =>
            {
                using var harness = new RazorRenderHost();

                var model = new PhoneVerifyViewModel
                {
                    MaskedPhone = "******",
                    OtpLength = 6,
                    ReturnUrl = string.Empty,
                    ResendCooldownRemainingSeconds = cooldown
                };

                // Render the full Verify view through the harness's absolute-path fallback.
                // isMainPage is false inside RenderPartialAsync, so the project _ViewStart's
                // Layout assignment is skipped — the view renders as a self-contained fragment
                // that still exercises the resend <form>.
                var html = harness.RenderPartialAsync(
                        "~/Views/Account/LoginWithPhone/Verify.cshtml",
                        model: model)
                    .GetAwaiter().GetResult();

                var resendButton = LocateResendButton(html);
                if (resendButton is null)
                {
                    return false.Label(
                        $"resend button not found for cooldown={cooldown}; html='{Truncate(html)}'");
                }

                // GetAttribute returns the raw attribute value, or null when absent. For
                // boolean HTML attributes ("disabled"), Razor emits the bare attribute
                // which AngleSharp surfaces as an empty-string value.
                var disabledAttr = resendButton.GetAttribute("disabled");
                var ariaDisabledAttr = resendButton.GetAttribute("aria-disabled");

                bool ok;
                string label;
                if (cooldown > 0)
                {
                    var hasDisabled = disabledAttr is not null;
                    var ariaDisabledTrue = string.Equals(ariaDisabledAttr, "true", StringComparison.Ordinal);
                    ok = hasDisabled && ariaDisabledTrue;
                    label =
                        $"cooldown={cooldown} expected: disabled present + aria-disabled='true'; " +
                        $"actual: disabled={(disabledAttr is null ? "<absent>" : $"'{disabledAttr}'")}, " +
                        $"aria-disabled={(ariaDisabledAttr is null ? "<absent>" : $"'{ariaDisabledAttr}'")}";
                }
                else
                {
                    var noDisabled = disabledAttr is null;
                    var ariaDisabledFalse = string.Equals(ariaDisabledAttr, "false", StringComparison.Ordinal);
                    ok = noDisabled && ariaDisabledFalse;
                    label =
                        $"cooldown={cooldown} expected: disabled absent + aria-disabled='false'; " +
                        $"actual: disabled={(disabledAttr is null ? "<absent>" : $"'{disabledAttr}'")}, " +
                        $"aria-disabled={(ariaDisabledAttr is null ? "<absent>" : $"'{ariaDisabledAttr}'")}";
                }

                return ok.Label(label);
            });
    }

    /// <summary>
    /// Walks the rendered HTML and returns the <c>&lt;button type="submit"&gt;</c> nested
    /// inside the resend form (action <c>/Account/LoginWithPhone/Resend</c>). Locating the
    /// form first guards against accidentally matching the verify form's submit button or
    /// any future sibling form added to the view.
    /// </summary>
    private static IElement? LocateResendButton(string html)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var resendForm = document
            .QuerySelectorAll("form")
            .OfType<IHtmlFormElement>()
            .FirstOrDefault(f => string.Equals(
                f.GetAttribute("action"),
                "/Account/LoginWithPhone/Resend",
                StringComparison.Ordinal));

        return resendForm?.QuerySelector("button[type='submit']");
    }

    private static string Truncate(string value)
        => value.Length <= 400 ? value : value.Substring(0, 400) + "...";
}
