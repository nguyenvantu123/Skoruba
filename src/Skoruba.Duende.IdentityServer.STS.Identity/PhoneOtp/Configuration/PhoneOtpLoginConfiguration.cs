using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;

public sealed class PhoneOtpLoginConfiguration
{
    public bool Enabled { get; set; } = false;
    public int OtpLength { get; set; } = 6;
    public int OtpTtlSeconds { get; set; } = 300;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int MaxVerifyAttemptsPerOtp { get; set; } = 5;
    public int IpRateLimitWindowSeconds { get; set; } = 600;
    public int IpRateLimitMaxRequests { get; set; } = 10;
    public int PhoneVerifyLockoutWindowSeconds { get; set; } = 3600;
    public int PhoneVerifyLockoutMaxFailures { get; set; } = 10;
    public bool SmsDeliveryEnabled { get; set; } = true;
    public bool ShowGeneratedOtpWhenSmsDeliveryDisabled { get; set; } = true;
    public string DefaultRegion { get; set; } = "VN";
    public string RedisKeyPrefix { get; set; } = "otp:";
    public string StoreProvider { get; set; } = "Redis";
    public string MongoConnectionString { get; set; } = string.Empty;
    public string MongoDatabase { get; set; } = "identityserveradmin";
    public string MongoCollection { get; set; } = "phone_otp_records";
    public PhoneOtpUserApiConfiguration UserApi { get; set; } = new();
    public Dictionary<string, PhoneOtpTenantOverrideConfiguration> TenantOverrides { get; set; } = new();
    public MultiAccountConfiguration MultiAccount { get; set; } = new();

    public bool IsMultiAccountEnabled(string? tenantKey)
    {
        if (TryGetTenantOverride(tenantKey, out var tenantOverride) && tenantOverride.MultiAccountEnabled.HasValue)
        {
            return tenantOverride.MultiAccountEnabled.Value;
        }

        return MultiAccount.Enabled;
    }

    public bool HasAnyMultiAccountEnabled()
    {
        if (MultiAccount.Enabled)
        {
            return true;
        }

        foreach (var tenantOverride in TenantOverrides.Values)
        {
            if (tenantOverride.MultiAccountEnabled == true)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetTenantOverride(string? tenantKey, out PhoneOtpTenantOverrideConfiguration tenantOverride)
    {
        tenantOverride = default!;

        if (string.IsNullOrWhiteSpace(tenantKey) || TenantOverrides.Count == 0)
        {
            return false;
        }

        return TenantOverrides.TryGetValue(tenantKey.Trim(), out tenantOverride!);
    }
}
