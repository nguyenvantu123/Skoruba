namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

public sealed record VerifyOtpRequest(string TenantKey, string PhoneE164Hash, string SubmittedOtp, string RemoteIp);
