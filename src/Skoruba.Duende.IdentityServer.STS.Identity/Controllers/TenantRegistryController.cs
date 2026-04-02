using IdentityModel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration.Constants;
using Skoruba.Duende.IdentityServer.STS.Identity.Dtos;
using Skoruba.Duende.IdentityServer.STS.Identity.Services;
using System.Threading;
using System.Threading.Tasks;
using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Controllers;

[ApiController]
[Route("api/tenant-admin/tenant-registry")]
public sealed class TenantRegistryController : ControllerBase
{
    private readonly ITenantRegistryLookupService _tenantRegistryLookupService;

    public TenantRegistryController(ITenantRegistryLookupService tenantRegistryLookupService)
    {
        _tenantRegistryLookupService = tenantRegistryLookupService;
    }

    [HttpGet]
    [Authorize(
        AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
        Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantRegistryLookupResultDto>> GetCurrentTenant([FromQuery] string? serviceName, CancellationToken cancellationToken)
    {
        var tenantKey = User.FindFirst(TenantClaimTypes.TenantKey)?.Value;
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            return Forbid();
        }

        var tenant = await _tenantRegistryLookupService.GetTenantAsync(tenantKey, serviceName, cancellationToken);
        if (tenant == null)
        {
            return NotFound();
        }

        return Ok(tenant);
    }
}
