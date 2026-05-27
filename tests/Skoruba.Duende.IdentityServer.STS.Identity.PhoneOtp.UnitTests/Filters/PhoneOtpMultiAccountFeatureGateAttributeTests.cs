using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Filters;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Filters;

/// <summary>
/// Tests cho <see cref="PhoneOtpMultiAccountFeatureGateAttribute"/> — gate
/// action <c>SelectAccount</c> dựa trên cặp flag
/// <c>PhoneOtpLogin:Enabled</c> + <c>MultiAccount:Enabled</c>
/// (Requirements 1.2, 1.8, 14.4).
/// </summary>
public class PhoneOtpMultiAccountFeatureGateAttributeTests
{
    private static ActionExecutingContext BuildContext(PhoneOtpLoginConfiguration config)
    {
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

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
    }

    private static (ActionExecutionDelegate next, Func<bool> wasCalled) BuildNextDelegate()
    {
        var called = false;
        ActionExecutionDelegate next = () =>
        {
            called = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        };
        return (next, () => called);
    }

    [Fact]
    public async Task ParentOff_Returns404()
    {
        var filter = new PhoneOtpMultiAccountFeatureGateAttribute();
        var context = BuildContext(new PhoneOtpLoginConfiguration
        {
            Enabled = false,
            MultiAccount = new MultiAccountConfiguration { Enabled = true },
        });
        var (next, wasCalled) = BuildNextDelegate();

        await filter.OnActionExecutionAsync(context, next);

        context.Result.Should().BeOfType<NotFoundResult>();
        wasCalled().Should().BeFalse();
    }

    [Fact]
    public async Task MultiOff_Returns404()
    {
        var filter = new PhoneOtpMultiAccountFeatureGateAttribute();
        var context = BuildContext(new PhoneOtpLoginConfiguration
        {
            Enabled = true,
            MultiAccount = new MultiAccountConfiguration { Enabled = false },
        });
        var (next, wasCalled) = BuildNextDelegate();

        await filter.OnActionExecutionAsync(context, next);

        context.Result.Should().BeOfType<NotFoundResult>();
        wasCalled().Should().BeFalse();
    }

    [Fact]
    public async Task BothOn_CallsNext()
    {
        var filter = new PhoneOtpMultiAccountFeatureGateAttribute();
        var context = BuildContext(new PhoneOtpLoginConfiguration
        {
            Enabled = true,
            MultiAccount = new MultiAccountConfiguration { Enabled = true },
        });
        var (next, wasCalled) = BuildNextDelegate();

        await filter.OnActionExecutionAsync(context, next);

        context.Result.Should().BeNull();
        wasCalled().Should().BeTrue();
    }
}
