using System;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

/// <summary>
/// Default implementation của <see cref="ISelectionTokenProtector"/> sử dụng
/// <see cref="IDataProtector"/> với purpose <c>PhoneOtp.AccountSelectToken</c>
/// (Section 4.4 design).
/// </summary>
public sealed class SelectionTokenProtector : ISelectionTokenProtector
{
    private const string Purpose = "PhoneOtp.AccountSelectToken";

    private readonly IDataProtector _protector;

    public SelectionTokenProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string Issue(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var bytes = Encoding.UTF8.GetBytes(userId);
        return Base64UrlTextEncoder.Encode(_protector.Protect(bytes));
    }

    public bool TryResolve(string token, out string userId)
    {
        userId = string.Empty;

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        try
        {
            var bytes = Base64UrlTextEncoder.Decode(token);
            var plain = _protector.Unprotect(bytes);
            userId = Encoding.UTF8.GetString(plain);
            return !string.IsNullOrEmpty(userId);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
