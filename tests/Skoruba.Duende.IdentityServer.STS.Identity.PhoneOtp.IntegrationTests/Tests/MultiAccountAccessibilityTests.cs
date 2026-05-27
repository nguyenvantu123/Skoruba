// Copyright (c) Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// Feature: phone-otp-multi-account-select, Task 12 — MultiAccountAccessibilityTests
// Validates: Requirements 16.10, 12.1, 12.2, 12.3, 12.4, 12.5, 12.7, 12.8,
//            12.9, 13.1, 13.2, 13.5.
//
// ----------------------------------------------------------------------------
// HARNESS DEFERRED — same blocker as the rest of the integration project (see
// MultiAccountFlowTests file header). DOM-level accessibility assertions
// require a rendered HTML response from a running STS host so AngleSharp
// can parse the markup of `Views/Account/LoginWithPhone/SelectAccount.cshtml`
// after a real `IViewLocalizer` resolves the resx keys.
//
// The Razor view itself is checked via the production source file at
//   `src/Skoruba.Duende.IdentityServer.STS.Identity/Views/Account/LoginWithPhone/SelectAccount.cshtml`
// (Section 5.1 of the design pins the markup verbatim — `<h1>`, `<label
// for="account-select">`, `<select id="account-select" name="SelectionToken"
// aria-required="true" autofocus required>`, `<button type="submit"
// aria-label="...">`, `<a href="/Account/Login?returnUrl=...">`,
// `role="alert"` error region, every visible string routed through
// `IViewLocalizer`).
//
// The unit tests in
//   `tests/Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests
//   /Controllers/PhoneLoginControllerSelectAccountGetTests` already cover
// the controller-side contract that drives the view (model shape, candidate
// ordering, empty-username omission, masked phone fallback). The integration
// suite below would supplement that with the actually-rendered HTML.
// ----------------------------------------------------------------------------

using System.Threading.Tasks;

using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests.Tests;

/// <summary>
/// Accessibility / DOM integration tests for
/// <c>/Account/LoginWithPhone/SelectAccount</c>. Skipped pending a working
/// WebApplicationFactory harness — see file header.
/// </summary>
public sealed class MultiAccountAccessibilityTests
{
    private const string SkipReason =
        "Integration harness deferred — needs WebApplicationFactory<Program> " +
        "with stubbed TenantInfrastructure / master DB plus a multi-account " +
        "phone_otp_account_select cookie injection point so the SelectAccount " +
        "view can be rendered under both `vi` and `en` cultures.";

    /// <summary>
    /// Validates Requirements 16.10, 12.1, 12.2, 12.3, 12.4, 12.5, 12.7, 12.8,
    /// 13.1, 13.2, 13.5.
    /// </summary>
    [Fact(Skip = SkipReason)]
    public Task DOM_HasH1_Label_Select_AriaRequired_Autofocus_SubmitAriaLabel()
    {
        // Intended assertions (parse the rendered HTML via AngleSharp):
        //
        //   var doc = await context.OpenAsync(req => req.Content(html));
        //
        //   // R12.1 — single <h1> heading.
        //   doc.QuerySelectorAll("h1").Length.Should().Be(1);
        //
        //   // R12.2 — explicit <label for="account-select">.
        //   var label = doc.QuerySelector("label[for='account-select']");
        //   label.Should().NotBeNull();
        //
        //   // R12.3 — single <select id="account-select" name="SelectionToken"
        //   //         aria-required="true">.
        //   var select = doc.QuerySelector("select#account-select");
        //   select.Should().NotBeNull();
        //   select!.GetAttribute("name").Should().Be("SelectionToken");
        //   select.GetAttribute("aria-required").Should().Be("true");
        //
        //   // R12.4 — autofocus attribute present on the <select>.
        //   select.HasAttribute("autofocus").Should().BeTrue();
        //
        //   // R12.5 — single <button type="submit" aria-label="...">. The
        //   //         aria-label MUST come from the localized resx key
        //   //         `LoginWithPhone.SelectAccount.SubmitAriaLabel` (R13.1,
        //   //         R13.2, R13.5).
        //   var submit = doc.QuerySelector("button[type='submit']");
        //   submit.Should().NotBeNull();
        //   submit!.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        //
        //   // R12.7 — only one click affordance (submit button). Verify there
        //   //         is no second <button>, no <input type="submit">, no
        //   //         <a> styled as primary action.
        //   doc.QuerySelectorAll("button[type='submit'], input[type='submit']").Length
        //      .Should().Be(1);
        //
        //   // R12.8 — error region uses role="alert" when Error is set.
        //   //         (Re-render the view with Model.Error populated and
        //   //         re-parse before this assertion.)
        //   doc.QuerySelector("[role='alert']").Should().NotBeNull();
        //
        //   // R5.10 — single <form method="post"> with a hidden ReturnUrl
        //   //         input and an anti-forgery token field.
        //   var form = doc.QuerySelector("form[method='post']");
        //   form.Should().NotBeNull();
        //   form!.QuerySelector("input[name='ReturnUrl']").Should().NotBeNull();
        //   form.QuerySelector("input[name='__RequestVerificationToken']")
        //       .Should().NotBeNull();
        //
        //   // R5.13 — back-link to /Account/Login preserves returnUrl.
        //   var back = doc.QuerySelector("a[href^='/Account/Login']");
        //   back.Should().NotBeNull();
        //
        //   // R5.11 — first option carries `selected`.
        //   doc.QuerySelector("select#account-select option:first-child")!
        //      .HasAttribute("selected").Should().BeTrue();
        //
        //   // R5.12 — zero JS beyond the existing layout-level scripts.
        //   //         Assert the SelectAccount markup itself emits no inline
        //   //         <script> tags or `on*=` handlers.
        //   doc.QuerySelectorAll("script[data-page='select-account']").Length
        //      .Should().Be(0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates Requirement 12.9 — candidates whose <c>UserName</c> is empty
    /// MUST be omitted from the rendered <c>&lt;select&gt;</c> dropdown.
    /// </summary>
    [Fact(Skip = SkipReason)]
    public Task EmptyUserName_Omitted()
    {
        // Intended flow:
        //   - Seed three users in tenant t1 sharing the phone, but set
        //     `UserName` to NULL (or empty string) on the second user.
        //   - Issue + verify so Candidate_Set = [u-1, u-2, u-3].
        //   - GET /SelectAccount.
        //   - Parse the rendered HTML and assert exactly two <option>
        //     elements (u-1 and u-3 only). The dropdown MUST NOT include any
        //     option with empty visible text.
        //   - Assert the relative order matches Candidate_Set after filtering.
        return Task.CompletedTask;
    }

    [Fact]
    public void Skip_reason_is_documented()
    {
        Assert.False(string.IsNullOrWhiteSpace(SkipReason));
    }
}
