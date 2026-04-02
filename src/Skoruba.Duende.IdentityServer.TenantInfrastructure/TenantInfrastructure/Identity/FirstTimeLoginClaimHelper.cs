using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;

namespace TenantInfrastructure.Identity;

public static class FirstTimeLoginClaimHelper
{
    public static string ToClaimValue(bool value)
    {
        return value ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant();
    }

    public static bool ParseClaimValue(string? value, bool defaultValue = false)
    {
        return bool.TryParse(value, out var parsedValue) ? parsedValue : defaultValue;
    }

    public static async Task<bool> GetFirstTimeLoginAsync<TUser>(UserManager<TUser> userManager, TUser user, bool defaultValue = false)
        where TUser : class
    {
        var claims = await userManager.GetClaimsAsync(user);
        var claimValue = claims.FirstOrDefault(x => x.Type == TenantClaimTypes.FirstTimeLogin)?.Value;
        return ParseClaimValue(claimValue, defaultValue);
    }

    public static async Task<IdentityResult> SetFirstTimeLoginAsync<TUser>(UserManager<TUser> userManager, TUser user, bool value)
        where TUser : class
    {
        var desiredValue = ToClaimValue(value);
        var claims = await userManager.GetClaimsAsync(user);
        var existingClaim = claims.FirstOrDefault(x => x.Type == TenantClaimTypes.FirstTimeLogin);

        if (existingClaim != null && string.Equals(existingClaim.Value, desiredValue, StringComparison.OrdinalIgnoreCase))
        {
            return IdentityResult.Success;
        }

        var claim = new Claim(TenantClaimTypes.FirstTimeLogin, desiredValue);
        if (existingClaim == null)
        {
            return await userManager.AddClaimAsync(user, claim);
        }

        return await userManager.ReplaceClaimAsync(user, existingClaim, claim);
    }
}
