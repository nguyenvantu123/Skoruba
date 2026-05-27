using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Configuration;

/// <summary>
/// Tests cho 4 rule fail-fast validation của <c>PhoneOtpLogin:MultiAccount</c>
/// trong <see cref="PhoneOtpServiceCollectionExtensions.AddPhoneOtpLogin"/>
/// (Section 7.2 design — Requirements 1.5, 1.6, 1.7, 18.2).
/// </summary>
public class MultiAccountConfigurationValidationTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> overrides)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["PhoneOtpLogin:Enabled"] = "true",
            ["PhoneOtpLogin:OtpLength"] = "6",
            ["PhoneOtpLogin:DefaultRegion"] = "VN",
            ["PhoneOtpLogin:MultiAccount:Enabled"] = "true",
            ["PhoneOtpLogin:MultiAccount:SelectTtlSeconds"] = "60",
            ["PhoneOtpLogin:MultiAccount:IpSelectRateLimitWindowSeconds"] = "600",
            ["PhoneOtpLogin:MultiAccount:IpSelectRateLimitMaxRequests"] = "30",
        };

        foreach (var kvp in overrides)
            defaults[kvp.Key] = kvp.Value;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaults)
            .Build();
    }

    private static InvalidOperationException ExpectThrow(IDictionary<string, string?> overrides)
    {
        var services = new ServiceCollection();
        var config = BuildConfig(overrides);
        var act = () => services.AddPhoneOtpLogin(config);
        return act.Should().Throw<InvalidOperationException>().Which;
    }

    [Theory]
    [InlineData("29")]
    [InlineData("181")]
    public void SelectTtl_OutOfRange_Throws(string ttlSeconds)
    {
        var ex = ExpectThrow(new Dictionary<string, string?>
        {
            ["PhoneOtpLogin:MultiAccount:SelectTtlSeconds"] = ttlSeconds,
        });

        ex.Message.Should().Contain("SelectTtlSeconds");
        ex.Message.Should().Contain("[30, 180]");
    }

    [Theory]
    [InlineData("59")]
    [InlineData("3601")]
    public void IpSelectWindow_OutOfRange_Throws(string seconds)
    {
        var ex = ExpectThrow(new Dictionary<string, string?>
        {
            ["PhoneOtpLogin:MultiAccount:IpSelectRateLimitWindowSeconds"] = seconds,
        });

        ex.Message.Should().Contain("IpSelectRateLimitWindowSeconds");
        ex.Message.Should().Contain("[60, 3600]");
    }

    [Theory]
    [InlineData("4")]
    [InlineData("201")]
    public void IpSelectMax_OutOfRange_Throws(string maxRequests)
    {
        var ex = ExpectThrow(new Dictionary<string, string?>
        {
            ["PhoneOtpLogin:MultiAccount:IpSelectRateLimitMaxRequests"] = maxRequests,
        });

        ex.Message.Should().Contain("IpSelectRateLimitMaxRequests");
        ex.Message.Should().Contain("[5, 200]");
    }

    [Fact]
    public void SubFlag_True_When_ParentFalse_Throws()
    {
        var ex = ExpectThrow(new Dictionary<string, string?>
        {
            ["PhoneOtpLogin:Enabled"] = "false",
            ["PhoneOtpLogin:MultiAccount:Enabled"] = "true",
        });

        ex.Message.Should().Contain("PhoneOtpLogin:MultiAccount:Enabled");
        ex.Message.Should().Contain("PhoneOtpLogin:Enabled");
    }
}
