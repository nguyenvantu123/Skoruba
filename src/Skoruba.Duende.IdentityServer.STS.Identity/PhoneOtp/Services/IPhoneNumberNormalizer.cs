namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public interface IPhoneNumberNormalizer
{
    bool TryNormalize(string raw, string defaultRegion, out string e164);
    string Format(string e164);
    string MaskLast4(string e164);
}
