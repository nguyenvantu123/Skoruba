namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant
{
    public class TenantRegistryLookupResultDto
    {
        public string TenantId { get; set; } = string.Empty;

        public string Identifier { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string SecretName { get; set; } = string.Empty;

        public Dictionary<string, string> ConnectionSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsActive { get; set; }
    }
}
