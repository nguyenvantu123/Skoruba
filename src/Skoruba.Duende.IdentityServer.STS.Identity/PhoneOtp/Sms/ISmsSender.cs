namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Sms;

public interface ISmsSender
{
    Task<SmsSendResult> SendAsync(string e164PhoneNumber, string body, CancellationToken cancellationToken);
}
