namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

public sealed record VerifyOtpResult(VerifyOutcome Outcome, string? UserId, int AttemptCount);
