using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Tenants
{
    public class TenantLogoUploadApiDto
    {
        [Required]
        [MaxLength(64)]
        public string TenantKey { get; set; } = string.Empty;

        [Required]
        public IFormFile? File { get; set; }
    }
}
