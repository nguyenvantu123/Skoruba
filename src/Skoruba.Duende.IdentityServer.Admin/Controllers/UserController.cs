using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Skoruba.Duende.IdentityServer.Admin.UI.Services;
using Skoruba.Duende.IdentityServer.Admin.UI.Services.AntiForgeryProtection;
using Skoruba.Duende.IdentityServer.Admin.UI.Services.User;
using System.Security.Claims;

namespace Skoruba.Duende.IdentityServer.Admin.Controllers;

[ApiController]
[Route("[controller]")]
public class UserController(ILogger<UserController> logger) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    [AntiForgeryProtection]
    public async Task<ActionResult<UserClaimsDto>> Get()
    {
        if (User.Identity is not { IsAuthenticated: true })
        {
            return Ok(CreateUnauthenticatedResponse());
        }

        try
        {
            var tokenResult = await HttpContext.GetUserAccessTokenAsync();
            if (!tokenResult.Succeeded || string.IsNullOrWhiteSpace(tokenResult.Token?.AccessToken))
            {
                logger.LogWarning(
                    "Authenticated admin session for user {UserName} has no usable access token. Signing out the local cookie and forcing re-authentication.",
                    User.Identity?.Name ?? "unknown");

                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                return Ok(CreateUnauthenticatedResponse());
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to resolve a usable admin API access token for user {UserName}. Signing out the local cookie and forcing re-authentication.",
                User.Identity?.Name ?? "unknown");

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Ok(CreateUnauthenticatedResponse());
        }

        return Ok(new UserClaimsDto
        {
            IsAuthenticated = true,
            UserId = User.GetUserId()!,
            UserName = User.GetUserName()!, 
            Email = User.GetUserEmail()!,
            TenantKey = User.FindFirst("tenant_key")?.Value,
            FirstTimeLogin = bool.TryParse(User.FindFirst("firstTimeLogin")?.Value, out var firstTimeLogin) && firstTimeLogin,
            Roles = GetRoles(User)
        });
    }

    private static List<string> GetRoles(ClaimsPrincipal user)
    {
        return user.Claims
            .Where(x =>
                x.Type == ClaimTypes.Role ||
                x.Type == "role" ||
                x.Type == "roles" ||
                x.Type == "client_role")
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static UserClaimsDto CreateUnauthenticatedResponse()
    {
        return new UserClaimsDto
        {
            IsAuthenticated = false
        };
    }
}
