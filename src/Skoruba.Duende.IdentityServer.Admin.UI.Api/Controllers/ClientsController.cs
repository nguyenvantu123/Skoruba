// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Dtos.Common;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.Constants;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Clients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.ExceptionHandling;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Mappers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Resources;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [TypeFilter(typeof(ControllerExceptionFilterAttribute))]
    [Produces("application/json", "application/problem+json")]
    [Authorize(Policy = AuthorizationConsts.AdministrationPolicy)]
    public class ClientsController : ControllerBase
    {
        private readonly IClientService _clientService;
        private readonly IClientScopeCacheService _clientScopeCacheService;
        private readonly IApiErrorResources _errorResources;
        private readonly ITenantClientCacheService _tenantClientCache;
        private readonly IClientTenantScopeResolver _scopeResolver;

        /// <summary>
        /// Constructs a <see cref="ClientsController"/>. The
        /// <paramref name="tenantClientCache"/> and <paramref name="scopeResolver"/>
        /// dependencies wire the per-tenant snapshot cache (feature
        /// <c>tenant-client-cache-expansion</c>) alongside the legacy
        /// <see cref="IClientScopeCacheService"/>; both caches are written
        /// in a single CRUD request, with the legacy call kept first to
        /// preserve backward compatibility (see design.md, "Coexistence").
        /// </summary>
        public ClientsController(
            IClientService clientService,
            IClientScopeCacheService clientScopeCacheService,
            IApiErrorResources errorResources,
            ITenantClientCacheService tenantClientCache,
            IClientTenantScopeResolver scopeResolver)
        {
            _clientService = clientService;
            _clientScopeCacheService = clientScopeCacheService;
            _errorResources = errorResources;
            _tenantClientCache = tenantClientCache;
            _scopeResolver = scopeResolver;
        }

        [HttpGet]
        public async Task<ActionResult<ClientsApiDto>> Get(string searchText, int page = 1, int pageSize = 10)
        {
            var clientsDto = await _clientService.GetClientsAsync(searchText, page, pageSize);
            var clientsApiDto = clientsDto.ToClientApiModel<ClientsApiDto>();

            return Ok(clientsApiDto);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<ClientApiDto>> Get(int id)
        {
            var clientDto = await _clientService.GetClientAsync(id);
            var clientApiDto = clientDto.ToClientApiModel<ClientApiDto>();

            return Ok(clientApiDto);
        }

        [HttpGet(nameof(GetAccessTokenTypes))]
        public ActionResult<List<SelectItemDto>> GetAccessTokenTypes()
        {
            var accessTokenTypes = _clientService.GetAccessTokenTypes();

            return accessTokenTypes;
        }

        [HttpGet(nameof(GetTokenExpirations))]
        public ActionResult<List<SelectItemDto>> GetTokenExpirations()
        {
            var tokenExpirations = _clientService.GetTokenExpirations();

            return tokenExpirations;
        }

        [HttpGet(nameof(GetTokenUsage))]
        public ActionResult<List<SelectItemDto>> GetTokenUsage()
        {
            var tokenUsage = _clientService.GetTokenUsage();

            return tokenUsage;
        }

        [HttpGet(nameof(GetProtocolTypes))]
        public ActionResult<List<SelectItemDto>> GetProtocolTypes()
        {
            var protocolTypes = _clientService.GetProtocolTypes();

            return protocolTypes;
        }

        [HttpGet(nameof(GetDPoPValidationModes))]
        public ActionResult<List<SelectItemDto>> GetDPoPValidationModes()
        {
            var dPoPValidationModes = _clientService.GetDPoPValidationModes();

            return dPoPValidationModes;
        }

        [HttpGet(nameof(GetScopes))]
        public async Task<ActionResult<List<string>>> GetScopes(string scope, int limit = 0, bool excludeIdentityResources = false, bool excludeApiScopes = false)
        {
            var scopes = await _clientService.GetScopesAsync(scope, limit, excludeIdentityResources, excludeApiScopes);

            return scopes;
        }

        [HttpGet(nameof(GetGrantTypes))]
        public ActionResult<List<SelectItemDto>> GetGrantTypes(string grant, bool includeObsoleteGrants, int limit = 0)
        {
            var grants = _clientService.GetGrantTypes(grant, includeObsoleteGrants, limit);

            return grants;
        }

        [HttpGet(nameof(GetHashTypes))]
        public ActionResult<List<SelectItemDto>> GetHashTypes()
        {
            var hashTypes = _clientService.GetHashTypes();

            return hashTypes;
        }

        [HttpGet(nameof(GetSecretTypes))]
        public ActionResult<List<SelectItemDto>> GetSecretTypes()
        {
            var secretTypes = _clientService.GetSecretTypes();

            return secretTypes;
        }

        [HttpGet(nameof(GetStandardClaims))]
        public ActionResult<List<string>> GetStandardClaims(string claim, int limit = 0)
        {
            var standardClaims = _clientService.GetStandardClaims(claim, limit);

            return standardClaims;
        }

        [HttpGet(nameof(GetSigningAlgorithms))]
        public ActionResult<List<string>> GetSigningAlgorithms(string algorithm, int limit = 0)
        {
            var signingAlgorithms = _clientService.GetSigningAlgorithms(algorithm, limit);

            return signingAlgorithms;
        }

        [HttpGet(nameof(CanInsertClient))]
        public async Task<ActionResult<bool>> CanInsertClient(int id, string clientId, bool isCloned)
        {
            var clientExists = await _clientService.CanInsertClientAsync(new ClientDto()
            {
                Id = id,
                ClientId = clientId,
            }, isCloned);

            return clientExists;
        }

        [HttpPost]
        [ProducesResponseType(typeof(ClientApiDto), 201)]
        [ProducesResponseType(400)]
        public async Task<ActionResult<ClientApiDto>> Post([FromBody] ClientApiDto client)
        {
            var clientDto = client.ToClientApiModel<ClientDto>();

            if (!clientDto.Id.Equals(default))
            {
                return BadRequest(_errorResources.CannotSetId());
            }

            var ct = HttpContext.RequestAborted;

            // 1. Source-of-truth mutation (BusinessLogic + EF).
            var id = await _clientService.AddClientAsync(clientDto);
            // 2. Legacy scope cache (R12 backward compat — call order verbatim).
            await _clientScopeCacheService.SaveAllowedScopesAsync(clientDto.ClientId, clientDto.AllowedScopes, ct);
            client.Id = id;

            // 3-5. New tenant-scoped cache. Re-read for fresh TenantRedirectPairs
            //      (design.md "Detail Add path") and write one snapshot per tenant.
            //      Empty tenantKeys ⇒ skip write but still return 201 (R4.2).
            var fresh = await _clientService.GetClientAsync(id);
            var tenantKeys = await _scopeResolver.ResolveTenantKeysAsync(fresh, ct);
            await _tenantClientCache.WriteSnapshotsAsync(tenantKeys, fresh, ct);

            return CreatedAtAction(nameof(Get), new { id }, client);
        }

        [HttpPut]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Put([FromBody] ClientApiDto client)
        {
            var clientDto = client.ToClientApiModel<ClientDto>();
            var ct = HttpContext.RequestAborted;

            // R5.7 / drift detection: capture the pre-update view before
            // mutating the row so we can compare tenant scope and clientId.
            var preUpdate = await _clientService.GetClientAsync(clientDto.Id);
            var preTenantKeys = await _scopeResolver.ResolveTenantKeysAsync(preUpdate, ct);
            var preClientId = preUpdate.ClientId;

            // 1. Source-of-truth mutation.
            await _clientService.UpdateClientAsync(clientDto, updateClientClaims: true, updateClientProperties: true);
            // 2. Legacy scope cache (R12 backward compat — call order verbatim).
            await _clientScopeCacheService.SaveAllowedScopesAsync(clientDto.ClientId, clientDto.AllowedScopes, ct);

            // 3-4. Re-read for the post-update tenant scope (source of truth).
            var fresh = await _clientService.GetClientAsync(clientDto.Id);
            var tenantKeys = await _scopeResolver.ResolveTenantKeysAsync(fresh, ct);

            var rename = !string.Equals(preClientId, fresh.ClientId, StringComparison.Ordinal);
            if (rename)
            {
                // R5.7: clientId rename — the old (tenantKey, oldClientId) entries
                // can never be reached by the new key. Invalidate both the
                // pre- AND post-update tenant sets under the OLD clientId,
                // then write fresh snapshots under the NEW clientId.
                var invalidateTenantKeys = preTenantKeys
                    .Union(tenantKeys, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                await _tenantClientCache.InvalidateSnapshotsAsync(invalidateTenantKeys, preClientId, ct);
                await _tenantClientCache.WriteSnapshotsAsync(tenantKeys, fresh, ct);
            }
            else
            {
                // R5.2: drift = tenants present pre-update that are no longer
                // in the post-update set. Invalidate just those, then write
                // fresh snapshots for the remaining (post-update) tenants.
                var drift = preTenantKeys
                    .Except(tenantKeys, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (drift.Count > 0)
                {
                    await _tenantClientCache.InvalidateSnapshotsAsync(drift, fresh.ClientId, ct);
                }
                await _tenantClientCache.WriteSnapshotsAsync(tenantKeys, fresh, ct);
            }

            return NoContent();
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Delete(int id)
        {
            var ct = HttpContext.RequestAborted;

            // Capture pre-delete view BEFORE the mutation so we know which
            // tenants the snapshot was written under (R6.1, R6.2).
            var clientDto = await _clientService.GetClientAsync(id);
            var preTenantKeys = await _scopeResolver.ResolveTenantKeysAsync(clientDto, ct);
            var clientId = clientDto.ClientId;

            // 1. Source-of-truth mutation.
            await _clientService.RemoveClientAsync(clientDto);
            // 2. Legacy scope cache (R12 backward compat — call order verbatim).
            await _clientScopeCacheService.RemoveAllowedScopesAsync(clientId, ct);
            // 3. Invalidate tenant-scoped snapshots.
            await _tenantClientCache.InvalidateSnapshotsAsync(preTenantKeys, clientId, ct);

            return NoContent();
        }

        [HttpPost("Clone")]
        [ProducesResponseType(typeof(ClientApiDto), 201)]
        [ProducesResponseType(400)]
        public async Task<ActionResult<ClientApiDto>> PostClientClone([FromBody] ClientCloneApiDto client)
        {
            var clientCloneDto = client.ToClientApiModel<ClientCloneDto>();
            var ct = HttpContext.RequestAborted;

            var originalClient = await _clientService.GetClientAsync(clientCloneDto.Id);
            var id = await _clientService.CloneClientAsync(clientCloneDto);
            originalClient.Id = id;

            var clonedClient = originalClient.ToClientApiModel<ClientApiDto>();

            // R7.1: write snapshots ONLY for the cloned client. R7.2: do NOT
            // touch source-client cache entries — the source row is unchanged.
            var freshClone = await _clientService.GetClientAsync(id);
            var tenantKeys = await _scopeResolver.ResolveTenantKeysAsync(freshClone, ct);
            await _tenantClientCache.WriteSnapshotsAsync(tenantKeys, freshClone, ct);

            return CreatedAtAction(nameof(Get), new { id }, clonedClient);
        }

        [HttpGet("{id}/Secrets")]
        public async Task<ActionResult<ClientSecretsApiDto>> GetSecrets(int id, int page = 1, int pageSize = 10)
        {
            var clientSecretsDto = await _clientService.GetClientSecretsAsync(id, page, pageSize);
            var clientSecretsApiDto = clientSecretsDto.ToClientApiModel<ClientSecretsApiDto>();

            return Ok(clientSecretsApiDto);
        }

        [HttpGet("Secrets/{secretId}")]
        public async Task<ActionResult<ClientSecretApiDto>> GetSecret(int secretId)
        {
            var clientSecretsDto = await _clientService.GetClientSecretAsync(secretId);
            var clientSecretDto = clientSecretsDto.ToClientApiModel<ClientSecretApiDto>();

            return Ok(clientSecretDto);
        }

        [HttpPost("{id}/Secrets")]
        [ProducesResponseType(typeof(ClientSecretApiDto), 201)]
        [ProducesResponseType(400)]
        public async Task<ActionResult<ClientSecretApiDto>> PostSecret(int id, [FromBody] ClientSecretApiDto clientSecretApi)
        {
            var secretsDto = clientSecretApi.ToClientApiModel<ClientSecretsDto>();
            secretsDto.ClientId = id;

            if (!secretsDto.ClientSecretId.Equals(default))
            {
                return BadRequest(_errorResources.CannotSetId());
            }

            var secretId = await _clientService.AddClientSecretAsync(secretsDto);
            clientSecretApi.Id = secretId;

            return CreatedAtAction(nameof(GetSecret), new { secretId }, clientSecretApi);
        }

        [HttpDelete("Secrets/{secretId}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> DeleteSecret(int secretId)
        {
            var clientSecret = new ClientSecretsDto { ClientSecretId = secretId };

            await _clientService.GetClientSecretAsync(clientSecret.ClientSecretId);
            await _clientService.DeleteClientSecretAsync(clientSecret);

            return NoContent();
        }

        [HttpGet("{id}/Properties")]
        public async Task<ActionResult<ClientPropertiesApiDto>> GetProperties(int id, int page = 1, int pageSize = 10)
        {
            var clientPropertiesDto = await _clientService.GetClientPropertiesAsync(id, page, pageSize);
            var clientPropertiesApiDto = clientPropertiesDto.ToClientApiModel<ClientPropertiesApiDto>();

            return Ok(clientPropertiesApiDto);
        }

        [HttpGet("Properties/{propertyId}")]
        public async Task<ActionResult<ClientPropertyApiDto>> GetProperty(int propertyId)
        {
            var clientPropertiesDto = await _clientService.GetClientPropertyAsync(propertyId);
            var clientPropertyApiDto = clientPropertiesDto.ToClientApiModel<ClientPropertyApiDto>();

            return Ok(clientPropertyApiDto);
        }

        [HttpPost("{id}/Properties")]
        [ProducesResponseType(typeof(ClientPropertyApiDto), 201)]
        [ProducesResponseType(400)]
        public async Task<ActionResult<ClientPropertyApiDto>> PostProperty(int id, [FromBody] ClientPropertyApiDto clientPropertyApi)
        {
            var clientPropertiesDto = clientPropertyApi.ToClientApiModel<ClientPropertiesDto>();
            clientPropertiesDto.ClientId = id;

            if (!clientPropertiesDto.ClientPropertyId.Equals(default))
            {
                return BadRequest(_errorResources.CannotSetId());
            }

            var propertyId = await _clientService.AddClientPropertyAsync(clientPropertiesDto);
            clientPropertyApi.Id = propertyId;

            return CreatedAtAction(nameof(GetProperty), new { propertyId }, clientPropertyApi);
        }

        [HttpDelete("Properties/{propertyId}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> DeleteProperty(int propertyId)
        {
            var clientProperty = new ClientPropertiesDto { ClientPropertyId = propertyId };

            await _clientService.GetClientPropertyAsync(clientProperty.ClientPropertyId);
            await _clientService.DeleteClientPropertyAsync(clientProperty);

            return NoContent();
        }

        [HttpGet("{id}/Claims")]
        public async Task<ActionResult<ClientClaimsApiDto>> GetClaims(int id, int page = 1, int pageSize = 10)
        {
            var clientClaimsDto = await _clientService.GetClientClaimsAsync(id, page, pageSize);
            var clientClaimsApiDto = clientClaimsDto.ToClientApiModel<ClientClaimsApiDto>();

            return Ok(clientClaimsApiDto);
        }

        [HttpGet("Claims/{claimId}")]
        public async Task<ActionResult<ClientClaimApiDto>> GetClaim(int claimId)
        {
            var clientClaimsDto = await _clientService.GetClientClaimAsync(claimId);
            var clientClaimApiDto = clientClaimsDto.ToClientApiModel<ClientClaimApiDto>();

            return Ok(clientClaimApiDto);
        }

        [HttpPost("{id}/Claims")]
        [ProducesResponseType(typeof(ClientClaimApiDto), 201)]
        [ProducesResponseType(400)]
        public async Task<ActionResult<ClientClaimApiDto>> PostClaim(int id, [FromBody] ClientClaimApiDto clientClaimApiDto)
        {
            var clientClaimsDto = clientClaimApiDto.ToClientApiModel<ClientClaimsDto>();
            clientClaimsDto.ClientId = id;

            if (!clientClaimsDto.ClientClaimId.Equals(default))
            {
                return BadRequest(_errorResources.CannotSetId());
            }

            var claimId = await _clientService.AddClientClaimAsync(clientClaimsDto);
            clientClaimApiDto.Id = claimId;

            return CreatedAtAction(nameof(GetClaim), new { claimId }, clientClaimApiDto);
        }

        [HttpDelete("Claims/{claimId}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> DeleteClaim(int claimId)
        {
            var clientClaimsDto = new ClientClaimsDto { ClientClaimId = claimId };

            await _clientService.GetClientClaimAsync(claimId);
            await _clientService.DeleteClientClaimAsync(clientClaimsDto);

            return NoContent();
        }
    }
}
