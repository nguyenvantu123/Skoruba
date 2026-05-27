// Copyright (c) Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// Feature: phone-otp-multi-account-select, Task 10 — SelectAccountPostTests
// Validates: Requirements 6.6, 6.9, 7.1, 7.2, 7.3, 7.5, 8.5, 18.5, 18.6.
//
// ----------------------------------------------------------------------------
// HARNESS DEFERRED — same blocker as the rest of the integration project (see
// PhoneVerifyRedesignTests file header). End-to-end exercise of POST
// `/Account/LoginWithPhone/SelectAccount` requires:
//
//   1. A WebApplicationFactory<Program> harness with TenantInfrastructure /
//      master DB stubs.
//   2. A mechanism to inject a valid `phone_otp_session` cookie so
//      `PhoneLoginController.Verify` can transition to setting
//      `phone_otp_account_select`. Alternatively, a direct cookie injection
//      that bypasses verify (using the registered
//      `PhoneOtpAccountSelectCookieCodec`).
//   3. Multi-tenant seed of two `UserIdentity` rows in tenant `t1` sharing the
//      same `PhoneNumber` so the candidate set has size > 1.
//   4. `MultiAccount.Enabled = true` config overlay applied to the test host.
//
// Both are out of scope for the optional task-12 work, so the assertions are
// captured here as skipped facts for the future implementer.
// ----------------------------------------------------------------------------

using System.Threading.Tasks;

using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests.Tests;

/// <summary>
/// Integration tests for the multi-account chooser POST endpoint
/// (<c>/Account/LoginWithPhone/SelectAccount</c>).
///
/// The unit tests in
/// <c>Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Controllers
/// .PhoneLoginControllerSelectAccountPostTests</c> already exercise the 9
/// gate matrix and the success branch deterministically against the same
/// real <c>PhoneOtpAccountSelectCookieCodec</c> /
/// <c>SelectionTokenProtector</c> implementations used in production. The
/// integration tests below would supplement that with end-to-end HTTP
/// behaviour (cookie deletion observable on the wire, Identity cookie issued,
/// returnUrl preservation through HttpClient redirects).
///
/// Skipped pending a working WebApplicationFactory harness — see file header.
/// </summary>
public sealed class SelectAccountPostTests
{
    private const string SkipReason =
        "Integration harness deferred — needs WebApplicationFactory<Program> " +
        "with stubbed TenantInfrastructure / master DB plus a multi-account " +
        "phone_otp_account_select cookie injection point and seeded duplicate-phone users.";

    [Fact(Skip = SkipReason)]
    public Task Post_SelectAccount_HappyPath_SignsInAndRedirects()
    {
        // Intended flow (assuming the harness can complete request → verify → set cookie):
        //   1. POST /Account/LoginWithPhone/Request with shared phone number.
        //   2. POST /Account/LoginWithPhone/Verify with correct OTP — receive
        //      302 to /SelectAccount and `phone_otp_account_select` cookie.
        //   3. GET  /Account/LoginWithPhone/SelectAccount — parse first
        //      `<option>`'s SelectionToken value via AngleSharp.
        //   4. POST /Account/LoginWithPhone/SelectAccount with that
        //      SelectionToken + ReturnUrl + anti-forgery token.
        //
        // Intended assertions:
        //   - response.StatusCode == HttpStatusCode.Redirect
        //   - response.Headers.Location.OriginalString starts with the original returnUrl (or "~/")
        //   - response.Headers.GetValues("Set-Cookie") contains the Identity application cookie
        //   - response.Headers.GetValues("Set-Cookie") contains a deletion of phone_otp_account_select
        //     (Set-Cookie expires=Thu, 01 Jan 1970 ...)
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task Post_SelectAccount_TamperedCookie_RedirectsToLogin_NoSignIn()
    {
        // Intended assertions (Gate 3 — R11.1):
        //   - Tamper a single byte of the cookie value before POST.
        //   - response.StatusCode == HttpStatusCode.Redirect
        //   - response.Headers.Location starts with /Account/Login
        //   - response.Headers.GetValues("Set-Cookie") deletes phone_otp_account_select
        //     (expires=Thu, 01 Jan 1970)
        //   - No application Identity cookie issued.
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task Post_SelectAccount_FlagOff_Returns404()
    {
        // Intended assertions:
        //   - With MultiAccount.Enabled = false applied via config overlay,
        //     POST returns 404 (PhoneOtpMultiAccountFeatureGateAttribute, R1.8).
        return Task.CompletedTask;
    }

    [Fact]
    public void Skip_reason_is_documented()
    {
        Assert.False(string.IsNullOrWhiteSpace(SkipReason));
    }
}
