namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

public sealed record OtpStoreRecord
{
    public byte[] OtpHash { get; init; } = Array.Empty<byte>();
    public string TenantKey { get; init; } = string.Empty;
    public string PhoneE164 { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public int AttemptCount { get; init; }
}
