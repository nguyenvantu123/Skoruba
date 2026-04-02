namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Tenants
{
    public class TenantApiDto
    {
        public int Id { get; set; }

        public string TenantKey { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public bool IsActive { get; set; }

        public Dictionary<string, string> ConnectionSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string RedirectUrl { get; set; }

        public string LogoUrl { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
