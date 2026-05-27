using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Filters;
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

        // Cross-flag validation luôn chạy: sub-flag không được true khi parent off
        // (Section 3.4 / 7.2 design — Requirement 1.6 cross-flag rule).
        var multi = phoneOtpConfig.MultiAccount ?? new MultiAccountConfiguration();
        if (multi.Enabled && !phoneOtpConfig.Enabled)
        {
            throw new InvalidOperationException(
                "PhoneOtpLogin:MultiAccount:Enabled cannot be true when PhoneOtpLogin:Enabled is false.");
        }

        if (!phoneOtpConfig.Enabled)
        {
            // Feature OFF: only options + filter registered. Routes will return 404 via filter.
            // Range validation cho sub-section MultiAccount chỉ trigger khi parent-flag bật (R1.2).
            return services;
        }

        // Range validation MultiAccount (Requirements 1.5, 1.6, 1.7, 18.2 — Section 7.2 design).
        if (multi.SelectTtlSeconds < 30 || multi.SelectTtlSeconds > 180)
        {
            throw new InvalidOperationException(
                "PhoneOtpLogin:MultiAccount:SelectTtlSeconds must be in [30, 180].");
        }
        if (multi.IpSelectRateLimitWindowSeconds < 60 || multi.IpSelectRateLimitWindowSeconds > 3600)
        {
            throw new InvalidOperationException(
                "PhoneOtpLogin:MultiAccount:IpSelectRateLimitWindowSeconds must be in [60, 3600].");
        }
        if (multi.IpSelectRateLimitMaxRequests < 5 || multi.IpSelectRateLimitMaxRequests > 200)
        {
            throw new InvalidOperationException(
                "PhoneOtpLogin:MultiAccount:IpSelectRateLimitMaxRequests must be in [5, 200].");
        }
        // DataProtector probe: skip build-time (Section 7.2 design notes), runtime fail-fast khi
        // PhoneOtpAccountSelectCookieCodec được resolve (sẽ register ở Task 6).

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

        // Multi-account select branch (Section 7.3 design, Requirement 1.8). Register codec +
        // selection-token protector + action filter chỉ khi cả parent flag và sub-flag đều bật.
        // DataProtector probe (R6.12): constructor của codec/protector throw nếu provider null
        // hoặc CreateProtector fail — fail-fast acceptable.
        if (multi.Enabled)
        {
            services.AddSingleton<PhoneOtpAccountSelectCookieCodec>();
            services.AddSingleton<ISelectionTokenProtector, SelectionTokenProtector>();
            services.AddSingleton<PhoneOtpMultiAccountFeatureGateAttribute>();
        }

        return services;
    }
}
