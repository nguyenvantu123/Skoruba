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

    /// <summary>
    /// Sub-section <c>PhoneOtpLogin:MultiAccount</c>. Defaults inline tại
    /// <see cref="MultiAccountConfiguration"/>. Khi key absent trong appsettings,
    /// options binding tạo instance với defaults (Requirement 1.5).
    /// </summary>
    public MultiAccountConfiguration MultiAccount { get; set; } = new();
}
