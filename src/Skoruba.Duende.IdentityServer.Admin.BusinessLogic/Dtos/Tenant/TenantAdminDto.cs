namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant
{
    public class TenantAdminDto
    {
        public string UserId { get; set; } = string.Empty;

        public string UserName { get; set; } = string.Empty;

        public string? Email { get; set; }
    }
}
