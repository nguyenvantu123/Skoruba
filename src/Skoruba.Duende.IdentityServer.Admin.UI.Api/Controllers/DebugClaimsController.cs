using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class DebugClaimsController : ControllerBase
{
    private readonly AdminApiConfiguration _configuration;

    public DebugClaimsController(AdminApiConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet(nameof(GetCurrent))]
    public ActionResult<CurrentUserClaimsDebugDto> GetCurrent()
    {
        var claims = User.Claims
            .Select(x => new ClaimDebugItem
            {
                Type = x.Type,
                Value = x.Value
            })
            .ToList();

        return Ok(new CurrentUserClaimsDebugDto
        {
            IsAuthenticated = User.Identity?.IsAuthenticated ?? false,
            AuthenticationType = User.Identity?.AuthenticationType,
            Name = User.Identity?.Name,
            RoleClaimType = (User.Identity as ClaimsIdentity)?.RoleClaimType,
            HasAdminRole = User.IsInRole(_configuration.AdministrationRole),
            Claims = claims
        });
    }
}

public class CurrentUserClaimsDebugDto
{
    public bool IsAuthenticated { get; set; }

    public string? AuthenticationType { get; set; }

    public string? Name { get; set; }

    public string? RoleClaimType { get; set; }

    public bool HasAdminRole { get; set; }

    public List<ClaimDebugItem> Claims { get; set; } = [];
}

public class ClaimDebugItem
{
    public string Type { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
