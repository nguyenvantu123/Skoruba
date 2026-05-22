using Microsoft.AspNetCore.Http;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public interface IPhoneOtpAntiBotChallenge
{
    Task<AntiBotDecision> EvaluateAsync(HttpContext context, CancellationToken ct);
}
