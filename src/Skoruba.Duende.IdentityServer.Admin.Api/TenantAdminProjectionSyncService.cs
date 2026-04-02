using System.Collections.Generic;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Skoruba.Duende.IdentityServer.Admin.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Dtos.TenantAdminProjection;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using TenantInfrastructure.MasterDb;

namespace Skoruba.Duende.IdentityServer.Admin.Api.Services
{
    public sealed class TenantAdminProjectionSyncService : ITenantAdminProjectionSyncService
    {
        private readonly UserManager<UserIdentity> _userManager;
        private readonly ITenantRepository _tenantRepository;
        private readonly ITenantRoleProvider _tenantRoleProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IOptions<TenantAdminProjectionSyncConfiguration> _configuration;
        private readonly ILogger<TenantAdminProjectionSyncService> _logger;

        public TenantAdminProjectionSyncService(
            UserManager<UserIdentity> userManager,
            ITenantRepository tenantRepository,
            ITenantRoleProvider tenantRoleProvider,
            IHttpClientFactory httpClientFactory,
            IHttpContextAccessor httpContextAccessor,
            IOptions<TenantAdminProjectionSyncConfiguration> configuration,
            ILogger<TenantAdminProjectionSyncService> logger)
        {
            _userManager = userManager;
            _tenantRepository = tenantRepository;
            _tenantRoleProvider = tenantRoleProvider;
            _httpClientFactory = httpClientFactory;
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task UpsertIfTenantAdminAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled())
            {
                _logger.LogDebug(
                    "Tenant admin projection sync skipped for user '{UserId}' because the feature is disabled.",
                    userId);
                return;
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                _logger.LogWarning(
                    "Tenant admin projection sync skipped because user '{UserId}' was not found.",
                    userId);
                return;
            }

            var isTenantAdmin = await IsTenantAdminAsync(user);
            if (!isTenantAdmin)
            {
                _logger.LogDebug(
                    "Tenant admin projection sync skipped for user '{UserId}' because the user does not have the tenant admin role.",
                    userId);
                return;
            }

            if (string.IsNullOrWhiteSpace(user.TenantKey))
            {
                _logger.LogWarning(
                    "Tenant admin projection sync skipped for user '{UserId}' because TenantKey is empty.",
                    userId);
                return;
            }

            var request = BuildRequest(user, TenantAdminProjectionOperation.Upsert);
            await SendAsync(user.TenantKey, request, cancellationToken);
        }

