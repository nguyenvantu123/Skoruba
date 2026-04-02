using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration.Constants;
using Skoruba.Duende.IdentityServer.STS.Identity.Dtos;
using Skoruba.Duende.IdentityServer.STS.Identity.Services;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Controllers;

[ApiController]
[Route("api/tenant-admin/account")]
public sealed class TenantAdminAccountController : ControllerBase
{
    private readonly ITenantAdminAccountService _tenantAdminAccountService;

    public TenantAdminAccountController(ITenantAdminAccountService tenantAdminAccountService)
    {
        _tenantAdminAccountService = tenantAdminAccountService;
    }

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantAdminAccountDto>> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var account = await _tenantAdminAccountService.GetCurrentAsync(User, cancellationToken);
        return account == null ? TenantAdminAccountUnavailable<TenantAdminAccountDto>() : Ok(account);
    }

    [HttpGet("personal-data")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantAdminPersonalDataDto>> GetPersonalDataAsync(CancellationToken cancellationToken)
    {
        var personalData = await _tenantAdminAccountService.GetPersonalDataAsync(User, cancellationToken);
        return personalData == null ? TenantAdminAccountUnavailable<TenantAdminPersonalDataDto>() : Ok(personalData);
    }

    [HttpPut("password")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantAdminChangePasswordResultDto>> ChangePasswordAsync([FromBody] TenantAdminChangePasswordDto request, CancellationToken cancellationToken)
    {
        var result = await _tenantAdminAccountService.ChangePasswordAsync(User, request, cancellationToken);
        if (result == null)
        {
            return TenantAdminAccountUnavailable<TenantAdminChangePasswordResultDto>();
        }

        if (result.Success)
        {
            return Ok(result);
        }

        foreach (var error in result.Errors.Where(error => !string.IsNullOrWhiteSpace(error)))
        {
            ModelState.AddModelError(nameof(TenantAdminChangePasswordDto.NewPassword), error);
        }

        return ValidationProblem(ModelState);
    }

    [HttpGet("two-factor")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantAdminTwoFactorStatusDto>> GetTwoFactorStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _tenantAdminAccountService.GetTwoFactorStatusAsync(User, cancellationToken);
        return status == null ? TenantAdminAccountUnavailable<TenantAdminTwoFactorStatusDto>() : Ok(status);
    }

    [HttpPost("two-factor/setup")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantAdminTwoFactorSetupDto>> GetTwoFactorSetupAsync(CancellationToken cancellationToken)
    {
        var setup = await _tenantAdminAccountService.GetTwoFactorSetupAsync(User, cancellationToken);
        return setup == null ? TenantAdminAccountUnavailable<TenantAdminTwoFactorSetupDto>() : Ok(setup);
    }

    [HttpPost("two-factor/enable")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantAdminTwoFactorOperationResultDto>> EnableTwoFactorAsync([FromBody] TenantAdminVerifyTwoFactorDto request, CancellationToken cancellationToken)
    {
        return await HandleTwoFactorResultAsync(_tenantAdminAccountService.EnableTwoFactorAsync(User, request, cancellationToken));
    }

    [HttpPost("two-factor/disable")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantAdminTwoFactorOperationResultDto>> DisableTwoFactorAsync(CancellationToken cancellationToken)
    {
        return await HandleTwoFactorResultAsync(_tenantAdminAccountService.DisableTwoFactorAsync(User, cancellationToken));
    }

    [HttpPost("two-factor/reset-authenticator")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantAdminTwoFactorOperationResultDto>> ResetAuthenticatorAsync(CancellationToken cancellationToken)
    {
        return await HandleTwoFactorResultAsync(_tenantAdminAccountService.ResetAuthenticatorAsync(User, cancellationToken));
    }

    [HttpPost("two-factor/recovery-codes")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AuthorizationConsts.TenantAdminApiPolicy)]
    public async Task<ActionResult<TenantAdminTwoFactorOperationResultDto>> GenerateRecoveryCodesAsync(CancellationToken cancellationToken)
    {
        return await HandleTwoFactorResultAsync(_tenantAdminAccountService.GenerateRecoveryCodesAsync(User, cancellationToken));
    }

    private async Task<ActionResult<TenantAdminTwoFactorOperationResultDto>> HandleTwoFactorResultAsync(Task<TenantAdminTwoFactorOperationResultDto?> task)
    {
        var result = await task;
        if (result == null)
        {
            return TenantAdminAccountUnavailable<TenantAdminTwoFactorOperationResultDto>();
        }

        if (result.Success)
        {
            return Ok(result);
        }

        foreach (var error in result.Errors.Where(error => !string.IsNullOrWhiteSpace(error)))
        {
            ModelState.AddModelError(nameof(TenantAdminVerifyTwoFactorDto.Code), error);
        }

        return ValidationProblem(ModelState);
    }

    private ObjectResult TenantAdminAccountUnavailable<T>()
    {
        return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Tenant admin account unavailable",
            Detail = "The current token could not be matched to an active central tenant-admin account for this tenant."
        });
    }
}
