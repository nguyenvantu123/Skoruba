namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

public sealed record IssueOtpRequest(string RawPhone, string TenantKey, string RemoteIp, string ReturnUrl);
