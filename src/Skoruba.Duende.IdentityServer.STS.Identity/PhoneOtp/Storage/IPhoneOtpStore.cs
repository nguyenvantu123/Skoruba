using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;

public interface IPhoneOtpStore
{
    Task<OtpStoreRecord?> GetAsync(string tenantKey, string phoneE164Hash, CancellationToken ct);
    Task SetAsync(string tenantKey, string phoneE164Hash, OtpStoreRecord record, TimeSpan ttl, CancellationToken ct);
    Task<int> IncrementAttemptAsync(string tenantKey, string phoneE164Hash, CancellationToken ct);
    Task DeleteAsync(string tenantKey, string phoneE164Hash, CancellationToken ct);
}
