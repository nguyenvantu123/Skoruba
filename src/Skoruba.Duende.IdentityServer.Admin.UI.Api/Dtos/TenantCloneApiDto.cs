using System.ComponentModel.DataAnnotations;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Tenants
{
    public class TenantCloneApiDto : IValidatableObject
    {
        [Required]
        public int? SourceTenantId { get; set; }

        [Required]
        [MaxLength(64)]
        public string TenantKey { get; set; } = string.Empty;

        [Required]
        [MaxLength(256)]
        public string DisplayName { get; set; } = string.Empty;

        public Dictionary<string, string>? ConnectionSecrets { get; set; }

        [Url]
        [MaxLength(2048)]
        public string? RedirectUrl { get; set; }

        [Url]
        [MaxLength(2048)]
        public string? LogoUrl { get; set; }

        public bool? IsActive { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (!SourceTenantId.HasValue || SourceTenantId.Value <= 0)
            {
                yield return new ValidationResult(
                    "The SourceTenantId field is required.",
                    [nameof(SourceTenantId)]);
            }
        }
    }
}