        public async Task<TenantAdminProjectionDeleteSnapshot?> CaptureDeleteSnapshotAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled())
            {
                return null;
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null || !await IsTenantAdminAsync(user) || string.IsNullOrWhiteSpace(user.TenantKey))
            {
                return null;
            }

            return new TenantAdminProjectionDeleteSnapshot
            {
                TenantKey = user.TenantKey.Trim(),
                ExternalIdentityId = user.Id,
                UserName = user.UserName ?? string.Empty,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                BranchCode = user.BranchCode,
                Roles = BuildProjectedRoles()
            };
        }

        public Task DeactivateAsync(TenantAdminProjectionDeleteSnapshot? snapshot, CancellationToken cancellationToken = default)
        {
            return SendFromSnapshotAsync(snapshot, TenantAdminProjectionOperation.Deactivate, cancellationToken);
        }

        public Task DeleteAsync(TenantAdminProjectionDeleteSnapshot? snapshot, CancellationToken cancellationToken = default)
        {
            return SendFromSnapshotAsync(snapshot, TenantAdminProjectionOperation.Delete, cancellationToken);
        }

        private async Task SendFromSnapshotAsync(
            TenantAdminProjectionDeleteSnapshot? snapshot,
            TenantAdminProjectionOperation operation,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled() || snapshot == null || string.IsNullOrWhiteSpace(snapshot.TenantKey))
            {
                return;
            }

            var request = new TenantAdminProjectionSyncRequest
            {
                TenantKey = snapshot.TenantKey,
                ExternalIdentityId = snapshot.ExternalIdentityId,
                UserName = snapshot.UserName,
                Email = snapshot.Email,
                PhoneNumber = snapshot.PhoneNumber,
                BranchCode = snapshot.BranchCode,
                Roles = new List<string>(snapshot.Roles),
                IsActive = false,
                Operation = operation
            };

            await SendAsync(snapshot.TenantKey, request, cancellationToken);
        }

        private async Task SendAsync(
            string tenantKey,
            TenantAdminProjectionSyncRequest payload,
            CancellationToken cancellationToken)
        {
            try
            {
                var tenant = await _tenantRepository.GetByKeyAsync(tenantKey, cancellationToken);
                if (tenant == null || !tenant.IsActive)
                {
                    _logger.LogWarning(
                        "Tenant admin projection sync skipped because tenant '{TenantKey}' was not found or inactive.",
                        tenantKey);
                    return;
                }

                if (!TryBuildEndpointUri(out var endpointUri))
                {
                    _logger.LogWarning(
                        "Tenant admin projection sync skipped for tenant '{TenantKey}' because TenantAdminProjectionSync:BaseUrl is missing or invalid.",
                        tenantKey);
                    return;
                }

                var client = _httpClientFactory.CreateClient("TenantAdminProjectionSync");
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(GetTimeout());

                using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri)
                {
                    Content = JsonContent.Create(payload)
                };

                AttachTenantContext(request, tenantKey);
                var hasBearerToken = TryAttachBearerToken(request, tenantKey);

                var options = _configuration.Value;
                if (!string.IsNullOrWhiteSpace(options.ApiKeyHeaderName) &&
                    !string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    request.Headers.TryAddWithoutValidation(options.ApiKeyHeaderName.Trim(), options.ApiKey.Trim());
                }

                _logger.LogInformation(
                    "Sending tenant admin projection sync. TenantKey={TenantKey}, UserId={UserId}, Operation={Operation}, Endpoint={Endpoint}, HasBearerToken={HasBearerToken}, HasApiKey={HasApiKey}, Roles={Roles}",
                    tenantKey,
                    payload.ExternalIdentityId,
                    payload.Operation,
                    endpointUri,
                    hasBearerToken,
                    !string.IsNullOrWhiteSpace(options.ApiKeyHeaderName) && !string.IsNullOrWhiteSpace(options.ApiKey),
                    string.Join(",", payload.Roles));

                using var response = await client.SendAsync(request, timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Tenant admin projection sync succeeded for tenant '{TenantKey}' and user '{UserId}' with status code {StatusCode}.",
                        tenantKey,
                        payload.ExternalIdentityId,
                        response.StatusCode);
                    return;
                }

                var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                _logger.LogWarning(
                    "Tenant admin projection sync failed for tenant '{TenantKey}' and user '{UserId}' with status code {StatusCode}. Response={Response}",
                    tenantKey,
                    payload.ExternalIdentityId,
                    response.StatusCode,
                    responseBody);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Tenant admin projection sync failed unexpectedly for tenant '{TenantKey}' and user '{UserId}'.",
                    tenantKey,
                    payload.ExternalIdentityId);
            }
        }

        private bool IsEnabled()
        {
            return _configuration.Value.Enabled;
        }

        private TimeSpan GetTimeout()
        {
            return TimeSpan.FromSeconds(Math.Max(1, _configuration.Value.TimeoutSeconds));
        }

        private async Task<bool> IsTenantAdminAsync(UserIdentity user)
        {
            return await _userManager.IsInRoleAsync(user, _tenantRoleProvider.TenantAdminRole);
        }

        private TenantAdminProjectionSyncRequest BuildRequest(
            UserIdentity user,
            TenantAdminProjectionOperation operation)
        {
            var isActive = user.LockoutEnabled
                ? user.LockoutEnd == null || user.LockoutEnd <= DateTimeOffset.UtcNow
                : true;

            return new TenantAdminProjectionSyncRequest
            {
                TenantKey = user.TenantKey.Trim(),
                ExternalIdentityId = user.Id,
                UserName = user.UserName ?? string.Empty,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                BranchCode = user.BranchCode,
                Roles = BuildProjectedRoles(),
                IsActive = isActive,
                Operation = operation
            };
        }

        private List<string> BuildProjectedRoles()
        {
            if (string.IsNullOrWhiteSpace(_tenantRoleProvider.TenantAdminRole))
            {
                return new List<string>();
            }

            return new List<string>
            {
                _tenantRoleProvider.TenantAdminRole.Trim()
            };
        }

        private void AttachTenantContext(HttpRequestMessage request, string tenantKey)
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantKey);
            request.Headers.TryAddWithoutValidation("X-TenantId", tenantKey);
            request.Headers.TryAddWithoutValidation(
                HeaderNames.Cookie,
                $"tenant={Uri.EscapeDataString(tenantKey)}; tenant_id={Uri.EscapeDataString(tenantKey)}");
        }

        private bool TryAttachBearerToken(HttpRequestMessage request, string tenantKey)
        {
            var authorization = _httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();
            if (string.IsNullOrWhiteSpace(authorization))
            {
                _logger.LogDebug(
                    "Tenant admin projection sync for tenant '{TenantKey}' is proceeding without forwarding a bearer token because the current request has no Authorization header.",
                    tenantKey);
                return false;
            }

            request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, authorization);
            return true;
        }

        private bool TryBuildEndpointUri(out Uri? endpointUri)
        {
            endpointUri = null;

            var baseUrl = _configuration.Value.BaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl) ||
                !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                return false;
            }

            var relativePath = _configuration.Value.RelativePath?.Trim();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = "/api/internal/tenant-admins/projection";
            }

            endpointUri = new Uri(baseUri, relativePath.TrimStart('/'));
            return true;
        }
    }
}
