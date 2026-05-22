using PhoneNumbers;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public sealed class PhoneNumberNormalizer : IPhoneNumberNormalizer
{
    private static readonly PhoneNumberUtil PhoneUtil = PhoneNumberUtil.GetInstance();

    public bool TryNormalize(string raw, string defaultRegion, out string e164)
    {
        e164 = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var number = PhoneUtil.Parse(raw, defaultRegion);

            if (!PhoneUtil.IsValidNumber(number))
            {
                return false;
            }

            e164 = PhoneUtil.Format(number, PhoneNumberFormat.E164);
            return true;
        }
        catch (NumberParseException)
        {
            return false;
        }
    }

    public string Format(string e164)
    {
        return e164;
    }

    public string MaskLast4(string e164)
    {
        if (string.IsNullOrEmpty(e164) || e164.Length < 4)
        {
            return "******";
        }

        return "******" + e164[^4..];
    }
}
