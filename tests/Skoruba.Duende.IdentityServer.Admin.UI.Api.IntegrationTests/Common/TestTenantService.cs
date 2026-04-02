using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common
{
    public class TestTenantService : ITenantService
    {
        private static readonly List<PublicTenantDto> PublicTenants =
        [
            new PublicTenantDto
            {
                DisplayName = "Branch A",
                LogoUrl = "https://cdn.example.com/branch-a.png"
            },
            new PublicTenantDto
            {
                DisplayName = "Branch B",
                LogoUrl = "https://cdn.example.com/branch-b.png"
            }
        ];

        public Task<List<TenantDto>> GetTenantsAsync(string? search, CancellationToken ct)
        {
            return Task.FromResult(new List<TenantDto>
            {
                new TenantDto
                {
                    Id = 1,
                    TenantKey = "branch-a",
                    DisplayName = "Branch A",
                    IsActive = true,
                    ConnectionSecrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["BlazorApiUser"] = "tenant-branch-a-secret",
                        ["BlazorWebApiFiles"] = "tenant-branch-a-file-secret"
                    },
                    RedirectUrl = "https://branch-a.example.com",
                    LogoUrl = "https://cdn.example.com/branch-a.png",
                    CreatedUtc = DateTime.UtcNow
                }
            });
        }

        public Task<List<PublicTenantDto>> GetPublicTenantsAsync(CancellationToken ct)
        {
            return Task.FromResult(PublicTenants.ToList());
        }

        public Task<TenantDto?> GetTenantAsync(int id, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<TenantRegistryLookupResultDto?> GetTenantRegistryLookupAsync(string identifier, string? serviceName, CancellationToken ct)
        {
            if (!string.Equals(identifier, "branch-a", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<TenantRegistryLookupResultDto?>(null);
            }

            var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BlazorApiUser"] = "tenant-branch-a-secret",
                ["BlazorWebApiFiles"] = "tenant-branch-a-file-secret"
            };

            var secretName = string.IsNullOrWhiteSpace(serviceName)
                ? secrets["BlazorApiUser"]
                : secrets.TryGetValue(serviceName, out var requestedSecret)
                    ? requestedSecret
                    : string.Empty;

            return Task.FromResult<TenantRegistryLookupResultDto?>(new TenantRegistryLookupResultDto
            {
                TenantId = "1",
                Identifier = "branch-a",
                Name = "Branch A",
                SecretName = secretName,
                ConnectionSecrets = secrets,
                IsActive = true
            });
        }

        public Task<TenantDto> CreateTenantAsync(TenantCreateDto model, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<TenantDto> CloneTenantAsync(TenantCloneDto model, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<TenantDto> UpdateTenantAsync(int id, TenantUpdateDto model, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<List<TenantAdminDto>> GetTenantAdminsAsync(int tenantId, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task AssignTenantAdminAsync(int tenantId, string userId, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task UnassignTenantAdminAsync(int tenantId, string userId, CancellationToken ct)
        {
            throw new NotSupportedException();
        }
    }
}
