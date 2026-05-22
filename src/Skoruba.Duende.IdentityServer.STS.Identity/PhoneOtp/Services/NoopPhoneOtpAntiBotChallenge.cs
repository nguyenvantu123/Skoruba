using Microsoft.AspNetCore.Http;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public sealed class NoopPhoneOtpAntiBotChallenge : IPhoneOtpAntiBotChallenge
{
    public Task<AntiBotDecision> EvaluateAsync(HttpContext context, CancellationToken ct)
    {
        return Task.FromResult(new AntiBotDecision(true, null));
    }
}
