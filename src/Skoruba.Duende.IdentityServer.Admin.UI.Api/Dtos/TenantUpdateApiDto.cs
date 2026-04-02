using System.ComponentModel.DataAnnotations;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Tenants
{
    public class TenantUpdateApiDto : IValidatableObject
    {
        [Required]
        [MaxLength(256)]
        public string DisplayName { get; set; } = string.Empty;

        [MinLength(1)]
        public Dictionary<string, string> ConnectionSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Backward compatibility for older clients still posting the single-secret contract.
        public string? ConnectionStringSecretName { get; set; }

        [Url]
        [MaxLength(2048)]
        public string RedirectUrl { get; set; }

        [Url]
        [MaxLength(2048)]
        public string LogoUrl { get; set; }

        public bool IsActive { get; set; } = true;

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if ((ConnectionSecrets == null || ConnectionSecrets.Count == 0) &&
                string.IsNullOrWhiteSpace(ConnectionStringSecretName))
            {
                yield return new ValidationResult(
                    "The ConnectionSecrets field is required.",
                    [nameof(ConnectionSecrets)]);
            }
        }

        public Dictionary<string, string> NormalizeConnectionSecrets(string defaultServiceKey)
        {
            if (ConnectionSecrets != null && ConnectionSecrets.Count > 0)
            {
                return new Dictionary<string, string>(ConnectionSecrets, StringComparer.OrdinalIgnoreCase);
            }

            return string.IsNullOrWhiteSpace(ConnectionStringSecretName)
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [defaultServiceKey] = ConnectionStringSecretName.Trim()
                };
        }
    }
}
