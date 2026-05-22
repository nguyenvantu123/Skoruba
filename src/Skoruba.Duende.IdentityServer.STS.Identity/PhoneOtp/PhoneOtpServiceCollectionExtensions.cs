using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Sms;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp;

public static class PhoneOtpServiceCollectionExtensions
{
    public static IServiceCollection AddPhoneOtpLogin(this IServiceCollection services, IConfiguration configuration)
    {
        // Always bind options so that the FeatureGate filter can read Enabled flag.
        services.Configure<PhoneOtpLoginConfiguration>(configuration.GetSection("PhoneOtpLogin"));
        services.Configure<SmsTwilioConfiguration>(configuration.GetSection("SmsConfiguration:Twilio"));

        var phoneOtpConfig = configuration.GetSection("PhoneOtpLogin").Get<PhoneOtpLoginConfiguration>()
                            ?? new PhoneOtpLoginConfiguration();

        if (!phoneOtpConfig.Enabled)
        {
            // Feature OFF: only options + filter registered. Routes will return 404 via filter.
            return services;
        }

        // Validate OtpLength.
        if (phoneOtpConfig.OtpLength < 4 || phoneOtpConfig.OtpLength > 10)
        {
            throw new InvalidOperationException(
                "PhoneOtpLogin:OtpLength must be between 4 and 10.");
        }

        // Validate DefaultRegion (ISO-3166 alpha-2: 2 uppercase letters).
        if (string.IsNullOrWhiteSpace(phoneOtpConfig.DefaultRegion)
            || phoneOtpConfig.DefaultRegion.Length != 2)
        {
            throw new InvalidOperationException(
                "PhoneOtpLogin:DefaultRegion must be an ISO-3166 alpha-2 region code (e.g., 'VN').");
        }

        // Twilio config validation: hard fail-fast in Production, soft fallback in non-Production.
        var twilioConfig = configuration.GetSection("SmsConfiguration:Twilio").Get<SmsTwilioConfiguration>()
                           ?? new SmsTwilioConfiguration();

        var environment = configuration["ASPNETCORE_ENVIRONMENT"]
                          ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isProduction = string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);

        var twilioConfigComplete = !string.IsNullOrWhiteSpace(twilioConfig.AccountSid)
                                  && !string.IsNullOrWhiteSpace(twilioConfig.AuthToken)
                                  && !string.IsNullOrWhiteSpace(twilioConfig.FromNumber);

        if (isProduction && !twilioConfigComplete)
        {
            if (string.IsNullOrWhiteSpace(twilioConfig.AccountSid))
                throw new InvalidOperationException(
                    "PhoneOtpLogin is enabled in Production but SmsConfiguration:Twilio:AccountSid is not configured.");
            if (string.IsNullOrWhiteSpace(twilioConfig.AuthToken))
                throw new InvalidOperationException(
                    "PhoneOtpLogin is enabled in Production but SmsConfiguration:Twilio:AuthToken is not configured.");
            if (string.IsNullOrWhiteSpace(twilioConfig.FromNumber))
                throw new InvalidOperationException(
                    "PhoneOtpLogin is enabled in Production but SmsConfiguration:Twilio:FromNumber is not configured.");
        }

        // TimeProvider: use System default. Tests will override via TestServer.
        services.TryAddSingleton(TimeProvider.System);

        // Singleton/scoped registrations.
        services.AddSingleton<IPhoneNumberNormalizer, PhoneNumberNormalizer>();
        services.AddScoped<IPhoneOtpStore, RedisPhoneOtpStore>();
        services.AddScoped<IPhoneOtpRateLimiter, PhoneOtpRateLimiter>();
        services.AddScoped<IPhoneOtpService, PhoneOtpService>();
        services.AddSingleton<PhoneOtpSessionCookieCodec>();
        services.AddSingleton<IPhoneOtpAntiBotChallenge, NoopPhoneOtpAntiBotChallenge>();

        // SMS sender: Twilio if config complete, otherwise Fake (logs a warning).
        if (twilioConfigComplete)
        {
            services.AddSingleton<ISmsSender, TwilioSmsSender>();
        }
        else
        {
            // Non-Production with missing Twilio config: register Fake sender. Log Warning at first use.
            services.AddSingleton<ISmsSender, FakeSmsSender>();
            // Note: Logging the missing keys at startup requires an ILogger which we don't have here.
            // The FakeSmsSender registration itself signals dev mode. A startup IHostedService could
            // log Warning, but adding that is out of scope for this task.
        }

        return services;
    }
}
