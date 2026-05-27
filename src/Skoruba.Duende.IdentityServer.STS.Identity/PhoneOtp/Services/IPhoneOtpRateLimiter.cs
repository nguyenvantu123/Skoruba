using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public interface IPhoneOtpRateLimiter
{
    Task<RateLimitDecision> CheckPhoneCooldownAsync(string tenantKey, string phoneE164Hash, CancellationToken ct);
    Task RegisterPhoneIssuanceAsync(string tenantKey, string phoneE164Hash, CancellationToken ct);
    Task<RateLimitDecision> CheckIpAsync(string ipHash, CancellationToken ct);
    Task RegisterIpIssuanceAsync(string ipHash, CancellationToken ct);
    Task<RateLimitDecision> CheckPhoneLockoutAsync(string tenantKey, string phoneE164Hash, CancellationToken ct);
    Task RegisterVerifyFailureAsync(string tenantKey, string phoneE164Hash, CancellationToken ct);

    /// <summary>
    /// NEW. Tăng counter per-IP cho POST <c>/Account/LoginWithPhone/SelectAccount</c>
    /// (Requirement 18.5). Counter share TTL = <c>MultiAccount.IpSelectRateLimitWindowSeconds</c>.
    /// Phải được gọi <em>mỗi POST</em> bất kể outcome (Section 4.2 design).
    /// </summary>
    Task RegisterIpSelectAttemptAsync(string ipHash, CancellationToken ct);

    /// <summary>
    /// NEW. Trả về <see cref="RateLimitDecision.Allowed"/> = <c>false</c> với
    /// <see cref="RateLimitDecision.Reason"/> = <c>"IpSelectWindow"</c> khi counter
    /// hiện tại &gt;= <c>MultiAccount.IpSelectRateLimitMaxRequests</c>
    /// (Requirement 18.3, Section 4.2 design).
    /// </summary>
    Task<RateLimitDecision> CheckIpSelectAsync(string ipHash, CancellationToken ct);
}
