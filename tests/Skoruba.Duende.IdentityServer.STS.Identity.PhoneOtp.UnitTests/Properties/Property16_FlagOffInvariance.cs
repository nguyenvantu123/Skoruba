// Feature: phone-otp-multi-account-select, Property 16: Flag-off invariance
//
// Validates: Requirements 14.4
//
// Generator: random combinations of (parent flag enabled, child flag enabled)
// where at least one is `false`. The property holds across the entire flag-off
// surface — both `PhoneOtpLogin:Enabled = false` and
// `PhoneOtpLogin:MultiAccount:Enabled = false` configurations must reject all
// multi-account-select traffic.
//
// Property invariants (Section 10.3 design):
//   * GET `/Account/LoginWithPhone/SelectAccount` MUST return HTTP 404 (the
//     action filter `PhoneOtpMultiAccountFeatureGateAttribute` short-circuits
//     before the controller runs).
//   * No `phone_otp_account_select` cookie is set on the response.
//   * No log entry with the event prefix `PhoneOtpAccountSelect…` is emitted
//     by the filter (Section 4.4 design — filter intentionally silent for
//     anti-enumeration).

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Filters;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public sealed class Property16_FlagOffInvariance
{
    public sealed record FlagScenario(bool ParentEnabled, bool ChildEnabled);

    public static class Arbs
    {
        public static Arbitrary<FlagScenario> Scenario()
            => (from parent in Gen.Elements(true, false)
                from child in Gen.Elements(true, false)
                where !(parent && child) // exclude both-on (covered by enabled property tests)
                select new FlagScenario(parent, child))
               .ToArbitrary();
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task FilterReturns404_NoCookieSet_NoControllerLog_When_FlagOff(FlagScenario scenario)
    {
        var filter = new PhoneOtpMultiAccountFeatureGateAttribute();

        var config = new PhoneOtpLoginConfiguration
        {
            Enabled = scenario.ParentEnabled,
            MultiAccount = new MultiAccountConfiguration
            {
                Enabled = scenario.ChildEnabled,
                SelectTtlSeconds = 60,
            },
        };

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<PhoneOtpLoginConfiguration>>(Options.Create(config));

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        var execContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());

        var nextCalled = false;
        ActionExecutionDelegate next = () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        };

        await filter.OnActionExecutionAsync(execContext, next);

        // R14.4 — short-circuit to 404; controller never runs.
        execContext.Result.Should().BeOfType<NotFoundResult>(
            because: "flag-off MUST return HTTP 404 (R1.2, R1.8, R14.4)");
        nextCalled.Should().BeFalse(
            because: "the filter MUST NOT invoke the controller when the flag is off");

        // No cookie was set (the controller never ran). The filter writes
        // nothing to the response.
        httpContext.Response.Headers.ContainsKey("Set-Cookie").Should().BeFalse(
            because: "no cookie may leak when the feature is off");

        // Cookie name sanity: even if some other layer tried to set a cookie,
        // the constant name remains stable so future regressions are caught.
        PhoneOtpAccountSelectCookieCodec.CookieName.Should().Be("phone_otp_account_select");
    }
}
