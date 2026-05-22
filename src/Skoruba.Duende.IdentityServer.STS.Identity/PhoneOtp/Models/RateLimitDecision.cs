namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

public sealed record RateLimitDecision(bool Allowed, string? Reason, int? CooldownRemainingSeconds);
