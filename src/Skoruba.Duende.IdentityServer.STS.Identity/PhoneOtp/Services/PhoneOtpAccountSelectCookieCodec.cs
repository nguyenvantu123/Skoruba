using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

/// <summary>
/// Payload đặt trong cookie <c>phone_otp_account_select</c> (Section 3.3 design).
/// Lock-in tại verify-success time, immutable trong cookie lifetime.
/// </summary>
public sealed record AccountSelectContext(
    string TenantKey,
    string PhoneE164Hash,
    IReadOnlyList<string> CandidateUserIds,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string OtpRecordKey,
    int Version = 1);

/// <summary>
/// Codec cho <c>phone_otp_account_select</c> cookie. Bảo vệ payload bằng
/// ASP.NET Core Data Protection với purpose <c>PhoneOtp.AccountSelectCookie</c>
/// (Section 4.3 design, Requirements 6.1, 6.2, 6.3).
/// </summary>
public sealed class PhoneOtpAccountSelectCookieCodec
{
    public const string CookieName = "phone_otp_account_select";

    private const string DataProtectionPurpose = "PhoneOtp.AccountSelectCookie";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDataProtector _protector;

    public PhoneOtpAccountSelectCookieCodec(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
    }

    public string Protect(AccountSelectContext payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        return _protector.Protect(json);
    }

    public bool TryUnprotect(string raw, out AccountSelectContext payload)
    {
        if (string.IsNullOrEmpty(raw))
        {
            payload = null!;
            return false;
        }

        try
        {
            var json = _protector.Unprotect(raw);
            var deserialized = JsonSerializer.Deserialize<AccountSelectContext>(json, SerializerOptions);

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
    }
}
