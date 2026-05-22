namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Sms;

public sealed record SmsSendResult(bool Succeeded, string? ProviderMessageId, string? ErrorCode, string? ErrorMessage)
{
    public static SmsSendResult Ok(string messageId) => new(true, messageId, null, null);
    public static SmsSendResult Failed(string errorCode, string errorMessage) => new(false, null, errorCode, errorMessage);
}
