// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using IdentityModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Identity.Dtos.Identity;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Identity.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.Constants;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Roles;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Users;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.ExceptionHandling;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers.Localization;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Resources;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.Identity;
using Microsoft.Extensions.Logging;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [TypeFilter(typeof(ControllerExceptionFilterAttribute))]
    [Produces("application/json", "application/problem+json")]
    public class UsersController<TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
            TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
            TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto> : ControllerBase
        where TUserDto : UserDto<TKey>, new()
        where TRoleDto : RoleDto<TKey>, new()
        where TUser : IdentityUser<TKey>
        where TRole : IdentityRole<TKey>
        where TKey : IEquatable<TKey>
        where TUserClaim : IdentityUserClaim<TKey>
        where TUserRole : IdentityUserRole<TKey>
        where TUserLogin : IdentityUserLogin<TKey>
        where TRoleClaim : IdentityRoleClaim<TKey>
        where TUserToken : IdentityUserToken<TKey>
        where TUsersDto : UsersDto<TUserDto, TKey>
        where TRolesDto : RolesDto<TRoleDto, TKey>
        where TUserRolesDto : UserRolesDto<TRoleDto, TKey>
        where TUserClaimsDto : UserClaimsDto<TUserClaimDto, TKey>, new()
        where TUserProviderDto : UserProviderDto<TKey>
        where TUserProvidersDto : UserProvidersDto<TUserProviderDto, TKey>
        where TUserChangePasswordDto : UserChangePasswordDto<TKey>
        where TRoleClaimsDto : RoleClaimsDto<TRoleClaimDto, TKey>
        where TUserClaimDto : UserClaimDto<TKey>
        where TRoleClaimDto : RoleClaimDto<TKey>
    {
        private readonly IIdentityService<TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
            TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
            TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto> _identityService;
        private readonly IGenericControllerLocalizer<UsersController<TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
            TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
            TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto>> _localizer;

        private readonly IMapper _mapper;
        private readonly IApiErrorResources _errorResources;
        private readonly ITenantRoleProvider _tenantRoleProvider;
        private readonly ITenantContextAccessor _tenantContextAccessor;
        private readonly ILogger<UsersController<TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
            TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
            TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto>> _logger;

        public UsersController(IIdentityService<TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
                TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
                TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto> identityService,
            IGenericControllerLocalizer<UsersController<TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
                TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
                TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto>> localizer, IMapper mapper, IApiErrorResources errorResources,
            ITenantRoleProvider tenantRoleProvider, ITenantContextAccessor tenantContextAccessor,
            ILogger<UsersController<TUserDto, TRoleDto, TUser, TRole, TKey, TUserClaim, TUserRole, TUserLogin, TRoleClaim, TUserToken,
                TUsersDto, TRolesDto, TUserRolesDto, TUserClaimsDto,
                TUserProviderDto, TUserProvidersDto, TUserChangePasswordDto, TRoleClaimsDto, TUserClaimDto, TRoleClaimDto>> logger)
        {
            _identityService = identityService;
            _localizer = localizer;
            _mapper = mapper;
            _errorResources = errorResources;
            _tenantRoleProvider = tenantRoleProvider;
            _tenantContextAccessor = tenantContextAccessor;
            _logger = logger;
        }

        [HttpGet("{id}")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        public async Task<ActionResult<TUserDto>> Get(TKey id)
        {
            var user = await _identityService.GetUserAsync(id.ToString());

            return Ok(user);
        }

        [HttpGet]
        [Authorize(Policy = AuthorizationConsts.AdministrationPolicy)]
        public async Task<ActionResult<TUsersDto>> Get(
            [FromQuery] string? searchText = null,
            [FromQuery(Name = "search")] string? search = null,
            [FromQuery] int page = 1,
            [FromQuery(Name = "pageNumber")] int? pageNumber = null,
            [FromQuery] int pageSize = 10)
        {
            var normalizedSearch = searchText ?? search ?? string.Empty;
            var normalizedPage = pageNumber ?? page;
            var tenantScopedKey = ResolveTenantScope();
            var usersDto = await _identityService.GetUsersAsync(normalizedSearch, normalizedPage, pageSize, tenantScopedKey);

            return Ok(usersDto);
        }

        [HttpPost]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(201)]
        [ProducesResponseType(400)]
        public async Task<ActionResult<TUserDto>> Post([FromBody] TUserDto user)
        {
            if (!EqualityComparer<TKey>.Default.Equals(user.Id, default))
            {
                return BadRequest(_errorResources.CannotSetId());
            }

            _logger.LogInformation(
                "Create user request received. UserName={UserName}, TenantKey={TenantKey}, HasPassword={HasPassword}, PasswordLength={PasswordLength}, Actor={Actor}",
                user.UserName,
                user.TenantKey,
                !string.IsNullOrWhiteSpace(user.Password),
                user.Password?.Length ?? 0,
                ResolveActor());

            var (identityResult, userId) = await _identityService.CreateUserAsync(user);
            var createdUser = await _identityService.GetUserAsync(userId.ToString());

            _logger.LogInformation(
                "Create user completed. UserId={UserId}, UserName={UserName}, TenantKey={TenantKey}",
                createdUser.Id,
                createdUser.UserName,
                createdUser.TenantKey);

            return CreatedAtAction(nameof(Get), new { id = createdUser.Id }, createdUser);
        }

        [HttpPut]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Put([FromBody] TUserDto user)
        {
            await _identityService.GetUserAsync(user.Id.ToString());
            await _identityService.UpdateUserAsync(user);

            return NoContent();
        }

        [HttpDelete("{id}")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(204)]
        [ProducesResponseType(403)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Delete(TKey id)
        {
            if (IsDeleteForbidden(id))
                return StatusCode((int)System.Net.HttpStatusCode.Forbidden);

            var user = new TUserDto { Id = id };

            await _identityService.GetUserAsync(user.Id.ToString());
            await _identityService.DeleteUserAsync(user.Id.ToString(), user);

            return NoContent();
        }

        private bool IsDeleteForbidden(TKey id)
        {
            var userId = User.FindFirst(JwtClaimTypes.Subject);

            return userId == null ? false : userId.Value == id.ToString();
        }

        [HttpGet("{id}/Roles")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        public async Task<ActionResult<UserRolesApiDto<TRoleDto>>> GetUserRoles(TKey id, int page = 1, int pageSize = 10)
        {
            var userRoles = await _identityService.GetUserRolesAsync(id.ToString(), page, pageSize);
            var userRolesApiDto = _mapper.Map<UserRolesApiDto<TRoleDto>>(userRoles);

            return Ok(userRolesApiDto);
        }

        [HttpPost("Roles")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> PostUserRoles([FromBody] UserRoleApiDto<TKey> role)
        {
            var userRolesDto = _mapper.Map<TUserRolesDto>(role);
            var tenantAssignmentScope = ResolveTenantAssignmentScope();

            _logger.LogInformation(
                "Assign role request received. UserId={UserId}, RoleId={RoleId}, TenantAssignmentScope={TenantAssignmentScope}, Actor={Actor}",
                userRolesDto.UserId,
                userRolesDto.RoleId,
                tenantAssignmentScope,
                ResolveActor());

            await _identityService.GetUserAsync(userRolesDto.UserId.ToString());
            await _identityService.GetRoleAsync(userRolesDto.RoleId.ToString());

            await _identityService.CreateUserRoleAsync(userRolesDto, tenantAssignmentScope);

            _logger.LogInformation(
                "Assign role completed. UserId={UserId}, RoleId={RoleId}, TenantAssignmentScope={TenantAssignmentScope}",
                userRolesDto.UserId,
                userRolesDto.RoleId,
                tenantAssignmentScope);

            return NoContent();
        }

        [HttpDelete("Roles")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> DeleteUserRoles([FromBody] UserRoleApiDto<TKey> role)
        {
            var userRolesDto = _mapper.Map<TUserRolesDto>(role);

            await _identityService.GetUserAsync(userRolesDto.UserId.ToString());
            await _identityService.GetRoleAsync(userRolesDto.RoleId.ToString());

            await _identityService.DeleteUserRoleAsync(userRolesDto, ResolveTenantAssignmentScope());

            return NoContent();
        }

        [HttpGet("{id}/Claims")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        public async Task<ActionResult<UserClaimsApiDto<TKey>>> GetUserClaims(TKey id, int page = 1, int pageSize = 10)
        {
            var claims = await _identityService.GetUserClaimsAsync(id.ToString(), page, pageSize);

            var userClaimsApiDto = _mapper.Map<UserClaimsApiDto<TKey>>(claims);

            return Ok(userClaimsApiDto);
        }

        [HttpPost("Claims")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> PostUserClaims([FromBody] UserClaimApiDto<TKey> claim)
        {
            var userClaimDto = _mapper.Map<TUserClaimsDto>(claim);

            if (!userClaimDto.ClaimId.Equals(default))
            {
                return BadRequest(_errorResources.CannotSetId());
            }

            await _identityService.CreateUserClaimsAsync(userClaimDto);

            return NoContent();
        }

        [HttpPut("Claims")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> PutUserClaims([FromBody] UserClaimApiDto<TKey> claim)
        {
            var userClaimDto = _mapper.Map<TUserClaimsDto>(claim);

            await _identityService.GetUserClaimAsync(userClaimDto.UserId.ToString(), userClaimDto.ClaimId);
            await _identityService.UpdateUserClaimsAsync(userClaimDto);

            return NoContent();
        }

        [HttpDelete("{id}/Claims")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> DeleteUserClaims([FromRoute] TKey id, int claimId)
        {
            var userClaimsDto = new TUserClaimsDto
            {
                ClaimId = claimId,
                UserId = id
            };

            await _identityService.GetUserClaimAsync(id.ToString(), claimId);
            await _identityService.DeleteUserClaimAsync(userClaimsDto);

            return NoContent();
        }

        [HttpGet("{id}/Providers")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        public async Task<ActionResult<UserProvidersApiDto<TKey>>> GetUserProviders(TKey id)
        {
            var userProvidersDto = await _identityService.GetUserProvidersAsync(id.ToString());
            var userProvidersApiDto = _mapper.Map<UserProvidersApiDto<TKey>>(userProvidersDto);

            return Ok(userProvidersApiDto);
        }

        [HttpDelete("Providers")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> DeleteUserProviders([FromBody] UserProviderDeleteApiDto<TKey> provider)
        {
            var providerDto = _mapper.Map<TUserProviderDto>(provider);

            await _identityService.GetUserProviderAsync(providerDto.UserId.ToString(), providerDto.ProviderKey);
            await _identityService.DeleteUserProvidersAsync(providerDto);

            return NoContent();
        }

        [HttpPost("ChangePassword")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> PostChangePassword([FromBody] UserChangePasswordApiDto<TKey> password)
        {
            var userChangePasswordDto = _mapper.Map<TUserChangePasswordDto>(password);

            await _identityService.UserChangePasswordAsync(userChangePasswordDto);

            return NoContent();
        }

        [HttpGet("{id}/RoleClaims")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        public async Task<ActionResult<RoleClaimsApiDto<TKey>>> GetRoleClaims(TKey id, string claimSearchText, int page = 1, int pageSize = 10)
        {
            var roleClaimsDto = await _identityService.GetUserRoleClaimsAsync(id.ToString(), claimSearchText, page, pageSize);
            var roleClaimsApiDto = _mapper.Map<RoleClaimsApiDto<TKey>>(roleClaimsDto);

            return Ok(roleClaimsApiDto);
        }

        [HttpGet("ClaimType/{claimType}/ClaimValue/{claimValue}")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        public async Task<ActionResult<TUsersDto>> GetClaimUsers(string claimType, string claimValue, int page = 1, int pageSize = 10)
        {
            var usersDto = await _identityService.GetClaimUsersAsync(claimType, claimValue, page, pageSize);

            return Ok(usersDto);
        }

        [HttpGet("ClaimType/{claimType}")]
        [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
        public async Task<ActionResult<TUsersDto>> GetClaimUsers(string claimType, int page = 1, int pageSize = 10)
        {
            var usersDto = await _identityService.GetClaimUsersAsync(claimType, null, page, pageSize);

            return Ok(usersDto);
        }

        private string ResolveTenantScope()
        {
            if (User.HasClaim(c =>
                    ((c.Type == JwtClaimTypes.Role || c.Type == ClaimTypes.Role) && c.Value == _tenantRoleProvider.SuperAdminRole) ||
                    (c.Type == $"client_{JwtClaimTypes.Role}" && c.Value == _tenantRoleProvider.SuperAdminRole)))
            {
                return null;
            }

            var tenantKey = _tenantContextAccessor.Current?.TenantKey ?? User.FindFirst(TenantClaimTypes.TenantKey)?.Value;
            return string.IsNullOrWhiteSpace(tenantKey) ? null : tenantKey.Trim();
        }

        private string ResolveTenantAssignmentScope()
        {
            var tenantKey = _tenantContextAccessor.Current?.TenantKey ?? User.FindFirst(TenantClaimTypes.TenantKey)?.Value;
            return string.IsNullOrWhiteSpace(tenantKey) ? null : tenantKey.Trim();
        }

        private string? ResolveActor()
        {
            return User.FindFirst(JwtClaimTypes.Subject)?.Value ??
                   User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                   User.Identity?.Name;
        }
    }
}
