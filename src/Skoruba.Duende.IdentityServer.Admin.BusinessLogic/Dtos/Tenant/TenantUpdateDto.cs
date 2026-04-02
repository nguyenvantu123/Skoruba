namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant
{
    public class TenantUpdateDto
    {
        public string DisplayName { get; set; } = string.Empty;

        public Dictionary<string, string> ConnectionSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string RedirectUrl { get; set; }

        public string LogoUrl { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
