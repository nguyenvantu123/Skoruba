using Microsoft.AspNetCore.Identity;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Mappers;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.MasterDb;
using static TenantInfrastructure.MasterDb.DistributedTenantRegistryCache;

namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services
{
    public class TenantService : ITenantService
    {
        private const string DefaultSecretServiceKey = "BlazorApiUser";
        private readonly ITenantRepository _tenantRepository;
        private readonly UserManager<UserIdentity> _userManager;
        private readonly RoleManager<UserIdentityRole> _roleManager;
        private readonly ITenantRoleProvider _roleProvider;
        private readonly ITenantRegistryCache _tenantRegistryCache;
        private readonly ITenantAdminProjectionSyncService _tenantAdminProjectionSyncService;

        public TenantService(
            ITenantRepository tenantRepository,
            UserManager<UserIdentity> userManager,
            RoleManager<UserIdentityRole> roleManager,
            ITenantRoleProvider roleProvider,
            ITenantRegistryCache tenantRegistryCache,
            ITenantAdminProjectionSyncService tenantAdminProjectionSyncService)
        {
            _tenantRepository = tenantRepository;
            _userManager = userManager;
            _roleManager = roleManager;
            _roleProvider = roleProvider;
            _tenantRegistryCache = tenantRegistryCache;
            _tenantAdminProjectionSyncService = tenantAdminProjectionSyncService;
        }

        public async Task<List<TenantDto>> GetTenantsAsync(string? search, CancellationToken ct)
        {
            var tenants = await _tenantRepository.GetTenantsAsync(search, ct);
            return tenants.ToModel();
        }

        public async Task<List<PublicTenantDto>> GetPublicTenantsAsync(CancellationToken ct)
        {
            var cachedTenantNames = _tenantRegistryCache.TryGetPublicTenantNames();
            if (cachedTenantNames != null)
            {
                return ToPublicTenantDtos(cachedTenantNames);
            }

            var tenants = await _tenantRepository.GetTenantsAsync(null, ct);
            var publicTenantNames = BuildPublicTenantNames(tenants);

            var publicTenantCacheData = publicTenantNames.Select(x => new PublicTenantCacheData
            {
                DisplayName = x.DisplayName,
                LogoUrl = x.LogoUrl
            }).ToList();
            _tenantRegistryCache.SetPublicTenantNames(publicTenantCacheData);
            return ToPublicTenantDtos(publicTenantCacheData);
        }

        public async Task<TenantDto?> GetTenantAsync(int id, CancellationToken ct)
        {
            var tenant = await _tenantRepository.GetByIdAsync(id, ct);
            return tenant?.ToModel();
        }

        public async Task<TenantRegistryLookupResultDto?> GetTenantRegistryLookupAsync(string identifier, string? serviceName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return null;
            }

            var normalizedIdentifier = identifier.Trim();
            var normalizedServiceName = string.IsNullOrWhiteSpace(serviceName) ? null : serviceName.Trim();
            var tenants = await _tenantRepository.GetTenantsAsync(normalizedIdentifier, ct);
            var tenant = tenants.FirstOrDefault(x =>
                string.Equals(x.TenantKey, normalizedIdentifier, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.DisplayName, normalizedIdentifier, StringComparison.OrdinalIgnoreCase));

            if (tenant == null)
            {
                return null;
            }

            return new TenantRegistryLookupResultDto
            {
                TenantId = tenant.Id.ToString(),
                Identifier = tenant.TenantKey,
                Name = tenant.DisplayName,
                SecretName = ResolveSecretName(tenant.ConnectionSecrets, normalizedServiceName),
                ConnectionSecrets = new Dictionary<string, string>(tenant.ConnectionSecrets, StringComparer.OrdinalIgnoreCase),
                IsActive = tenant.IsActive
            };
        }

        public async Task<TenantDto> CreateTenantAsync(TenantCreateDto model, CancellationToken ct)
        {
            var existing = await _tenantRepository.GetByKeyAsync(model.TenantKey, ct);
            if (existing != null)
                throw new InvalidOperationException($"Tenant key '{model.TenantKey}' already exists.");

            ValidateOptionalUrl(model.RedirectUrl, "RedirectUrl");
            ValidateOptionalUrl(model.LogoUrl, "LogoUrl");
            ValidateConnectionSecrets(model.ConnectionSecrets);

            var entity = model.ToEntity();
            var created = await _tenantRepository.AddAsync(entity, ct);
            _tenantRegistryCache.SetTenant(created);
            await RefreshPublicTenantNamesCacheAsync(ct);
            return created.ToModel();
        }

        public async Task<TenantDto> CloneTenantAsync(TenantCloneDto model, CancellationToken ct)
        {
            var existing = await _tenantRepository.GetByKeyAsync(model.TenantKey, ct);
            if (existing != null)
                throw new InvalidOperationException($"Tenant key '{model.TenantKey}' already exists.");

            var sourceTenant = await _tenantRepository.GetByIdAsync(model.SourceTenantId, ct)
                ?? throw new InvalidOperationException("Source tenant not found.");

            var connectionSecrets = model.ConnectionSecrets is { Count: > 0 }
                ? new Dictionary<string, string>(model.ConnectionSecrets, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(sourceTenant.ConnectionSecrets, StringComparer.OrdinalIgnoreCase);

            var redirectUrl = string.IsNullOrWhiteSpace(model.RedirectUrl)
                ? sourceTenant.RedirectUrl
                : model.RedirectUrl;

            var logoUrl = string.IsNullOrWhiteSpace(model.LogoUrl)
                ? sourceTenant.LogoUrl
                : model.LogoUrl;

            var isActive = model.IsActive ?? sourceTenant.IsActive;

            ValidateOptionalUrl(redirectUrl, "RedirectUrl");
            ValidateOptionalUrl(logoUrl, "LogoUrl");
            ValidateConnectionSecrets(connectionSecrets);

            var entity = new TenantInfo
            {
                TenantKey = model.TenantKey,
                DisplayName = model.DisplayName,
                ConnectionSecrets = connectionSecrets,
                RedirectUrl = redirectUrl,
                LogoUrl = logoUrl,
                IsActive = isActive
            };

            var created = await _tenantRepository.AddAsync(entity, ct);
            _tenantRegistryCache.SetTenant(created);
            await RefreshPublicTenantNamesCacheAsync(ct);
            return created.ToModel();
        }

        public async Task<TenantDto> UpdateTenantAsync(int id, TenantUpdateDto model, CancellationToken ct)
        {
            var tenant = await _tenantRepository.GetByIdAsync(id, ct)
                ?? throw new InvalidOperationException("Tenant not found.");

            ValidateOptionalUrl(model.RedirectUrl, "RedirectUrl");
            ValidateOptionalUrl(model.LogoUrl, "LogoUrl");
            ValidateConnectionSecrets(model.ConnectionSecrets);

            model.MapToEntity(tenant);
            await _tenantRepository.UpdateAsync(tenant, ct);
            _tenantRegistryCache.SetTenant(tenant);
            await RefreshPublicTenantNamesCacheAsync(ct);
            return tenant.ToModel();
        }

        public async Task<List<TenantAdminDto>> GetTenantAdminsAsync(int tenantId, CancellationToken ct)
        {
            var tenant = await _tenantRepository.GetByIdAsync(tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");

            var tenantAdmins = await _userManager.GetUsersInRoleAsync(_roleProvider.TenantAdminRole);

            return tenantAdmins
                .Where(x => string.Equals(x.TenantKey, tenant.TenantKey, StringComparison.OrdinalIgnoreCase))
                .Select(user => new TenantAdminDto
                {
                    UserId = user.Id,
                    UserName = user.UserName ?? string.Empty,
                    Email = user.Email
                })
                .ToList();
        }

        public async Task AssignTenantAdminAsync(int tenantId, string userId, CancellationToken ct)
        {
            var tenant = await _tenantRepository.GetByIdAsync(tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");

            var user = await _userManager.FindByIdAsync(userId)
                ?? throw new InvalidOperationException("User not found.");

            await EnsureRoleExistsAsync(_roleProvider.TenantAdminRole, ct);

            user.TenantKey = tenant.TenantKey;
            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
                throw new InvalidOperationException("Failed to update user tenant key.");

            var roleResult = await _userManager.AddToRoleAsync(user, _roleProvider.TenantAdminRole);
            if (!roleResult.Succeeded)
                throw new InvalidOperationException("Failed to assign tenant admin role.");

            await _tenantAdminProjectionSyncService.UpsertIfTenantAdminAsync(user.Id, ct);
        }

        public async Task UnassignTenantAdminAsync(int tenantId, string userId, CancellationToken ct)
        {
            var tenant = await _tenantRepository.GetByIdAsync(tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");

            var user = await _userManager.FindByIdAsync(userId)
                ?? throw new InvalidOperationException("User not found.");

            if (!string.Equals(user.TenantKey, tenant.TenantKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("User does not belong to this tenant.");

            var deleteSnapshot = await _tenantAdminProjectionSyncService.CaptureDeleteSnapshotAsync(user.Id, ct);

            var roleResult = await _userManager.RemoveFromRoleAsync(user, _roleProvider.TenantAdminRole);
            if (!roleResult.Succeeded)
                throw new InvalidOperationException("Failed to remove tenant admin role.");

            user.TenantKey = string.Empty;
            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
                throw new InvalidOperationException("Failed to clear user tenant key.");

            await _tenantAdminProjectionSyncService.DeactivateAsync(deleteSnapshot, ct);
        }

        private async Task EnsureRoleExistsAsync(string roleName, CancellationToken ct)
        {
            if (await _roleManager.RoleExistsAsync(roleName)) return;

            var result = await _roleManager.CreateAsync(new UserIdentityRole
            {
                Name = roleName
            });

            if (!result.Succeeded)
                throw new InvalidOperationException($"Failed to create role '{roleName}'.");
        }

        private static string ResolveSecretName(IReadOnlyDictionary<string, string>? connectionSecrets, string? serviceName)
        {
            if (connectionSecrets == null || connectionSecrets.Count == 0)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                return connectionSecrets.TryGetValue(serviceName, out var requestedSecretName) &&
                       !string.IsNullOrWhiteSpace(requestedSecretName)
                    ? requestedSecretName
                    : string.Empty;
            }

            if (connectionSecrets.TryGetValue(DefaultSecretServiceKey, out var defaultSecretName) &&
                !string.IsNullOrWhiteSpace(defaultSecretName))
            {
                return defaultSecretName;
            }

            return connectionSecrets.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static void ValidateOptionalUrl(string? value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"{fieldName} must be an absolute http/https URL.");
            }
        }

        private static void ValidateConnectionSecrets(IReadOnlyDictionary<string, string>? connectionSecrets)
        {
            if (connectionSecrets is null || connectionSecrets.Count == 0)
            {
                throw new InvalidOperationException("ConnectionSecrets must contain at least one service mapping.");
            }

            foreach (var pair in connectionSecrets)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new InvalidOperationException("ConnectionSecrets contains an empty service key.");
                }

                if (string.IsNullOrWhiteSpace(pair.Value))
                {
                    throw new InvalidOperationException($"ConnectionSecrets contains an empty secret name for service '{pair.Key}'.");
                }
            }
        }

        private async Task RefreshPublicTenantNamesCacheAsync(CancellationToken ct)
        {
            var tenants = await _tenantRepository.GetTenantsAsync(null, ct);
            _tenantRegistryCache.SetPublicTenantNames(BuildPublicTenantNames(tenants));
        }

        private static IReadOnlyList<PublicTenantCacheData> BuildPublicTenantNames(IEnumerable<TenantInfo> tenants)
        {
            return tenants
                .Select(tenant => new PublicTenantCacheData
                {
                    DisplayName = tenant.DisplayName,
                    LogoUrl = tenant.LogoUrl
                })
                .ToList();
        }

        private static List<PublicTenantDto> ToPublicTenantDtos(IEnumerable<PublicTenantCacheData> tenantNames)
        {
            return tenantNames
                .Select(tenants => new PublicTenantDto
                {
                    DisplayName = tenants.DisplayName,
                    LogoUrl = tenants.LogoUrl
                })
                .ToList();
        }
    }
}





