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
}
