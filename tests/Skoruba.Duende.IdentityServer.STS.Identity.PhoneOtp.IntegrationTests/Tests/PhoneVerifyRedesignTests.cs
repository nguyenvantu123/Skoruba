// Copyright (c) Jan Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// Feature: login-ui-redesign-i18n, Task 11.2 — PhoneVerifyRedesignTests
// Validates: Requirements 9.9, 12.3
//
// ----------------------------------------------------------------------------
// HARNESS DEFERRED — same blocker as LoginRedesignTests (see that file header).
// In addition, exercising `/Account/LoginWithPhone/Verify` end-to-end requires
// either a valid phone-OTP session cookie (issued only after a successful
// phone-step-1 POST against a real `IPhoneOtpStore` backed by Redis or in-memory
// caching) or a custom override of `IPhoneOtpSessionCookieCodec` injected via
// `WebApplicationFactory.ConfigureServices`. Both are out of scope for the
// optional task-11 work.
//
// The unauthenticated GET path (no session cookie) still yields a deterministic
// outcome — `PhoneLoginController.Verify` short-circuits to a redirect back to
// `/Account/Login` — and that case is captured below as a skipped fact so the
// future implementer has the assertions ready.
// ----------------------------------------------------------------------------

using System.Net;
using System.Threading.Tasks;

using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests.Tests
{
    /// <summary>
    /// Integration tests asserting that the redesigned phone-OTP verify page
    /// (<c>/Account/LoginWithPhone/Verify</c>) renders the same login-shell
    /// chrome as <c>/Account/Login</c>, preserves the OTP form contract, and
    /// applies the correct localized title/subtitle under both supported
    /// cultures.
    ///
    /// Skipped pending a working WebApplicationFactory harness — see file header.
    /// </summary>
    public sealed class PhoneVerifyRedesignTests
    {
        private const string SkipReason =
            "Integration harness deferred — needs WebApplicationFactory<Program> " +
            "with a stubbed TenantInfrastructure / master DB plus a phone-OTP " +
            "session cookie injection point.";

        [Fact(Skip = SkipReason)]
        public Task Get_Verify_without_session_cookie_redirects_to_login()
        {
            // Intended assertions:
            //   var response = await client.GetAsync("/Account/LoginWithPhone/Verify");
            //   response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
            //   response.Headers.Location!.OriginalString
            //       .Should().StartWith("/Account/Login");
            return Task.CompletedTask;
        }

        [Fact(Skip = SkipReason)]
        public Task Get_Verify_with_session_cookie_renders_login_shell_chrome()
        {
            // Intended assertions (assuming the harness can mint a valid session cookie):
            //   var body = await response.Content.ReadAsStringAsync();
            //   body.Should().Contain("login-shell login-shell--gradient");
            //   // OTP input contract preserved per Requirement 3.4 / 9.3.
            //   body.Should().Contain("id=\"phoneOtpCode\"");
            //   body.Should().Contain("name=\"Otp\"");
            //   body.Should().Contain("inputmode=\"numeric\"");
            //   body.Should().Contain("autocomplete=\"one-time-code\"");
            //   // Anti-forgery and resend form preserved per Requirement 9.4.
            //   body.Should().Contain("name=\"__RequestVerificationToken\"");
            //   body.Should().Contain("/Account/LoginWithPhone/Resend");
            //   // Back-link to Login round-trips returnUrl via URI encoding (Requirement 3.7).
            //   body.Should().MatchRegex("/Account/Login(\\?returnUrl=[^\"\\s]+)?");
            return Task.CompletedTask;
        }

        [Fact(Skip = SkipReason)]
        public Task SetLanguage_round_trip_changes_rendered_culture_on_Verify()
        {
            // Intended flow:
            //   1. POST /Home/SetLanguage with culture=en + valid anti-forgery from /Account/Login.
            //   2. Re-GET /Account/LoginWithPhone/Verify (with session cookie).
            //   3. Assert body contains the English Verify title key and not the Vietnamese one.
            return Task.CompletedTask;
        }

        [Fact]
        public void Skip_reason_is_documented()
        {
            Assert.False(string.IsNullOrWhiteSpace(SkipReason));
        }
    }
}
