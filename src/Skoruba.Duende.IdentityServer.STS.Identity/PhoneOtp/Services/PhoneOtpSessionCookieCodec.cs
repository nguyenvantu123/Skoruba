using System;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public sealed record SessionCookiePayload(
    string TenantKey,
    string PhoneE164Hash,
    DateTimeOffset ExpiresAtUtc,
    int Version = 1);

public sealed class PhoneOtpSessionCookieCodec
{
    public const string CookieName = "phone_otp_session";

    private const string DataProtectionPurpose = "PhoneOtp.SessionCookie";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDataProtector _protector;

    public PhoneOtpSessionCookieCodec(IDataProtectionProvider dataProtectionProvider)
    {
        if (dataProtectionProvider is null)
        {
            throw new ArgumentNullException(nameof(dataProtectionProvider));
        }

        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
    }

    public string Protect(SessionCookiePayload payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        return _protector.Protect(json);
    }

    public bool TryUnprotect(string raw, out SessionCookiePayload payload)
    {
        if (string.IsNullOrEmpty(raw))
        {
            payload = null!;
            return false;
        }

        try
        {
            var json = _protector.Unprotect(raw);
            var deserialized = JsonSerializer.Deserialize<SessionCookiePayload>(json, SerializerOptions);

            if (deserialized is null)
            {
                payload = null!;
                return false;
            }

            payload = deserialized;
            return true;
        }
        catch (CryptographicException)
        {
            payload = null!;
            return false;
        }
        catch (JsonException)
        {
            payload = null!;
            return false;
        }
        catch (Exception)
        {
            payload = null!;
            return false;
        }
    }
}
