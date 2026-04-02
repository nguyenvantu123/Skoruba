namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant
{
    public class TenantCloneDto
    {
        public int SourceTenantId { get; set; }

        public string TenantKey { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public Dictionary<string, string>? ConnectionSecrets { get; set; }

        public string? RedirectUrl { get; set; }

        public string? LogoUrl { get; set; }

        public bool? IsActive { get; set; }
    }
}
