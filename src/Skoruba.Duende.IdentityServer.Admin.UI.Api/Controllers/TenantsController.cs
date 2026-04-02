using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.Constants;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Tenants;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Mappers;
using System.Text;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers
{
    [Authorize(Policy = AuthorizationConsts.SuperAdminPolicy)]
    [ApiController]
    [Route("api/tenants")]
    public class TenantsController : ControllerBase
    {
        private const string DefaultLegacyServiceKey = "BlazorApiUser";
        private const long MaxLogoFileSizeBytes = 2 * 1024 * 1024;
        private static readonly HashSet<string> AllowedLogoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".webp"
        };

        private readonly ITenantService _tenantService;
        private readonly PublicTenantDirectoryConfiguration _publicTenantDirectoryConfiguration;
        private readonly IWebHostEnvironment _environment;

        public TenantsController(
            ITenantService tenantService,
            IOptions<PublicTenantDirectoryConfiguration> publicTenantDirectoryConfiguration,
            IWebHostEnvironment environment)
        {
            _tenantService = tenantService;
            _publicTenantDirectoryConfiguration = publicTenantDirectoryConfiguration.Value;
            _environment = environment;
        }

        [AllowAnonymous]
        [HttpGet("public")]
        [EnableRateLimiting(PublicTenantApiConsts.RateLimitPolicy)]
        [ProducesResponseType(typeof(IEnumerable<PublicTenantApiDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<IEnumerable<PublicTenantApiDto>>> GetPublicTenants(CancellationToken ct)
        {

            ApplyPublicResponseCacheHeaders();

            var tenants = await _tenantService.GetPublicTenantsAsync(ct);
            var result = tenants.ToTenantApiModel<List<PublicTenantApiDto>>();
            return Ok(result);
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<TenantApiDto>>> GetTenants([FromQuery] string? search, CancellationToken ct)
        {
            var tenants = await _tenantService.GetTenantsAsync(search, ct);
            var result = tenants.ToTenantApiModel<List<TenantApiDto>>();
            return Ok(result);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<TenantApiDto>> GetTenant(int id, CancellationToken ct)
        {
            var tenant = await _tenantService.GetTenantAsync(id, ct);
            if (tenant == null) return NotFound();
            return Ok(tenant.ToTenantApiModel<TenantApiDto>());
        }

        [HttpPost]
        public async Task<ActionResult<TenantApiDto>> CreateTenant([FromBody] TenantCreateApiDto model, CancellationToken ct)
        {
            var dto = model.ToTenantApiModel<TenantCreateDto>();
            dto.ConnectionSecrets = model.NormalizeConnectionSecrets(DefaultLegacyServiceKey);
            var created = await _tenantService.CreateTenantAsync(dto, ct);
            return Ok(created.ToTenantApiModel<TenantApiDto>());
        }

        [HttpPost("clone")]
        public async Task<ActionResult<TenantApiDto>> CloneTenant([FromBody] TenantCloneApiDto model, CancellationToken ct)
        {
            var dto = model.ToTenantApiModel<TenantCloneDto>();
            var created = await _tenantService.CloneTenantAsync(dto, ct);
            return Ok(created.ToTenantApiModel<TenantApiDto>());
        }

        [HttpPost("logo")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(TenantLogoUploadResultApiDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<TenantLogoUploadResultApiDto>> UploadTenantLogo([FromForm] TenantLogoUploadApiDto model, CancellationToken ct)
        {
            if (!ModelState.IsValid || model.File == null)
            {
                return ValidationProblem(ModelState);
            }

            var normalizedTenantKey = NormalizeTenantKey(model.TenantKey);
            if (string.IsNullOrWhiteSpace(normalizedTenantKey))
            {
                return BadRequest("Invalid tenant key.");
            }

            var extension = Path.GetExtension(model.File.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedLogoExtensions.Contains(extension))
            {
                return BadRequest("Invalid logo file type. Allowed: .png, .jpg, .jpeg, .webp");
            }

            if (model.File.Length <= 0 || model.File.Length > MaxLogoFileSizeBytes)
            {
                return BadRequest("Invalid logo file size. Maximum allowed is 2 MB.");
            }

            if (string.IsNullOrWhiteSpace(model.File.ContentType) || !model.File.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Invalid logo content type.");
            }

            var webRoot = string.IsNullOrWhiteSpace(_environment.WebRootPath)
                ? Path.Combine(AppContext.BaseDirectory, "wwwroot")
                : _environment.WebRootPath;

            var tenantLogoDirectory = Path.Combine(webRoot, "tenant-logos", normalizedTenantKey);
            Directory.CreateDirectory(tenantLogoDirectory);

            foreach (var existingFile in Directory.GetFiles(tenantLogoDirectory))
            {
                System.IO.File.Delete(existingFile);
            }

            var logoFileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var destinationPath = Path.Combine(tenantLogoDirectory, logoFileName);

            await using (var fileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await model.File.CopyToAsync(fileStream, ct);
            }

            var relativeLogoPath = $"/tenant-logos/{Uri.EscapeDataString(normalizedTenantKey)}/{logoFileName}";
            var absoluteLogoUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{relativeLogoPath}";

            return Ok(new TenantLogoUploadResultApiDto
            {
                LogoUrl = absoluteLogoUrl
            });
        }

        [HttpPut("{id:int}")]
        public async Task<ActionResult<TenantApiDto>> UpdateTenant(int id, [FromBody] TenantUpdateApiDto model, CancellationToken ct)
        {
            var dto = model.ToTenantApiModel<TenantUpdateDto>();
            dto.ConnectionSecrets = model.NormalizeConnectionSecrets(DefaultLegacyServiceKey);
            var updated = await _tenantService.UpdateTenantAsync(id, dto, ct);
            return Ok(updated.ToTenantApiModel<TenantApiDto>());
        }

        [HttpGet("{id:int}/admins")]
        public async Task<ActionResult<IEnumerable<TenantAdminApiDto>>> GetTenantAdmins(int id, CancellationToken ct)
        {
            var admins = await _tenantService.GetTenantAdminsAsync(id, ct);
            var result = admins.ToTenantApiModel<List<TenantAdminApiDto>>();
            return Ok(result);
        }

        [HttpPost("{id:int}/admins")]
        public async Task<IActionResult> AssignTenantAdmin(int id, [FromBody] TenantAdminAssignApiDto model, CancellationToken ct)
        {
            await _tenantService.AssignTenantAdminAsync(id, model.UserId, ct);
            return NoContent();
        }

        [HttpDelete("{id:int}/admins/{userId}")]
        public async Task<IActionResult> UnassignTenantAdmin(int id, string userId, CancellationToken ct)
        {
            await _tenantService.UnassignTenantAdminAsync(id, userId, ct);
            return NoContent();
        }

        private void ApplyPublicResponseCacheHeaders()
        {
            var cacheSeconds = Math.Max(0, _publicTenantDirectoryConfiguration.ResponseCacheSeconds);
            var headers = Response.GetTypedHeaders();

            if (cacheSeconds <= 0)
            {
                headers.CacheControl = new Microsoft.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoStore = true
                };
                return;
            }

            headers.CacheControl = new Microsoft.Net.Http.Headers.CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromSeconds(cacheSeconds)
            };

            Response.Headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;
        }

        private static string NormalizeTenantKey(string tenantKey)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
            {
                return string.Empty;
            }

            var normalized = tenantKey.Trim().ToLowerInvariant();
            var builder = new StringBuilder(normalized.Length);

            foreach (var character in normalized)
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }
    }
}
