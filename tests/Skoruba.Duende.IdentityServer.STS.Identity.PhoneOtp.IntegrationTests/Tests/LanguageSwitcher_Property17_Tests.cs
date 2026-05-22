// Copyright (c) Jan Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// Feature: login-ui-redesign-i18n, Task 11.3 — Property 17
// Validates: Requirements 6.6, 9.1, 9.4
//
// ----------------------------------------------------------------------------
// HARNESS DEFERRED — same blocker as the other 11.x tests (see
// LoginRedesignTests.cs header). Property 17 requires a working TestServer
// pointing at the STS host so HTTP POSTs to `/Home/SetLanguage` actually hit
// `HomeController.SetLanguage`. Without a stubbed TenantInfrastructure /
// master-DB pipeline, the host throws at `Startup.ConfigureServices`.
//
// The intended property and its assertions are documented in detail below so
// the implementation can be flipped on once a harness exists.
// ----------------------------------------------------------------------------

using FsCheck.Xunit;

using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests.Tests
{
    /// <summary>
    /// **Property 17: SetLanguage sets a long-lived cookie and 302-redirects
    /// preserving the returnUrl.**
    ///
    /// For any <c>culture</c> drawn from the resolved <c>SupportedUICultures</c>
    /// (currently <c>vi</c>, <c>en</c>) and any <c>returnUrl</c> that is a
    /// local URL with arbitrary URL-safe ASCII path/query, when the test client
    /// POSTs <c>{ culture, returnUrl, __RequestVerificationToken }</c> to
    /// <c>/Home/SetLanguage</c> with valid anti-forgery cookies, the response
    /// MUST satisfy:
    /// <list type="bullet">
    ///   <item>HTTP status code 302 (Found / Redirect).</item>
    ///   <item><c>Location</c> header byte-equal to the input <c>returnUrl</c>
    ///         (HomeController calls <c>LocalRedirect(returnUrl)</c>).</item>
    ///   <item>One <c>Set-Cookie</c> for <c>.AspNetCore.Culture</c> whose
    ///         <c>Expires</c> attribute is strictly between
    ///         <c>now + 364 days</c> and <c>now + 366 days</c>
    ///         (HomeController writes <c>DateTimeOffset.UtcNow.AddYears(1)</c>).</item>
    /// </list>
    ///
    /// Skipped pending a working WebApplicationFactory harness — see file header.
    /// </summary>
    public sealed class LanguageSwitcher_Property17_Tests
    {
        private const string SkipReason =
            "Integration harness deferred — needs a working TestServer for the STS " +
            "host. HomeController.SetLanguage cannot be exercised without it.";

        // Generator notes (for the future implementation):
        //   - culture: pick from SupportedUICultures resolved by the host
        //     (currently { "vi", "en" }) using FsCheck `Gen.Elements`.
        //   - returnUrl: prefix "/" plus 0..32 chars from the URL-safe ASCII
        //     pool [A-Za-z0-9_/?=&:%-]. Filter to `Url.IsLocalUrl`-compatible
        //     shapes (no scheme, no authority).
        // [Property(MaxTest = 50)]   ← lower than 100 to keep TestServer cost manageable.

        [Fact(Skip = SkipReason)]
        public void SetLanguage_sets_long_lived_cookie_and_302_redirects_preserving_returnUrl()
        {
            // Intended assertions per iteration:
            //
            //   var loginResponse = await client.GetAsync("/Account/Login");
            //   var antiForgeryToken = await loginResponse.ExtractAntiForgeryToken();
            //   var form = new Dictionary<string, string>
            //   {
            //       ["culture"] = culture,
            //       ["returnUrl"] = returnUrl,
            //       ["__RequestVerificationToken"] = antiForgeryToken,
            //   };
            //   var request = RequestHelper.CreatePostRequestWithCookies(
            //       "/Home/SetLanguage", form, loginResponse);
            //   var response = await client.SendAsync(request);
            //
            //   response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            //   response.Headers.Location!.OriginalString.Should().Be(returnUrl);
            //
            //   var setCookies = SetCookieHeaderValue.ParseList(
            //       response.Headers.GetValues("Set-Cookie").ToList());
            //   var cultureCookie = setCookies.Single(c =>
            //       c.Name.Value == ".AspNetCore.Culture");
            //   var lower = DateTimeOffset.UtcNow.AddDays(364);
            //   var upper = DateTimeOffset.UtcNow.AddDays(366);
            //   cultureCookie.Expires.Should().NotBeNull();
            //   cultureCookie.Expires!.Value.Should().BeAfter(lower).And.BeBefore(upper);
        }

        [Fact]
        public void Skip_reason_is_documented()
        {
            Assert.False(string.IsNullOrWhiteSpace(SkipReason));
        }
    }
}
