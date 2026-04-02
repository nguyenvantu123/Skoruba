using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using IdentityModel;
using Microsoft.AspNetCore.Identity;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

public sealed class TenantProfileService : IProfileService
{
    private readonly UserManager<UserIdentity> _userManager;

    public TenantProfileService(UserManager<UserIdentity> userManager)
    {
        _userManager = userManager;
    }

    public async Task GetProfileDataAsync(ProfileDataRequestContext context)
    {
        var user = await ResolveUserAsync(context.Subject);
        if (user == null)
        {
            return;
        }

        var userRoles = await _userManager.GetRolesAsync(user);
        foreach (var role in userRoles)
        {
            if (ShouldIssueClaim(context, JwtClaimTypes.Role, role, alwaysIssueForAccessToken: true))
            {
                context.IssuedClaims.Add(new Claim(JwtClaimTypes.Role, role));
            }

            if (ShouldIssueClaim(context, ClaimTypes.Role, role, alwaysIssueForAccessToken: true))
            {
                context.IssuedClaims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        if (!string.IsNullOrWhiteSpace(user.TenantKey) &&
            ShouldIssueClaim(context, TenantClaimTypes.TenantKey, user.TenantKey, alwaysIssueForAccessToken: true))
        {
            context.IssuedClaims.Add(new Claim(TenantClaimTypes.TenantKey, user.TenantKey));
        }

        var firstTimeLogin = await FirstTimeLoginClaimHelper.GetFirstTimeLoginAsync(_userManager, user);
        var firstTimeLoginValue = FirstTimeLoginClaimHelper.ToClaimValue(firstTimeLogin);
        if (ShouldIssueClaim(context, TenantClaimTypes.FirstTimeLogin, firstTimeLoginValue, alwaysIssueForUserInfo: true))
        {
            context.IssuedClaims.Add(new Claim(TenantClaimTypes.FirstTimeLogin, firstTimeLoginValue, ClaimValueTypes.Boolean));
        }
    }

    public async Task IsActiveAsync(IsActiveContext context)
    {
        var user = await ResolveUserAsync(context.Subject);
        context.IsActive = user != null;
    }

    private async Task<UserIdentity> ResolveUserAsync(ClaimsPrincipal subject)
    {
        var subjectId = subject.FindFirst(JwtClaimTypes.Subject)?.Value
                        ?? subject.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? _userManager.GetUserId(subject);

        if (string.IsNullOrWhiteSpace(subjectId))
        {
            return await _userManager.GetUserAsync(subject);
        }

        return await _userManager.FindByIdAsync(subjectId);
    }

    private static bool ShouldIssueClaim(
        ProfileDataRequestContext context,
        string claimType,
        string claimValue,
        bool alwaysIssueForAccessToken = false,
        bool alwaysIssueForUserInfo = false)
    {
        if (alwaysIssueForAccessToken && context.Caller.IndexOf("AccessToken", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return !context.IssuedClaims.Any(c => c.Type == claimType && c.Value == claimValue);
        }

        if (alwaysIssueForUserInfo && context.Caller.IndexOf("UserInfo", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return !context.IssuedClaims.Any(c => c.Type == claimType && c.Value == claimValue);
        }

        var requestedClaims = context.RequestedClaimTypes;
        var isRequested = requestedClaims == null || !requestedClaims.Any() || requestedClaims.Contains(claimType);
        if (!isRequested)
        {
            return false;
        }

        return !context.IssuedClaims.Any(c => c.Type == claimType && c.Value == claimValue);
    }
}
