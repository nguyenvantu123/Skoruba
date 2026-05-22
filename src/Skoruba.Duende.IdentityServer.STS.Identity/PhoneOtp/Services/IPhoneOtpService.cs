using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public interface IPhoneOtpService
{
    Task<IssueOtpResult> IssueAsync(IssueOtpRequest request, CancellationToken ct);
    Task<VerifyOtpResult> VerifyAsync(VerifyOtpRequest request, CancellationToken ct);
    Task<IssueOtpResult> ResendAsync(IssueOtpRequest request, CancellationToken ct);
}
