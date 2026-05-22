namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

public sealed record AntiBotDecision(bool Allowed, string? Reason);
