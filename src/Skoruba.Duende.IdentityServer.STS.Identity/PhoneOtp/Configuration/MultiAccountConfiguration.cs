namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;

/// <summary>
/// Sub-section <c>PhoneOtpLogin:MultiAccount</c> điều khiển hành vi nhánh
/// multi-account-select của luồng phone-OTP. Khi <see cref="Enabled"/> = false
/// (mặc định), behaviour của <c>PhoneOtpService</c> giữ nguyên: reject nhánh
/// <c>users.Count != 1</c>. Khi <see cref="Enabled"/> = true, <c>users.Count >= 1</c>
/// được chấp nhận và <see cref="SelectTtlSeconds"/> giới hạn lifetime của
/// cookie <c>phone_otp_account_select</c> sau verify thành công.
/// </summary>
/// <remarks>
/// Defaults verbatim Section 3.4 design (Requirement 1.5):
/// <c>Enabled=false</c>, <c>SelectTtlSeconds=60</c>,
/// <c>IpSelectRateLimitWindowSeconds=600</c>, <c>IpSelectRateLimitMaxRequests=30</c>.
/// Range hợp lệ được fail-fast validate trong <c>AddPhoneOtpLogin</c>
/// (Section 7.2 design, Requirements 1.6, 1.7, 18.2).
/// </remarks>
public sealed class MultiAccountConfiguration
{
    public bool Enabled { get; set; } = false;

    public int SelectTtlSeconds { get; set; } = 60;

    public int IpSelectRateLimitWindowSeconds { get; set; } = 600;

    public int IpSelectRateLimitMaxRequests { get; set; } = 30;
}
