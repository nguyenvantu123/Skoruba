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
    public string DefaultRegion { get; set; } = "VN";
    public string RedisKeyPrefix { get; set; } = "otp:";
}
